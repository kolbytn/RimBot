using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using RimWorld;
using Verse;

namespace RimBot.Tools
{
    public class ArchitectOrdersTool : ITool
    {
        // Static context for ownership tracking (safe: main-thread only execution)
        private static int currentPawnId;
        private static Map currentMap;

        public string Name => "architect_orders";

        public ToolDefinition GetDefinition()
        {
            return new ToolDefinition
            {
                Name = Name,
                Description = "Issue work orders to colonists. Coordinates are relative to your position (0,0). " +
                    "Colonists will carry out designated work automatically.",
                ParametersJson = "{\"type\":\"object\",\"properties\":{" +
                    "\"order\":{\"type\":\"string\"," +
                    "\"enum\":[\"mine\",\"harvest\",\"cut_plant\",\"haul\",\"deconstruct\",\"hunt\"," +
                    "\"tame\",\"smooth_floor\",\"smooth_wall\",\"cancel\",\"claim\",\"strip\",\"uninstall\"]," +
                    "\"description\":\"The order type\"}," +
                    "\"coordinates\":{\"type\":\"array\",\"items\":{\"type\":\"object\"," +
                    "\"properties\":{\"x\":{\"type\":\"integer\"},\"z\":{\"type\":\"integer\"}}," +
                    "\"required\":[\"x\",\"z\"]},\"description\":\"Relative coordinates to designate\"}}" +
                    ",\"required\":[\"order\",\"coordinates\"]}"
            };
        }

        public void Execute(ToolCall call, ToolContext context, Action<ToolResult> onComplete)
        {
            var coordsArray = call.Arguments["coordinates"] as JArray;
            if (coordsArray == null || coordsArray.Count == 0)
            {
                onComplete(new ToolResult
                {
                    ToolCallId = call.Id,
                    ToolName = Name,
                    Success = false,
                    Content = "No coordinates provided."
                });
                return;
            }

            string order = call.Arguments["order"]?.Value<string>();
            if (string.IsNullOrEmpty(order))
            {
                onComplete(new ToolResult
                {
                    ToolCallId = call.Id,
                    ToolName = Name,
                    Success = false,
                    Content = "order parameter is required."
                });
                return;
            }

            Log.Message("[RimBot] [AGENT] [" + context.PawnLabel + "] architect_orders(" + order +
                ", " + coordsArray.Count + " cells)");

            var map = context.Map;
            var observerPos = context.PawnPosition;
            var dm = map.designationManager;
            currentPawnId = context.PawnId;
            currentMap = map;

            int designated = 0;
            int skipped = 0;
            var skipReasons = new Dictionary<string, int>();

            foreach (var coordObj in coordsArray)
            {
                int rx = coordObj["x"]?.Value<int>() ?? 0;
                int rz = coordObj["z"]?.Value<int>() ?? 0;
                var cell = new IntVec3(observerPos.x + rx, 0, observerPos.z + rz);

                if (!cell.InBounds(map))
                {
                    AddSkipReason(skipReasons, "out of bounds");
                    skipped++;
                    continue;
                }

                string reason;
                bool success = TryDesignate(cell, order, map, dm, out reason);
                if (success)
                    designated++;
                else
                {
                    AddSkipReason(skipReasons, reason);
                    skipped++;
                }
            }

            // Build skip reason summary
            string skipInfo = "";
            if (skipReasons.Count > 0)
            {
                var parts = new List<string>();
                foreach (var kvp in skipReasons)
                    parts.Add(kvp.Value + " " + kvp.Key);
                skipInfo = " (" + string.Join(", ", parts) + ")";
            }

            Log.Message("[RimBot] [AGENT] [" + context.PawnLabel + "] architect_orders(" + order +
                "): designated=" + designated + " skipped=" + skipped);

            onComplete(new ToolResult
            {
                ToolCallId = call.Id,
                ToolName = Name,
                Success = true,
                Content = "Designated " + designated + " for " + order + ". " +
                    skipped + " skipped" + skipInfo + "."
            });
        }

