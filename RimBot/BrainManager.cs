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
        private static float lastCaptureTime;
        private static bool captureInProgress;
        private const float IntervalSeconds = 20f;
        private static int nextAssignmentIndex;
        private static bool extraColonistsSpawned;

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
                    Log.Error("[RimBot] Error in main thread queue: " + ex.Message);
                }
            }

            if (Current.ProgramState != ProgramState.Playing)
                return;
            if (Find.CurrentMap == null)
                return;

            EnsureFiveColonists();
            SelectionTest.CheckAutoStart();
            ArchitectMode.CheckAutoStart();
            SyncBrains();

            if (captureInProgress)
                return;
            if (Time.realtimeSinceStartup - lastCaptureTime < IntervalSeconds)
                return;

            lastCaptureTime = Time.realtimeSinceStartup;
            CaptureAll();
        }

        private static void EnsureFiveColonists()
        {
            if (extraColonistsSpawned)
                return;
            extraColonistsSpawned = true;

            var map = Find.CurrentMap;
            var colonists = map.mapPawns.FreeColonistsSpawned;
            int needed = 5 - colonists.Count;

            if (needed <= 0)
                return;

            for (int i = 0; i < needed; i++)
            {
                var request = new PawnGenerationRequest(
                    PawnKindDefOf.Colonist,
                    Faction.OfPlayer,
                    PawnGenerationContext.PlayerStarter);
                var pawn = PawnGenerator.GeneratePawn(request);
                GenSpawn.Spawn(pawn, CellFinder.RandomClosewalkCellNear(map.Center, map, 5), map);
            }

            Log.Message("[RimBot] Spawned " + needed + " extra colonists (total target: 5).");
        }

        private static void CaptureAll()
        {
            var requests = new List<ScreenshotCapture.CaptureRequest>();
            var brainOrder = new List<Brain>();
            var pawnOrder = new List<Pawn>();

            foreach (var kvp in brains)
            {
                var pawn = FindPawnById(kvp.Key);
                if (pawn == null || !pawn.Spawned)
                    continue;
                if (!kvp.Value.IsIdle)
                    continue;

                requests.Add(new ScreenshotCapture.CaptureRequest
                {
                    CenterCell = pawn.Position,
                    CameraSize = 24f,
                    PixelSize = 512
                });
                brainOrder.Add(kvp.Value);
                pawnOrder.Add(pawn);
            }

            if (requests.Count == 0)
                return;

            captureInProgress = true;
            ScreenshotCapture.StartBatchCapture(requests, results =>
            {
                if (SelectionTest.IsRunning)
                {
                    Log.Message("[RimBot] Selection test cycle: capturing " + brainOrder.Count + " brains");
                    SelectionTest.ProcessCapture(brainOrder, pawnOrder, results);
                }
                else if (ArchitectMode.IsRunning)
                {
                    Log.Message("[RimBot] Architect mode cycle: " + brainOrder.Count + " brains");
                    ArchitectMode.ProcessCapture(brainOrder, pawnOrder, results);
                }
                else
                {
                    for (int i = 0; i < brainOrder.Count; i++)
                        brainOrder[i].SendToLLM(results[i]);
                }
                captureInProgress = false;
            });
        }

        private static List<ProviderAssignment> GetFixedAssignments(RimBotSettings settings)
        {
            var result = new List<ProviderAssignment>();

            // 1. Claude Haiku - text coordinates
            if (!string.IsNullOrEmpty(settings.anthropicApiKey))
            {
                result.Add(new ProviderAssignment
                {
                    Provider = LLMProviderType.Anthropic,
                    Model = "claude-haiku-4-5-20251001",
                    ApiKey = settings.anthropicApiKey,
                    Mode = MapSelectionMode.Coordinates
                });
            }

            // 2. Gemini Flash - text coordinates
            if (!string.IsNullOrEmpty(settings.googleApiKey))
            {
                result.Add(new ProviderAssignment
                {
                    Provider = LLMProviderType.Google,
                    Model = "gemini-3-flash-preview",
                    ApiKey = settings.googleApiKey,
                    Mode = MapSelectionMode.Coordinates
                });
            }

            // 3. Gemini Flash - image mask
            if (!string.IsNullOrEmpty(settings.googleApiKey))
            {
                result.Add(new ProviderAssignment
                {
                    Provider = LLMProviderType.Google,
                    Model = "gemini-3-flash-preview",
                    ApiKey = settings.googleApiKey,
                    Mode = MapSelectionMode.Mask
                });
            }

            // 4. GPT-5 Mini - text coordinates
            if (!string.IsNullOrEmpty(settings.openAIApiKey))
            {
                result.Add(new ProviderAssignment
                {
                    Provider = LLMProviderType.OpenAI,
                    Model = "gpt-5-mini",
                    ApiKey = settings.openAIApiKey,
                    Mode = MapSelectionMode.Coordinates
                });
            }

            // 5. GPT-5 Mini - image mask
            if (!string.IsNullOrEmpty(settings.openAIApiKey))
            {
                result.Add(new ProviderAssignment
                {
                    Provider = LLMProviderType.OpenAI,
                    Model = "gpt-5-mini",
                    ApiKey = settings.openAIApiKey,
                    Mode = MapSelectionMode.Mask
                });
            }

            return result;
        }

        private struct ProviderAssignment
        {
            public LLMProviderType Provider;
            public string Model;
            public string ApiKey;
            public MapSelectionMode Mode;
        }

        private static void SyncBrains()
        {
            var colonists = Find.CurrentMap.mapPawns.FreeColonistsSpawned;
            var currentIds = new HashSet<int>();

            foreach (var pawn in colonists)
                currentIds.Add(pawn.thingIDNumber);

            // Remove brains for colonists no longer present
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
            }

            // Assign fixed provider+mode combos to colonists
            var settings = RimBotMod.Settings;
            var assignments = GetFixedAssignments(settings);
            if (assignments.Count == 0)
                return;

            foreach (var pawn in colonists)
            {
                if (!brains.ContainsKey(pawn.thingIDNumber))
                {
                    var a = assignments[nextAssignmentIndex % assignments.Count];
                    nextAssignmentIndex++;
                    var brain = new Brain(pawn.thingIDNumber, pawn.LabelShort,
                        a.Provider, a.Model, a.ApiKey, a.Mode);
                    brains[pawn.thingIDNumber] = brain;
                    Log.Message("[RimBot] Created brain for " + pawn.LabelShort
                        + " (" + a.Provider + ", " + a.Model + ", " + a.Mode + ")");
                }
            }
        }

        private static Pawn FindPawnById(int pawnId)
        {
            var colonists = Find.CurrentMap.mapPawns.FreeColonistsSpawned;
            foreach (var pawn in colonists)
            {
                if (pawn.thingIDNumber == pawnId)
                    return pawn;
            }
            return null;
        }
    }
}
