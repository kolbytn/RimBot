using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using RimBot.Models;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimBot
{
    public static class BrainManager
    {
        private static readonly Dictionary<int, Brain> brains = new Dictionary<int, Brain>();
        private static readonly ConcurrentQueue<Action> mainThreadQueue = new ConcurrentQueue<Action>();
        private const float AgentCooldownSeconds = 30f;

        public static Brain GetBrain(int pawnId)
        {
            Brain brain;
            brains.TryGetValue(pawnId, out brain);
            return brain;
        }

        public static Pawn FindPawnById(int pawnId)
        {
            var colonists = Find.CurrentMap.mapPawns.FreeColonistsSpawned;
            foreach (var pawn in colonists)
            {
                if (pawn.thingIDNumber == pawnId)
                    return pawn;
            }
            return null;
        }

        private static bool IsPawnIdleOrWandering(Pawn pawn)
        {
            var jobDef = pawn.CurJobDef;
            if (jobDef == null)
                return true;
            return jobDef == JobDefOf.Wait
                || jobDef == JobDefOf.Wait_Wander
                || jobDef == JobDefOf.GotoWander
                || jobDef == JobDefOf.Wait_MaintainPosture;
        }

        public static void EnqueueMainThread(Action action)
        {
            mainThreadQueue.Enqueue(action);
        }

        public static void Tick()
        {
            while (mainThreadQueue.TryDequeue(out var action))
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    Log.Warning("[RimBot] Error in main thread queue: " + ex.Message);
                }
            }

            if (Current.ProgramState != ProgramState.Playing)
                return;
            if (Find.CurrentMap == null)
                return;

            SyncConfigColonists();
            SyncBrains();

            float now = Time.realtimeSinceStartup;
            foreach (var kvp in brains)
            {
                var brain = kvp.Value;
                if (!brain.IsIdle || brain.IsPaused)
                    continue;

                var pawn = FindPawnById(kvp.Key);
                if (pawn == null || !pawn.Spawned)
                    continue;

                if (!IsPawnIdleOrWandering(pawn))
                {
                    float sinceLastRun = now - brain.LastRunStartedAt;
                    if (sinceLastRun < AgentCooldownSeconds)
                        continue;
                }

                brain.RunAgentLoop();
            }
        }

        private static int lastProfileCount = -1;

        private static void SyncConfigColonists()
        {
            var comp = Current.Game.GetComponent<ColonyAssignmentComponent>();
            if (comp == null)
                return;

            var settings = RimBotMod.Settings;
            settings.EnsureProfilesLoaded();
            settings.AddDefaultProfilesIfEmpty();

            // Count enabled profiles
            var enabledProfiles = new List<AgentProfile>();
            foreach (var p in settings.profiles)
            {
                if (!string.IsNullOrEmpty(settings.GetApiKeyForProvider(p.Provider)))
                    enabledProfiles.Add(p);
            }

            int target = enabledProfiles.Count;

            // Detect dead config colonists — move from living to dead list
            var colonists = Find.CurrentMap.mapPawns.FreeColonistsSpawned;
            var aliveIds = new HashSet<int>();
            foreach (var pawn in colonists)
                aliveIds.Add(pawn.thingIDNumber);

            for (int i = comp.configPawnIds.Count - 1; i >= 0; i--)
            {
                int id = comp.configPawnIds[i];
                if (!aliveIds.Contains(id))
                {
                    comp.MarkConfigPawnDead(id);
                    ClearAssignment(comp, id);
                    Log.Message("[RimBot] Config colonist " + id + " died, marked as dead.");
                }
            }

            int deadConfig = comp.deadConfigPawnIds.Count;
            var map = Find.CurrentMap;

            // First run: claim existing scenario colonists as config pawns, adjust to target
            if (comp.configPawnIds.Count == 0 && deadConfig == 0 && target > 0)
            {
                colonists = map.mapPawns.FreeColonistsSpawned;
                int toClaim = Math.Min(colonists.Count, target);
                for (int i = 0; i < toClaim; i++)
                {
                    comp.AddConfigPawn(colonists[i].thingIDNumber);
                    Log.Message("[RimBot] Claimed scenario colonist " + colonists[i].LabelShort + " as config pawn");
                }

                // Remove excess scenario colonists
                if (colonists.Count > target)
                {
                    for (int i = colonists.Count - 1; i >= target; i--)
                    {
                        Log.Message("[RimBot] Removing excess scenario colonist " + colonists[i].LabelShort);
                        colonists[i].Destroy();
                    }
                }
                // Spawn more if scenario didn't provide enough
                else if (colonists.Count < target)
                {
                    int toSpawn = target - colonists.Count;
                    for (int i = 0; i < toSpawn; i++)
                    {
                        var request = new PawnGenerationRequest(
                            PawnKindDefOf.Colonist,
                            Faction.OfPlayer,
                            PawnGenerationContext.PlayerStarter);
                        var pawn = PawnGenerator.GeneratePawn(request);
                        GenSpawn.Spawn(pawn, CellFinder.RandomClosewalkCellNear(map.Center, map, 5), map);
                        comp.AddConfigPawn(pawn.thingIDNumber);
                        Log.Message("[RimBot] Spawned config colonist " + pawn.LabelShort);
                    }
                }
            }

            // Skip if profile count hasn't changed and we already have the right number
            int livingConfig = comp.configPawnIds.Count;
            if (target == lastProfileCount && livingConfig + deadConfig == target)
                return;
            lastProfileCount = target;

            // Slots available = target minus dead config pawns (dead ones stay dead)
            int slotsAvailable = target - deadConfig;
            if (slotsAvailable < 0)
                slotsAvailable = 0;

            // Spawn more config colonists if needed
            if (livingConfig < slotsAvailable)
            {
                int toSpawn = slotsAvailable - livingConfig;
                for (int i = 0; i < toSpawn; i++)
                {
                    var request = new PawnGenerationRequest(
                        PawnKindDefOf.Colonist,
                        Faction.OfPlayer,
                        PawnGenerationContext.PlayerStarter);
                    var pawn = PawnGenerator.GeneratePawn(request);
                    GenSpawn.Spawn(pawn, CellFinder.RandomClosewalkCellNear(map.Center, map, 5), map);
                    comp.AddConfigPawn(pawn.thingIDNumber);
                    Log.Message("[RimBot] Spawned config colonist " + pawn.LabelShort + " (id=" + pawn.thingIDNumber + ")");
                }
            }
            // Remove excess config colonists if profiles were reduced
            else if (livingConfig > slotsAvailable)
            {
                int toRemove = livingConfig - slotsAvailable;
                for (int i = 0; i < toRemove; i++)
                {
                    int removeId = comp.configPawnIds[comp.configPawnIds.Count - 1];
                    var pawn = FindPawnById(removeId);
                    if (pawn != null)
                    {
                        Log.Message("[RimBot] Removing config colonist " + pawn.LabelShort + " (profile removed)");
                        ClearAssignment(comp, removeId);
                        comp.RemoveConfigPawn(removeId);
                        pawn.Destroy();
                    }
                    else
                    {
                        comp.RemoveConfigPawn(removeId);
                    }
                }
            }

            // Auto-assign all config colonists to profiles
            var livingConfigPawns = new List<int>(comp.configPawnIds);
            for (int i = 0; i < livingConfigPawns.Count && i < enabledProfiles.Count; i++)
            {
                comp.SetAssignment(livingConfigPawns[i], enabledProfiles[i].Id);
            }
        }

        private static void ClearAssignment(ColonyAssignmentComponent comp, int pawnId)
        {
            comp.ClearAssignment(pawnId);
            if (brains.ContainsKey(pawnId))
            {
                Log.Message("[RimBot] Removed brain for pawn " + pawnId);
                brains.Remove(pawnId);
            }
        }

        private static void SyncBrains()
        {
            var settings = RimBotMod.Settings;
            settings.EnsureProfilesLoaded();
            settings.AddDefaultProfilesIfEmpty();

            var comp = Current.Game.GetComponent<ColonyAssignmentComponent>();
            if (comp == null)
                return;

            var colonists = Find.CurrentMap.mapPawns.FreeColonistsSpawned;
            var currentIds = new HashSet<int>();
            foreach (var pawn in colonists)
                currentIds.Add(pawn.thingIDNumber);

            // Remove brains for dead/gone pawns and clear their assignments
            var toRemove = new List<int>();
            foreach (var id in brains.Keys)
            {
                if (!currentIds.Contains(id))
                    toRemove.Add(id);
            }
            foreach (var id in toRemove)
            {
                Log.Message("[RimBot] Removed brain for pawn " + id);
                brains.Remove(id);
                comp.ClearAssignment(id);
            }

            // Get enabled profiles (those with API keys)
            var enabledProfiles = new List<AgentProfile>();
            foreach (var p in settings.profiles)
            {
                if (!string.IsNullOrEmpty(settings.GetApiKeyForProvider(p.Provider)))
                    enabledProfiles.Add(p);
            }

            foreach (var pawn in colonists)
            {
                int pawnId = pawn.thingIDNumber;
                string profileId = comp.GetAssignment(pawnId);

                // Auto-assign new colonists if enabled
                if (string.IsNullOrEmpty(profileId) && comp.autoAssignNewColonists && enabledProfiles.Count > 0)
                {
                    comp.AutoAssign(pawnId, enabledProfiles);
                    profileId = comp.GetAssignment(pawnId);
                }

                if (string.IsNullOrEmpty(profileId))
                {
                    // No assignment — remove brain if it exists
                    if (brains.ContainsKey(pawnId))
                    {
                        Log.Message("[RimBot] Removed brain for " + pawn.LabelShort + " (unassigned)");
                        brains.Remove(pawnId);
                    }
                    continue;
                }

                var profile = settings.GetProfileById(profileId);
                if (profile == null)
                {
                    // Profile was deleted — remove brain and clear assignment
                    if (brains.ContainsKey(pawnId))
                    {
                        Log.Message("[RimBot] Removed brain for " + pawn.LabelShort + " (profile deleted)");
                        brains.Remove(pawnId);
                    }
                    comp.ClearAssignment(pawnId);
                    continue;
                }

                string apiKey = settings.GetApiKeyForProvider(profile.Provider);
                if (string.IsNullOrEmpty(apiKey))
                {
                    // No API key for this provider — remove brain
                    if (brains.ContainsKey(pawnId))
                    {
                        Log.Message("[RimBot] Removed brain for " + pawn.LabelShort + " (no API key for " + profile.Provider + ")");
                        brains.Remove(pawnId);
                    }
                    continue;
                }

                Brain existing;
                if (brains.TryGetValue(pawnId, out existing))
                {
                    // Check if profile changed (different profile or model changed)
                    if (existing.ProfileId == profileId && existing.Model == profile.Model)
                        continue;

                    Log.Message("[RimBot] Recreating brain for " + pawn.LabelShort + " (profile/model changed)");
                    brains.Remove(pawnId);
                }

                var brain = new Brain(pawnId, pawn.LabelShort,
                    profile.Provider, profile.Model, apiKey, profileId);
                brains[pawnId] = brain;
                Log.Message("[RimBot] Created brain for " + pawn.LabelShort
                    + " (" + profile.Provider + ", " + profile.Model + ")");
            }
        }

    }
}