        private static bool TryDesignate(IntVec3 cell, string order, Map map,
            DesignationManager dm, out string skipReason)
        {
            switch (order)
            {
                case "mine":
                    return TryMine(cell, map, dm, out skipReason);
                case "harvest":
                    return TryHarvest(cell, map, dm, out skipReason);
                case "cut_plant":
                    return TryCutPlant(cell, map, dm, out skipReason);
                case "haul":
                    return TryHaul(cell, map, dm, out skipReason);
                case "deconstruct":
                    return TryDeconstruct(cell, map, dm, out skipReason);
                case "hunt":
                    return TryHunt(cell, map, dm, out skipReason);
                case "tame":
                    return TryTame(cell, map, dm, out skipReason);
                case "smooth_floor":
                    return TrySmoothFloor(cell, map, dm, out skipReason);
                case "smooth_wall":
                    return TrySmoothWall(cell, map, dm, out skipReason);
                case "cancel":
                    return TryCancel(cell, map, dm, out skipReason);
                case "claim":
                    return TryClaim(cell, map, dm, out skipReason);
                case "strip":
                    return TryStrip(cell, map, dm, out skipReason);
                case "uninstall":
                    return TryUninstall(cell, map, dm, out skipReason);
                default:
                    skipReason = "unknown order";
                    return false;
            }
        }

        private static bool TryMine(IntVec3 cell, Map map, DesignationManager dm, out string skipReason)
        {
            if (dm.DesignationAt(cell, DesignationDefOf.Mine) != null)
            {
                skipReason = "already designated";
                return false;
            }

            var thingList = cell.GetThingList(map);
            bool hasMineable = false;
            for (int i = 0; i < thingList.Count; i++)
            {
                if (thingList[i].def.mineable)
                {
                    hasMineable = true;
                    break;
                }
            }

            if (!hasMineable)
            {
                skipReason = "nothing mineable";
                return false;
            }

            var des = new Designation(cell, DesignationDefOf.Mine);
            dm.AddDesignation(des);
            RegisterDesignation(des);
            skipReason = null;
            return true;
        }

        private static bool TryHarvest(IntVec3 cell, Map map, DesignationManager dm, out string skipReason)
        {
            var thingList = cell.GetThingList(map);
            for (int i = 0; i < thingList.Count; i++)
            {
                var plant = thingList[i] as Plant;
                if (plant != null && plant.HarvestableNow)
                {
                    if (dm.DesignationOn(plant, DesignationDefOf.HarvestPlant) != null)
                    {
                        skipReason = "already designated";
                        return false;
                    }
                    var des = new Designation(plant, DesignationDefOf.HarvestPlant);
                    dm.AddDesignation(des);
                    RegisterDesignation(des);
                    skipReason = null;
                    return true;
                }
            }
            skipReason = "no harvestable plant";
            return false;
        }

        private static bool TryCutPlant(IntVec3 cell, Map map, DesignationManager dm, out string skipReason)
        {
            var thingList = cell.GetThingList(map);
            for (int i = 0; i < thingList.Count; i++)
            {
                if (thingList[i] is Plant plant)
                {
                    if (dm.DesignationOn(plant, DesignationDefOf.CutPlant) != null)
                    {
                        skipReason = "already designated";
                        return false;
                    }
                    var desCut = new Designation(plant, DesignationDefOf.CutPlant);
                    dm.AddDesignation(desCut);
                    RegisterDesignation(desCut);
                    skipReason = null;
                    return true;
                }
            }
            skipReason = "no plant";
            return false;
        }

        private static bool TryHaul(IntVec3 cell, Map map, DesignationManager dm, out string skipReason)
        {
            var thingList = cell.GetThingList(map);
            for (int i = 0; i < thingList.Count; i++)
            {
                var thing = thingList[i];
                if (thing.def.category == ThingCategory.Item)
                {
                    if (dm.DesignationOn(thing, DesignationDefOf.Haul) != null)
                    {
                        skipReason = "already designated";
                        return false;
                    }
                    var desHaul = new Designation(thing, DesignationDefOf.Haul);
                    dm.AddDesignation(desHaul);
                    RegisterDesignation(desHaul);
                    skipReason = null;
                    return true;
                }
            }
            skipReason = "no item";
            return false;
        }

