using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using Verse;
using HarmonyLib;
using RimWorld;

namespace RimBot
{
    [HarmonyPatch(typeof(TickManager), nameof(TickManager.DoSingleTick))]
    public static class TickManagerPatch
    {
        private static int lastInitMapId = -1;
        private static int speedForceTicksLeft;
        private static bool prefsApplied;

        public static void Postfix(TickManager __instance)
        {
            if (Current.ProgramState == ProgramState.Playing && Find.CurrentMap != null)
            {
                int mapId = Find.CurrentMap.uniqueID;
                if (mapId != lastInitMapId)
                {
                    lastInitMapId = mapId;
                    speedForceTicksLeft = 120; // keep forcing for ~2 seconds
                    MetricsTracker.Reset();
                    UnforbidAll(Find.CurrentMap);
                    EnableAllWork(Find.CurrentMap);
                }
                if (speedForceTicksLeft > 0)
                {
                    __instance.CurTimeSpeed = TimeSpeed.Superfast;
                    speedForceTicksLeft--;
                }
            }

            if (!prefsApplied)
            {
                prefsApplied = true;
                try
                {
                    var dataField = typeof(Prefs).GetField("data", BindingFlags.Static | BindingFlags.NonPublic);
                    if (dataField != null)
                    {
                        var prefsData = dataField.GetValue(null) as PrefsData;
                        if (prefsData != null)
                        {
                            prefsData.openLogOnWarnings = false;
                            Log.Message("[RimBot] Disabled log auto-open.");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning("[RimBot] Could not disable log auto-open: " + ex.Message);
                }
            }

            BrainManager.Tick();
        }

        /// <summary>Thing IDs that were forbidden at map load (starting equipment).</summary>
        public static HashSet<int> StartingItemIds { get; private set; }

        private static void UnforbidAll(Map map)
        {
            int count = 0;
            StartingItemIds = new HashSet<int>();
            foreach (var thing in map.listerThings.AllThings)
            {
                var comp = thing.TryGetComp<CompForbiddable>();
                if (comp != null && comp.Forbidden)
                {
                    comp.Forbidden = false;
                    if (thing.def.category == ThingCategory.Item)
                        StartingItemIds.Add(thing.thingIDNumber);
                    count++;
                }
            }
            Log.Message("[RimBot] Unforbade " + count + " items on map.");
        }

        private static void EnableAllWork(Map map)
        {
            foreach (var pawn in map.mapPawns.FreeColonistsSpawned)
            {
                if (pawn.workSettings == null)
                    continue;
                foreach (var workType in DefDatabase<WorkTypeDef>.AllDefs)
                {
                    if (!pawn.WorkTypeIsDisabled(workType) && pawn.workSettings.GetPriority(workType) == 0)
                        pawn.workSettings.SetPriority(workType, 3);
                }
            }
            Log.Message("[RimBot] Enabled all work priorities for colonists.");
        }
    }

    [HarmonyPatch(typeof(PrefsData), nameof(PrefsData.Apply))]
    public static class RunInBackgroundPatch
    {
        public static void Postfix()
        {
            Application.runInBackground = true;
        }
    }

    /// <summary>Remove all work type restrictions for player colonists so every pawn can do every job.</summary>
    [HarmonyPatch(typeof(Pawn), nameof(Pawn.CombinedDisabledWorkTags), MethodType.Getter)]
    public static class DisableWorkRestrictionsPatch
    {
        public static void Postfix(Pawn __instance, ref WorkTags __result)
        {
            if (__instance.Faction != null && __instance.Faction.IsPlayer && __instance.RaceProps.Humanlike)
                __result = WorkTags.None;
        }
    }

    /// <summary>Downgrade specific harmless errors to warnings so they don't force the dev log open.</summary>
    [HarmonyPatch(typeof(Log), nameof(Log.Error))]
    public static class SuppressHarmlessErrorsPatch
    {
        public static bool Prefix(string text)
        {
            if (text != null && text.Contains("zone-incompatible"))
            {
                Log.Warning(text);
                return false;
            }
            return true;
        }
    }

    /// <summary>Track research project completion.</summary>
    [HarmonyPatch(typeof(ResearchManager), nameof(ResearchManager.FinishProject))]
    public static class ResearchFinishPatch
    {
        public static void Postfix(ResearchProjectDef proj)
        {
            if (proj != null)
                MetricsTracker.RecordResearchComplete(proj.label);
        }
    }

    /// <summary>Track when blueprints are replaced by completed buildings.</summary>
    [HarmonyPatch(typeof(Frame), nameof(Frame.CompleteConstruction))]
    public static class BuildingCompletePatch
    {
        public static void Prefix(Frame __instance, Pawn worker)
        {
            if (__instance?.def?.entityDefToBuild == null) return;
            if (worker?.Faction == null || !worker.Faction.IsPlayer) return;

            string defName = __instance.def.entityDefToBuild.defName;
            MetricsTracker.RecordBuildingComplete(defName, worker.LabelShort);
        }
    }

    /// <summary>Track when items are crafted at workbenches.</summary>
    [HarmonyPatch(typeof(GenRecipe), nameof(GenRecipe.MakeRecipeProducts))]
    public static class CraftingCompletePatch
    {
        public static void Postfix(RecipeDef recipeDef, Pawn worker, System.Collections.IEnumerable __result)
        {
            if (recipeDef == null || worker == null) return;
            if (worker.Faction == null || !worker.Faction.IsPlayer) return;

            // RecipeDef.products tells us what will be produced
            if (recipeDef.products != null)
            {
                foreach (var product in recipeDef.products)
                {
                    if (product.thingDef != null)
                        MetricsTracker.RecordItemCrafted(product.thingDef.defName, product.count, worker.LabelShort);
                }
            }
        }
    }

    /// <summary>Track colonist deaths.</summary>
    [HarmonyPatch(typeof(Pawn), nameof(Pawn.Kill))]
    public static class ColonistDeathPatch
    {
        public static void Prefix(Pawn __instance, DamageInfo? dinfo)
        {
            if (__instance?.Faction == null || !__instance.Faction.IsPlayer) return;
            if (!__instance.RaceProps.Humanlike) return;

            string cause = "unknown";
            if (dinfo.HasValue && dinfo.Value.Def != null)
                cause = dinfo.Value.Def.label;
            else if (__instance.health?.hediffSet?.hediffs != null)
            {
                // Check for disease / starvation / hypothermia etc.
                foreach (var hediff in __instance.health.hediffSet.hediffs)
                {
                    if (hediff.CurStage?.lifeThreatening == true)
                    {
                        cause = hediff.def.label;
                        break;
                    }
                }
            }

            MetricsTracker.RecordColonistDeath(__instance.LabelShort, cause);
        }
    }

    /// <summary>Set RimBot difficulty and Phoebe storyteller in quicktest game setup, before the game is created.</summary>
    [HarmonyPatch(typeof(Root_Play), "SetupForQuickTestPlay")]
    public static class QuickTestDifficultyPatch
    {
        public static void Postfix()
        {
            try
            {
                var rimBotDiff = DefDatabase<DifficultyDef>.GetNamed("RimBot", false);
                if (rimBotDiff == null) return;

                var initData = Find.GameInitData;
                if (initData != null)
                {
                    var traverse = HarmonyLib.Traverse.Create(initData);
                    traverse.Field("difficulty").SetValue(new Difficulty(rimBotDiff));

                    // Use Phoebe Chillax — most passive storyteller
                    var phoebe = DefDatabase<StorytellerDef>.GetNamed("Phoebe", false);
                    if (phoebe != null)
                    {
                        traverse.Field("storyTeller").SetValue(phoebe);
                        Log.Message("[RimBot] Quicktest: set storyteller to Phoebe Chillax.");
                    }

                    Log.Message("[RimBot] Quicktest: set difficulty to RimBot.");
                }
            }
            catch (Exception ex)
            {
                Log.Warning("[RimBot] Could not set quicktest difficulty: " + ex.Message);
            }
        }
    }
}
