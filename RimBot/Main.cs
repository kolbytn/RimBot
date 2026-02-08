using System;
using System.Collections.Generic;
using System.Net;
using System.Reflection;
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
        public static void Postfix()
        {
            LLMTestUtility.ProcessMainThreadQueue();
            BrainManager.Tick();
        }
    }
}
