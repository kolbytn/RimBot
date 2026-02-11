using System;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimBot
{
    // --- Ownership transfer: Blueprint → Frame → Building ---

    [HarmonyPatch(typeof(Blueprint), nameof(Blueprint.TryReplaceWithSolidThing))]
    public static class BlueprintToFramePatch
    {
        private static int capturedId;
        private static Map capturedMap;
        private static int capturedOwner;

        public static void Prefix(Blueprint __instance)
        {
            capturedId = __instance.thingIDNumber;
            capturedMap = __instance.Map;
            // Capture owner now — ThingDestroyPatch will remove it from the dict during this method
            capturedOwner = OwnershipTracker.Get(capturedMap)?.GetThingOwner(capturedId) ?? -1;
        }

        public static void Postfix(bool __result, Thing createdThing)
        {
            if (!__result || createdThing == null || capturedMap == null || capturedOwner < 0)
                return;
            OwnershipTracker.Get(capturedMap)?.SetThingOwner(createdThing.thingIDNumber, capturedOwner);
        }
    }

    [HarmonyPatch(typeof(Frame), nameof(Frame.CompleteConstruction))]
    public static class FrameToBuildingPatch
    {
        private static int capturedId;
        private static Map capturedMap;
        private static IntVec3 capturedPos;
        private static int capturedOwner;

        public static void Prefix(Frame __instance)
        {
            capturedId = __instance.thingIDNumber;
            capturedMap = __instance.Map;
            capturedPos = __instance.Position;
            capturedOwner = OwnershipTracker.Get(capturedMap)?.GetThingOwner(capturedId) ?? -1;
        }

        public static void Postfix(Pawn worker)
        {
            if (capturedMap == null || capturedOwner < 0)
                return;

            var tracker = OwnershipTracker.Get(capturedMap);
            if (tracker == null)
                return;

            // Find the newly built thing at the captured position
            var things = capturedPos.GetThingList(capturedMap);
            for (int i = 0; i < things.Count; i++)
            {
                var t = things[i];
                if (t.def.building != null && t.thingIDNumber != capturedId)
                {
                    tracker.SetThingOwner(t.thingIDNumber, capturedOwner);

                    // Auto-assign bed to building pawn
                    var bed = t as Building_Bed;
                    if (bed != null && !bed.Medical)
                    {
                        var ownerPawn = BrainManager.FindPawnById(capturedOwner);
                        if (ownerPawn != null)
                            ownerPawn.ownership.ClaimBedIfNonMedical(bed);
                    }
                    return;
                }
            }
        }
    }

    // --- Cleanup patches ---

    [HarmonyPatch(typeof(Thing), nameof(Thing.Destroy))]
    public static class ThingDestroyPatch
    {
        public static void Prefix(Thing __instance)
        {
            if (__instance.Map != null)
                OwnershipTracker.Get(__instance.Map)?.RemoveThing(__instance.thingIDNumber);
        }
    }

    [HarmonyPatch(typeof(Zone), "Delete", new Type[] { })]
    public static class ZoneDeletePatch
    {
        public static void Prefix(Zone __instance)
        {
            var map = __instance.zoneManager?.map;
            if (map != null)
                OwnershipTracker.Get(map)?.RemoveZone(__instance.ID);
        }
    }

    [HarmonyPatch(typeof(DesignationManager), nameof(DesignationManager.RemoveDesignation))]
    public static class DesignationRemovePatch
    {
        public static void Prefix(Designation des)
        {
            if (des?.designationManager?.map != null)
                OwnershipTracker.Get(des.designationManager.map)?.RemoveDesignation(des);
        }
    }

    // --- Job filtering: prevent bots from working on other bots' stuff ---

    public static class OwnershipFilterHelper
    {
        /// <summary>
        /// Returns true if the pawn is allowed to work on this thing (not owned by another bot).
        /// </summary>
        public static bool AllowJobOnThing(Pawn pawn, Thing t)
        {
            if (BrainManager.GetBrain(pawn.thingIDNumber) == null)
                return true; // not a bot — always allowed
            int owner = OwnershipTracker.Get(pawn.Map)?.GetThingOwner(t.thingIDNumber) ?? -1;
            return owner < 0 || owner == pawn.thingIDNumber;
        }

        /// <summary>
        /// Returns true if the pawn is allowed to work on this cell's zone (not owned by another bot).
        /// </summary>
        public static bool AllowJobOnZoneCell(Pawn pawn, IntVec3 c)
        {
            if (BrainManager.GetBrain(pawn.thingIDNumber) == null)
                return true;
            var zone = pawn.Map.zoneManager.ZoneAt(c);
            if (zone == null)
                return true;
            int owner = OwnershipTracker.Get(pawn.Map)?.GetZoneOwner(zone.ID) ?? -1;
            return owner < 0 || owner == pawn.thingIDNumber;
        }

        /// <summary>
        /// Returns true if the pawn is allowed to act on a designation on this thing.
        /// </summary>
        public static bool AllowDesignationOnThing(Pawn pawn, Thing t, DesignationDef desDef)
        {
            if (BrainManager.GetBrain(pawn.thingIDNumber) == null)
                return true;
            var des = pawn.Map.designationManager.DesignationOn(t, desDef);
            if (des == null)
                return true;
            int owner = OwnershipTracker.Get(pawn.Map)?.GetDesignationOwner(des) ?? -1;
            return owner < 0 || owner == pawn.thingIDNumber;
        }

        /// <summary>
        /// Returns true if the pawn is allowed to act on a designation at this cell.
        /// </summary>
        public static bool AllowDesignationAtCell(Pawn pawn, IntVec3 c, DesignationDef desDef)
        {
            if (BrainManager.GetBrain(pawn.thingIDNumber) == null)
                return true;
            var des = pawn.Map.designationManager.DesignationAt(c, desDef);
            if (des == null)
                return true;
            int owner = OwnershipTracker.Get(pawn.Map)?.GetDesignationOwner(des) ?? -1;
            return owner < 0 || owner == pawn.thingIDNumber;
        }
    }

    // Prevent bots from delivering resources to other bots' blueprints
    [HarmonyPatch(typeof(WorkGiver_ConstructDeliverResourcesToBlueprints), "JobOnThing")]
    public static class FilterConstructDeliverPatch
    {
        public static bool Prefix(Pawn pawn, Thing t, ref Job __result)
        {
            if (!OwnershipFilterHelper.AllowJobOnThing(pawn, t))
            {
                __result = null;
                return false;
            }
            return true;
        }
    }

    // Prevent bots from delivering resources to other bots' frames
    [HarmonyPatch(typeof(WorkGiver_ConstructDeliverResourcesToFrames), "JobOnThing")]
    public static class FilterConstructDeliverToFramesPatch
    {
        public static bool Prefix(Pawn pawn, Thing t, ref Job __result)
        {
            if (!OwnershipFilterHelper.AllowJobOnThing(pawn, t))
            {
                __result = null;
                return false;
            }
            return true;
        }
    }

    // Prevent bots from finishing other bots' frames
    [HarmonyPatch(typeof(WorkGiver_ConstructFinishFrames), "JobOnThing")]
    public static class FilterConstructFinishPatch
    {
        public static bool Prefix(Pawn pawn, Thing t, ref Job __result)
        {
            if (!OwnershipFilterHelper.AllowJobOnThing(pawn, t))
            {
                __result = null;
                return false;
            }
            return true;
        }
    }

    // Prevent bots from sowing in other bots' growing zones
    [HarmonyPatch(typeof(WorkGiver_GrowerSow), "JobOnCell")]
    public static class FilterGrowerSowPatch
    {
        public static bool Prefix(Pawn pawn, IntVec3 c, ref Job __result)
        {
            if (!OwnershipFilterHelper.AllowJobOnZoneCell(pawn, c))
            {
                __result = null;
                return false;
            }
            return true;
        }
    }

    // Prevent bots from harvesting in other bots' growing zones
    [HarmonyPatch(typeof(WorkGiver_GrowerHarvest), "HasJobOnCell")]
    public static class FilterGrowerHarvestPatch
    {
        public static bool Prefix(Pawn pawn, IntVec3 c, ref bool __result)
        {
            if (!OwnershipFilterHelper.AllowJobOnZoneCell(pawn, c))
            {
                __result = false;
                return false;
            }
            return true;
        }
    }

    // Prevent bots from mining other bots' designations
    [HarmonyPatch(typeof(WorkGiver_Miner), "JobOnThing")]
    public static class FilterMinerPatch
    {
        public static bool Prefix(Pawn pawn, Thing t, ref Job __result)
        {
            if (!OwnershipFilterHelper.AllowDesignationAtCell(pawn, t.Position, DesignationDefOf.Mine))
            {
                __result = null;
                return false;
            }
            return true;
        }
    }

    // Prevent bots from cutting/harvesting plants designated by other bots
    [HarmonyPatch(typeof(WorkGiver_PlantsCut), "JobOnThing")]
    public static class FilterPlantsCutPatch
    {
        public static bool Prefix(Pawn pawn, Thing t, ref Job __result)
        {
            if (!OwnershipFilterHelper.AllowDesignationOnThing(pawn, t, DesignationDefOf.CutPlant)
                || !OwnershipFilterHelper.AllowDesignationOnThing(pawn, t, DesignationDefOf.HarvestPlant))
            {
                __result = null;
                return false;
            }
            return true;
        }
    }

    // Prevent bots from deconstructing other bots' designated buildings
    [HarmonyPatch(typeof(WorkGiver_Deconstruct), "HasJobOnThing")]
    public static class FilterDeconstructPatch
    {
        public static bool Prefix(Pawn pawn, Thing t, ref bool __result)
        {
            if (!OwnershipFilterHelper.AllowDesignationOnThing(pawn, t, DesignationDefOf.Deconstruct))
            {
                __result = false;
                return false;
            }
            return true;
        }
    }

    // Prevent bots from hunting animals designated by other bots
    [HarmonyPatch(typeof(WorkGiver_HunterHunt), "HasJobOnThing")]
    public static class FilterHunterHuntPatch
    {
        public static bool Prefix(Pawn pawn, Thing t, ref bool __result)
        {
            if (!OwnershipFilterHelper.AllowDesignationOnThing(pawn, t, DesignationDefOf.Hunt))
            {
                __result = false;
                return false;
            }
            return true;
        }
    }

    // Prevent bots from taming animals designated by other bots
    [HarmonyPatch(typeof(WorkGiver_Tame), "JobOnThing")]
    public static class FilterTamePatch
    {
        public static bool Prefix(Pawn pawn, Thing t, ref Job __result)
        {
            if (!OwnershipFilterHelper.AllowDesignationOnThing(pawn, t, DesignationDefOf.Tame))
            {
                __result = null;
                return false;
            }
            return true;
        }
    }

    // Prevent bots from smoothing floors designated by other bots
    [HarmonyPatch(typeof(WorkGiver_ConstructSmoothFloor), "JobOnCell")]
    public static class FilterSmoothFloorPatch
    {
        public static bool Prefix(Pawn pawn, IntVec3 c, ref Job __result)
        {
            if (!OwnershipFilterHelper.AllowDesignationAtCell(pawn, c, DesignationDefOf.SmoothFloor))
            {
                __result = null;
                return false;
            }
            return true;
        }
    }

    // Prevent bots from smoothing walls designated by other bots
    [HarmonyPatch(typeof(WorkGiver_ConstructSmoothWall), "HasJobOnCell")]
    public static class FilterSmoothWallPatch
    {
        public static bool Prefix(Pawn pawn, IntVec3 c, ref bool __result)
        {
            if (!OwnershipFilterHelper.AllowDesignationAtCell(pawn, c, DesignationDefOf.SmoothWall))
            {
                __result = false;
                return false;
            }
            return true;
        }
    }

    // Prevent bots from stripping corpses/pawns designated by other bots
    [HarmonyPatch(typeof(WorkGiver_Strip), "HasJobOnThing")]
    public static class FilterStripPatch
    {
        public static bool Prefix(Pawn pawn, Thing t, ref bool __result)
        {
            if (!OwnershipFilterHelper.AllowDesignationOnThing(pawn, t, DesignationDefOf.Strip))
            {
                __result = false;
                return false;
            }
            return true;
        }
    }

    // Prevent bots from uninstalling buildings designated by other bots
    [HarmonyPatch(typeof(WorkGiver_Uninstall), "HasJobOnThing")]
    public static class FilterUninstallPatch
    {
        public static bool Prefix(Pawn pawn, Thing t, ref bool __result)
        {
            if (!OwnershipFilterHelper.AllowDesignationOnThing(pawn, t, DesignationDefOf.Uninstall))
            {
                __result = false;
                return false;
            }
            return true;
        }
    }

    // Prevent bots from hauling to other bots' stockpile zones
    [HarmonyPatch(typeof(WorkGiver_HaulGeneral), "JobOnThing")]
    public static class FilterHaulGeneralPatch
    {
        public static void Postfix(Pawn pawn, ref Job __result)
        {
            if (__result == null) return;
            if (BrainManager.GetBrain(pawn.thingIDNumber) == null) return;
            if (!__result.targetB.IsValid) return;

            if (!OwnershipFilterHelper.AllowJobOnZoneCell(pawn, __result.targetB.Cell))
                __result = null;
        }
    }

}