        private static bool TryDeconstruct(IntVec3 cell, Map map, DesignationManager dm, out string skipReason)
        {
            var thingList = cell.GetThingList(map);
            var tracker = OwnershipTracker.Get(map);
            for (int i = 0; i < thingList.Count; i++)
            {
                var thing = thingList[i];
                if (thing.def.building != null && thing.Faction == Faction.OfPlayer && !thing.def.mineable)
                {
                    // Block deconstructing another bot's building
                    if (tracker != null)
                    {
                        int owner = tracker.GetThingOwner(thing.thingIDNumber);
                        if (owner >= 0 && owner != currentPawnId)
                        {
                            skipReason = "owned by another colonist";
                            return false;
                        }
                    }
                    if (dm.DesignationOn(thing, DesignationDefOf.Deconstruct) != null)
                    {
                        skipReason = "already designated";
                        return false;
                    }
                    var desDecon = new Designation(thing, DesignationDefOf.Deconstruct);
                    dm.AddDesignation(desDecon);
                    RegisterDesignation(desDecon);
                    skipReason = null;
                    return true;
                }
            }
            skipReason = "no deconstructable building";
            return false;
        }

        private static bool TryHunt(IntVec3 cell, Map map, DesignationManager dm, out string skipReason)
        {
            var thingList = cell.GetThingList(map);
            for (int i = 0; i < thingList.Count; i++)
            {
                var pawn = thingList[i] as Pawn;
                if (pawn != null && pawn.AnimalOrWildMan() && pawn.Faction == null)
                {
                    if (dm.DesignationOn(pawn, DesignationDefOf.Hunt) != null)
                    {
                        skipReason = "already designated";
                        return false;
                    }
                    var desHunt = new Designation(pawn, DesignationDefOf.Hunt);
                    dm.AddDesignation(desHunt);
                    RegisterDesignation(desHunt);
                    skipReason = null;
                    return true;
                }
            }
            skipReason = "no wild animal";
            return false;
        }

        private static bool TryTame(IntVec3 cell, Map map, DesignationManager dm, out string skipReason)
        {
            var thingList = cell.GetThingList(map);
            for (int i = 0; i < thingList.Count; i++)
            {
                var pawn = thingList[i] as Pawn;
                if (pawn != null && pawn.AnimalOrWildMan() && pawn.Faction == null)
                {
                    if (dm.DesignationOn(pawn, DesignationDefOf.Tame) != null)
                    {
                        skipReason = "already designated";
                        return false;
                    }
                    var desTame = new Designation(pawn, DesignationDefOf.Tame);
                    dm.AddDesignation(desTame);
                    RegisterDesignation(desTame);
                    skipReason = null;
                    return true;
                }
            }
            skipReason = "no wild animal";
            return false;
        }

        private static bool TrySmoothFloor(IntVec3 cell, Map map, DesignationManager dm, out string skipReason)
        {
            if (dm.DesignationAt(cell, DesignationDefOf.SmoothFloor) != null)
            {
                skipReason = "already designated";
                return false;
            }

            var terrain = cell.GetTerrain(map);
            if (terrain == null || !terrain.affordances.Contains(TerrainAffordanceDefOf.SmoothableStone))
            {
                skipReason = "not smoothable stone floor";
                return false;
            }

            var desSmooth = new Designation(cell, DesignationDefOf.SmoothFloor);
            dm.AddDesignation(desSmooth);
            RegisterDesignation(desSmooth);
            skipReason = null;
            return true;
        }

        private static bool TrySmoothWall(IntVec3 cell, Map map, DesignationManager dm, out string skipReason)
        {
            if (dm.DesignationAt(cell, DesignationDefOf.SmoothWall) != null)
            {
                skipReason = "already designated";
                return false;
            }

            var thingList = cell.GetThingList(map);
            bool hasSmoothable = false;
            for (int i = 0; i < thingList.Count; i++)
            {
                if (thingList[i].def.IsSmoothable)
                {
                    hasSmoothable = true;
                    break;
                }
            }

            if (!hasSmoothable)
            {
                skipReason = "no smoothable wall";
                return false;
            }

            var desSmoothW = new Designation(cell, DesignationDefOf.SmoothWall);
            dm.AddDesignation(desSmoothW);
            RegisterDesignation(desSmoothW);
            skipReason = null;
            return true;
        }

