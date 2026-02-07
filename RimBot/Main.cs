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
        }
    }

    [HarmonyPatch(typeof(TickManager), nameof(TickManager.DoSingleTick))]
    public static class TickManagerPatch
    {
        public static void Postfix()
        {
            LLMTestUtility.ProcessMainThreadQueue();
            ScreenshotLoop.Tick();
        }
    }
}
