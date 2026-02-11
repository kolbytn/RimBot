using System;
using System.Collections.Generic;
using System.Net;
using System.Reflection;
using UnityEngine;
using Verse;
using HarmonyLib;
using RimWorld;

namespace RimBot
{
    [StaticConstructorOnStartup]
    public static class ModEntryPoint
    {
        static ModEntryPoint()
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;

            Log.Message("[RimBot] Initialization started.");

            var harmony = new Harmony("com.kolbywan.rimbot");
            harmony.PatchAll(Assembly.GetExecutingAssembly());

            Log.Message("[RimBot] Harmony patches applied.");

            RegisterITab();
        }

        private static void RegisterITab()
        {
            var tabType = typeof(ITab_RimBotHistory);
            int count = 0;

            foreach (var def in DefDatabase<ThingDef>.AllDefs)
            {
                if (def.race == null || !def.race.Humanlike)
                    continue;

                if (def.inspectorTabs == null)
                    def.inspectorTabs = new List<Type>();
                if (def.inspectorTabsResolved == null)
                    def.inspectorTabsResolved = new List<InspectTabBase>();

                if (!def.inspectorTabs.Contains(tabType))
                {
                    def.inspectorTabs.Add(tabType);
                    def.inspectorTabsResolved.Add(InspectTabManager.GetSharedInstance(tabType));
                    count++;
                }
            }

            Log.Message("[RimBot] Registered ITab_RimBotHistory on " + count + " humanlike ThingDefs.");
        }
    }

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

        private static void UnforbidAll(Map map)
        {
            int count = 0;
            foreach (var thing in map.listerThings.AllThings)
            {
                var comp = thing.TryGetComp<CompForbiddable>();
                if (comp != null && comp.Forbidden)
                {
                    comp.Forbidden = false;
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
}