        private static bool TryCancel(IntVec3 cell, Map map, DesignationManager dm, out string skipReason)
        {
            var tracker = OwnershipTracker.Get(map);

            // Cancel all designations on cell (skip other bots' designations)
            var cellDesigs = dm.AllDesignationsAt(cell);
            bool cancelled = false;

            // Copy to list to avoid modification during iteration
            var toRemove = new List<Designation>();
            foreach (var d in cellDesigs)
            {
                if (tracker != null)
                {
                    int owner = tracker.GetDesignationOwner(d);
                    if (owner >= 0 && owner != currentPawnId)
                        continue; // skip other bot's designation
                }
                toRemove.Add(d);
            }

            foreach (var d in toRemove)
            {
                dm.RemoveDesignation(d);
                cancelled = true;
            }

            // Also cancel thing-based designations on things at this cell
            var thingList = cell.GetThingList(map);
            for (int i = thingList.Count - 1; i >= 0; i--)
            {
                var thing = thingList[i];

                // Cancel blueprints/frames (skip other bots')
                if (thing is Blueprint || thing is Frame)
                {
                    if (tracker != null)
                    {
                        int owner = tracker.GetThingOwner(thing.thingIDNumber);
                        if (owner >= 0 && owner != currentPawnId)
                            continue;
                    }
                    thing.Destroy(DestroyMode.Cancel);
                    cancelled = true;
                    continue;
                }

                var thingDesigs = dm.AllDesignationsOn(thing);
                var thingToRemove = new List<Designation>();
                foreach (var d in thingDesigs)
                {
                    if (tracker != null)
                    {
                        int dOwner = tracker.GetDesignationOwner(d);
                        if (dOwner >= 0 && dOwner != currentPawnId)
                            continue;
                    }
                    thingToRemove.Add(d);
                }
                foreach (var d in thingToRemove)
                {
                    dm.RemoveDesignation(d);
                    cancelled = true;
                }
            }

            if (!cancelled)
            {
                skipReason = "nothing to cancel";
                return false;
            }

            skipReason = null;
            return true;
        }

        private static bool TryClaim(IntVec3 cell, Map map, DesignationManager dm, out string skipReason)
        {
            var thingList = cell.GetThingList(map);
            for (int i = 0; i < thingList.Count; i++)
            {
                var thing = thingList[i];
                if (thing.def.building != null && thing.Faction == null)
                {
                    thing.SetFaction(Faction.OfPlayer);
                    skipReason = null;
                    return true;
                }
            }
            skipReason = "nothing claimable";
            return false;
        }

        private static bool TryStrip(IntVec3 cell, Map map, DesignationManager dm, out string skipReason)
        {
            var thingList = cell.GetThingList(map);
            for (int i = 0; i < thingList.Count; i++)
            {
                var thing = thingList[i];
                if (thing is Corpse || (thing is Pawn p && p.Downed))
                {
                    if (dm.DesignationOn(thing, DesignationDefOf.Strip) != null)
                    {
                        skipReason = "already designated";
                        return false;
                    }
                    var desStrip = new Designation(thing, DesignationDefOf.Strip);
                    dm.AddDesignation(desStrip);
                    RegisterDesignation(desStrip);
                    skipReason = null;
                    return true;
                }
            }
            skipReason = "nothing to strip";
            return false;
        }

        private static bool TryUninstall(IntVec3 cell, Map map, DesignationManager dm, out string skipReason)
        {
            var thingList = cell.GetThingList(map);
            for (int i = 0; i < thingList.Count; i++)
            {
                var thing = thingList[i];
                if (thing.def.Minifiable && thing.Faction == Faction.OfPlayer)
                {
                    if (dm.DesignationOn(thing, DesignationDefOf.Uninstall) != null)
                    {
                        skipReason = "already designated";
                        return false;
                    }
                    var desUninst = new Designation(thing, DesignationDefOf.Uninstall);
                    dm.AddDesignation(desUninst);
                    RegisterDesignation(desUninst);
                    skipReason = null;
                    return true;
                }
            }
            skipReason = "nothing uninstallable";
            return false;
        }

        private static void RegisterDesignation(Designation des)
        {
            OwnershipTracker.Get(currentMap)?.SetDesignationOwner(des, currentPawnId);
        }

        private static void AddSkipReason(Dictionary<string, int> reasons, string reason)
        {
            if (reasons.ContainsKey(reason))
                reasons[reason]++;
            else
                reasons[reason] = 1;
        }
    }
}
