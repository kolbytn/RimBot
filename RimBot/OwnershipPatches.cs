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
        private static Map capturedMap;
        private static IntVec3 capturedPos;
        private static int capturedOwner;

        public static void Prefix(Blueprint __instance)
        {
            capturedMap = __instance.Map;
            capturedPos = __instance.Position;
            capturedOwner = OwnershipTracker.Get(capturedMap)?.GetThingOwner(__instance.thingIDNumber) ?? -1;
        }

        public static void Postfix(bool __result, Thing createdThing)
        {
            if (!__result || capturedMap == null || capturedOwner < 0)
                return;

            var tracker = OwnershipTracker.Get(capturedMap);
            if (tracker == null)
                return;

            if (createdThing != null)
            {
                tracker.SetThingOwner(createdThing.thingIDNumber, capturedOwner);
                return;
            }

            // Fallback: search by position for a Frame
            var things = capturedPos.GetThingList(capturedMap);
            for (int i = 0; i < things.Count; i++)
            {
                if (things[i] is Frame frame)
                {
                    tracker.SetThingOwner(frame.thingIDNumber, capturedOwner);
                    return;
                }
            }

            Log.Warning("[RimBot] Ownership transfer failed: blueprint → frame at (" +
                capturedPos.x + "," + capturedPos.z + "), owner=" + capturedOwner);
        }
    }

    [HarmonyPatch(typeof(Frame), nameof(Frame.CompleteConstruction))]
    public static class FrameToBuildingPatch
    {
        private static int capturedId;
        private static Map capturedMap;
        private static IntVec3 capturedPos;
        private static int capturedOwner;
        private static BuildableDef capturedBuildDef;

        public static void Prefix(Frame __instance)
        {
            capturedId = __instance.thingIDNumber;
            capturedMap = __instance.Map;
            capturedPos = __instance.Position;
            capturedOwner = OwnershipTracker.Get(capturedMap)?.GetThingOwner(capturedId) ?? -1;
            capturedBuildDef = __instance.def.entityDefToBuild;
        }

        public static void Postfix(Pawn worker)
        {
            if (capturedMap == null)
                return;

            if (capturedOwner < 0)
            {
                // Frame had no owner — log for debugging
                string defName = capturedBuildDef?.defName ?? "unknown";
                Log.Warning("[RimBot] Unowned frame completed: " + defName + " at (" +
                    capturedPos.x + "," + capturedPos.z + ") by " +
                    (worker?.LabelShort ?? "unknown"));
                return;
            }

            var tracker = OwnershipTracker.Get(capturedMap);
            if (tracker == null)
                return;

            var things = capturedPos.GetThingList(capturedMap);

            if (capturedBuildDef != null)
            {
                for (int i = 0; i < things.Count; i++)
                {
                    if (things[i].def == capturedBuildDef)
                    {
                        tracker.SetThingOwner(things[i].thingIDNumber, capturedOwner);
                        AutoAssignBed(things[i]);
                        return;
                    }
                }
            }

            for (int i = 0; i < things.Count; i++)
            {
                var t = things[i];
                if (t.def.building != null && t.thingIDNumber != capturedId)
                {
                    tracker.SetThingOwner(t.thingIDNumber, capturedOwner);
                    AutoAssignBed(t);
                    return;
                }
            }

            Log.Warning("[RimBot] Ownership transfer failed: frame → building at (" +
                capturedPos.x + "," + capturedPos.z + "), owner=" + capturedOwner);
        }

        private static void AutoAssignBed(Thing t)
        {
            var bed = t as Building_Bed;
            if (bed != null && !bed.Medical)
            {
                var ownerPawn = BrainManager.FindPawnById(capturedOwner);
                if (ownerPawn != null)
                    ownerPawn.ownership.ClaimBedIfNonMedical(bed);
            }
        }
    }

    // --- Botched construction: Frame reverts to Blueprint ---

    [HarmonyPatch(typeof(Frame), "FailConstruction")]
    public static class FrameFailConstructionPatch
    {
        private static Map capturedMap;
        private static IntVec3 capturedPos;
        private static int capturedOwner;

        public static void Prefix(Frame __instance)
        {
            capturedMap = __instance.Map;
            capturedPos = __instance.Position;
            capturedOwner = OwnershipTracker.Get(capturedMap)?.GetThingOwner(__instance.thingIDNumber) ?? -1;
        }

        public static void Postfix()
        {
            if (capturedMap == null || capturedOwner < 0)
                return;

            var tracker = OwnershipTracker.Get(capturedMap);
            if (tracker == null)
                return;

            // Find the new blueprint at the same position
            var things = capturedPos.GetThingList(capturedMap);
            for (int i = 0; i < things.Count; i++)
            {
                if (things[i] is Blueprint blueprint)
                {
                    tracker.SetThingOwner(blueprint.thingIDNumber, capturedOwner);
                    Log.Message("[RimBot] Botched construction at (" + capturedPos.x + "," +
                        capturedPos.z + "): ownership preserved on new blueprint (owner=" + capturedOwner + ")");
                    return;
                }
            }

            Log.Warning("[RimBot] Botched construction at (" + capturedPos.x + "," +
                capturedPos.z + "): could not find new blueprint to transfer ownership (owner=" + capturedOwner + ")");
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
            if (pawn == null || t == null || pawn.Map == null)
                return true;
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
            if (pawn == null || pawn.Map == null)
                return true;
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
            if (pawn == null || t == null || pawn.Map == null)
                return true;
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
            if (pawn == null || pawn.Map == null)
                return true;
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

    // --- Item pickup logging: detect what bots pick up and from where ---

    [HarmonyPatch(typeof(Pawn_CarryTracker), nameof(Pawn_CarryTracker.TryStartCarry), new Type[] { typeof(Thing), typeof(int), typeof(bool) })]
    public static class ItemPickupLogPatch
    {
        public static void Postfix(Pawn_CarryTracker __instance, Thing item, int count, int __result)
        {
            if (__result <= 0) return;
            var pawn = __instance.pawn;
            if (pawn == null || pawn.Map == null) return;
            if (BrainManager.GetBrain(pawn.thingIDNumber) == null) return;

            var zone = pawn.Map.zoneManager.ZoneAt(item.Position);
            string zoneName = zone != null ? zone.label : "no zone";
            var tracker = OwnershipTracker.Get(pawn.Map);
            string ownerInfo = "";
            if (tracker != null)
            {
                int itemOwner = tracker.GetThingOwner(item.thingIDNumber);
                int zoneOwner = zone != null ? tracker.GetZoneOwner(zone.ID) : -1;
                if (itemOwner >= 0)
                    ownerInfo += ", itemOwner=" + (BrainManager.FindPawnById(itemOwner)?.LabelShort ?? itemOwner.ToString());
                if (zoneOwner >= 0)
                    ownerInfo += ", zoneOwner=" + (BrainManager.FindPawnById(zoneOwner)?.LabelShort ?? zoneOwner.ToString());
            }

            string job = pawn.CurJob != null ? pawn.CurJob.def.defName : "none";
            Log.Message("[RimBot] [PICKUP] " + pawn.LabelShort + " picked up " + count + "x " +
                item.LabelNoCount + " at (" + item.Position.x + "," + item.Position.z +
                ") from [" + zoneName + "] for job=" + job + ownerInfo);
        }
    }

    // --- Per-pawn forbidden: items in other bots' zones or owned by other bots appear forbidden ---

    [HarmonyPatch(typeof(ForbidUtility), nameof(ForbidUtility.IsForbidden), new Type[] { typeof(Thing), typeof(Pawn) })]
    public static class OwnershipForbidPatch
    {
        public static void Postfix(Thing t, Pawn pawn, ref bool __result)
        {
            // Already forbidden by vanilla — nothing to add
            if (__result) return;
            // Only apply to bots
            if (pawn == null || t == null || pawn.Map == null) return;
            if (BrainManager.GetBrain(pawn.thingIDNumber) == null) return;

            // Check thing-level ownership (e.g. distributed starting items)
            var tracker = OwnershipTracker.Get(pawn.Map);
            if (tracker != null)
            {
                int itemOwner = tracker.GetThingOwner(t.thingIDNumber);
                if (itemOwner >= 0 && itemOwner != pawn.thingIDNumber)
                {
                    __result = true;
                    return;
                }
            }

            // Check zone-level ownership (item sitting in another bot's stockpile)
            // Guard: position may be out of bounds if the thing is being carried/destroyed
            if (!t.Position.InBounds(pawn.Map)) return;
            var zone = pawn.Map.zoneManager.ZoneAt(t.Position);
            if (zone != null && tracker != null)
            {
                int zoneOwner = tracker.GetZoneOwner(zone.ID);
                if (zoneOwner >= 0 && zoneOwner != pawn.thingIDNumber)
                {
                    __result = true;
                }
            }
        }
    }

    // Prevent bots from hauling items to other bots' stockpile zones.
    // This is the single chokepoint for ALL haul destination selection — every WorkGiver
    // that generates haul jobs calls TryFindBestBetterStoreCellFor to pick the destination.
    [HarmonyPatch(typeof(StoreUtility), nameof(StoreUtility.TryFindBestBetterStoreCellFor))]
    public static class FilterHaulDestinationPatch
    {
        public static void Postfix(Thing t, Pawn carrier, ref bool __result, ref IntVec3 foundCell)
        {
            if (!__result) return;
            if (carrier == null || carrier.Map == null) return;
            if (BrainManager.GetBrain(carrier.thingIDNumber) == null) return;

            var zone = carrier.Map.zoneManager.ZoneAt(foundCell);
            if (zone == null) return;

            var tracker = OwnershipTracker.Get(carrier.Map);
            if (tracker == null) return;

            int zoneOwner = tracker.GetZoneOwner(zone.ID);
            if (zoneOwner >= 0 && zoneOwner != carrier.thingIDNumber)
            {
                __result = false;
                foundCell = IntVec3.Invalid;
            }
        }
    }

}
