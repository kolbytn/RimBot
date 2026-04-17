using System;
using System.Collections.Generic;
using Verse;
using Verse.AI;
using HarmonyLib;
using RimWorld;

namespace RimBot
{
    // ===========================================
    //  Peaceful-mode patches (threats & breaks)
    // ===========================================

    // Temporary until we better handle defense
    /// <summary>Block all threat incidents (raids, manhunter packs, infestations, mech clusters, etc.).</summary>
    [HarmonyPatch(typeof(Storyteller), nameof(Storyteller.TryFire))]
    public static class BlockThreatIncidentsPatch
    {
        public static bool Prefix(FiringIncident fi)
        {
            if (fi?.def?.category == null) return true;
            if (fi.def.category == IncidentCategoryDefOf.ThreatBig ||
                fi.def.category == IncidentCategoryDefOf.ThreatSmall)
            {
                Log.Message("[RimBot] Blocked threat incident: " + fi.def.defName);
                return false;
            }
            return true;
        }
    }

    /// <summary>Block all mental breaks for player colonists.</summary>
    [HarmonyPatch(typeof(MentalStateHandler), nameof(MentalStateHandler.TryStartMentalState))]
    public static class BlockMentalBreaksPatch
    {
        public static bool Prefix(MentalStateHandler __instance, MentalStateDef stateDef, ref bool __result)
        {
            if (stateDef == null) return true;
            var pawn = Traverse.Create(__instance).Field("pawn").GetValue<Pawn>();
            if (pawn?.Faction == null || !pawn.Faction.IsPlayer) return true;

            Log.Message("[RimBot] Blocked mental state " + stateDef.defName + " on " + pawn.LabelShort);
            __result = false;
            return false;
        }
    }

    // ===========================================
    //  Stat boosts (construction, mining, rest, medical)
    // ===========================================

    /// <summary>
    /// Boost survival-related stats for player colonists to shift focus toward social simulation.
    /// Construction 2x speed, 100% success. Mining 1.5x speed. Rest recovery 2x. Healing/immunity 2x.
    /// </summary>
    [HarmonyPatch(typeof(StatExtension), "GetStatValue", new Type[] { typeof(Thing), typeof(StatDef), typeof(bool), typeof(int) })]
    public static class StatBoostPatch
    {
        public static void Postfix(Thing thing, StatDef stat, ref float __result)
        {
            // Pawn-specific boosts: only for player colonists
            if (thing is Pawn pawn && pawn.Faction != null && pawn.Faction.IsPlayer)
            {
                if (stat == StatDefOf.ConstructionSpeed)
                    __result *= 2f;
                else if (stat == StatDefOf.ConstructSuccessChance)
                    __result = 1f;
                else if (stat == StatDefOf.MiningSpeed)
                    __result *= 1.5f;
                else if (stat == StatDefOf.RestRateMultiplier)
                    __result *= 2f;
                else if (stat == StatDefOf.ImmunityGainSpeed)
                    __result *= 2f;
                else if (stat == StatDefOf.InjuryHealingFactor)
                    __result *= 2f;
            }

            // All things: disable deterioration
            if (stat == StatDefOf.DeteriorationRate)
                __result = 0f;
        }
    }

    // ===========================================
    //  Construction cost reduction (50%)
    // ===========================================

    /// <summary>
    /// Halve construction material costs by modifying ThingDef.costList at startup.
    /// Applied once rather than per-call to avoid mutating shared state.
    /// </summary>
    [StaticConstructorOnStartup]
    public static class ConstructionCostReduction
    {
        static ConstructionCostReduction()
        {
            int count = 0;
            foreach (var def in DefDatabase<ThingDef>.AllDefs)
            {
                if (def.costList == null) continue;
                // Only reduce costs for buildable things (buildings, floors, etc.)
                if (!def.BuildableByPlayer) continue;
                foreach (var cost in def.costList)
                {
                    int original = cost.count;
                    cost.count = Math.Max(1, cost.count / 2);
                    if (cost.count != original) count++;
                }
                // Also halve stuff cost (e.g. walls that cost X wood/stone)
                if (def.costStuffCount > 0)
                    def.costStuffCount = Math.Max(1, def.costStuffCount / 2);
            }
            Log.Message("[RimBot] Halved construction costs on " + count + " material entries.");
        }
    }

    // ===========================================
    //  Faster plant growth (1.5x)
    // ===========================================

    /// <summary>Speed up plant growth by 50% so farming cycles are shorter.</summary>
    [HarmonyPatch(typeof(Plant), nameof(Plant.GrowthRate), MethodType.Getter)]
    public static class PlantGrowthSpeedPatch
    {
        public static void Postfix(ref float __result)
        {
            __result *= 1.5f;
        }
    }
}
