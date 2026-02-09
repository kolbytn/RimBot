using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using RimWorld;
using Verse;

namespace RimBot.Tools
{
    public class DesignateTool : ITool
    {
        public string Name => "designate";

        public ToolDefinition GetDefinition()
        {
            return new ToolDefinition
            {
                Name = Name,
                Description = "Designate cells for harvesting, mining, hauling, or other colonist work orders. " +
                    "Coordinates are relative to your position (0,0). Colonists will carry out designated work automatically. " +
                    "Types: mine (rock/ore), harvest (ripe plants/trees), cut_plant (any plant), " +
                    "haul (move items to stockpile), deconstruct (tear down buildings), hunt (kill animals).",
                ParametersJson = "{\"type\":\"object\",\"properties\":{" +
                    "\"coordinates\":{\"type\":\"array\",\"items\":{\"type\":\"object\"," +
                    "\"properties\":{\"x\":{\"type\":\"integer\"},\"z\":{\"type\":\"integer\"}}," +
                    "\"required\":[\"x\",\"z\"]},\"description\":\"Relative coordinates to designate\"}," +
                    "\"type\":{\"type\":\"string\"," +
                    "\"enum\":[\"mine\",\"harvest\",\"cut_plant\",\"haul\",\"deconstruct\",\"hunt\"]," +
                    "\"description\":\"Designation type\"}}" +
                    ",\"required\":[\"coordinates\",\"type\"]}"
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

            string desigType = call.Arguments["type"]?.Value<string>();
            if (string.IsNullOrEmpty(desigType))
            {
                onComplete(new ToolResult
                {
                    ToolCallId = call.Id,
                    ToolName = Name,
                    Success = false,
                    Content = "type parameter is required."
                });
                return;
            }

            Log.Message("[RimBot] [AGENT] [" + context.PawnLabel + "] designate(" + desigType +
                ", " + coordsArray.Count + " cells)");

            var map = context.Map;
            var observerPos = context.PawnPosition;
            var dm = map.designationManager;

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
                bool success = TryDesignate(cell, desigType, map, dm, out reason);
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

            Log.Message("[RimBot] [AGENT] [" + context.PawnLabel + "] designate(" + desigType +
                "): designated=" + designated + " skipped=" + skipped);

            onComplete(new ToolResult
            {
                ToolCallId = call.Id,
                ToolName = Name,
                Success = true,
                Content = "Designated " + designated + " for " + desigType + ". " +
                    skipped + " skipped" + skipInfo + "."
            });
        }

        private static bool TryDesignate(IntVec3 cell, string type, Map map,
            DesignationManager dm, out string skipReason)
        {
            switch (type)
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
                default:
                    skipReason = "unknown type";
                    return false;
            }
        }

        private static bool TryMine(IntVec3 cell, Map map, DesignationManager dm, out string skipReason)
        {
            // Mine is cell-based — check for mineable building at the cell
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

            dm.AddDesignation(new Designation(cell, DesignationDefOf.Mine));
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
                    dm.AddDesignation(new Designation(plant, DesignationDefOf.HarvestPlant));
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
                    dm.AddDesignation(new Designation(plant, DesignationDefOf.CutPlant));
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
                    dm.AddDesignation(new Designation(thing, DesignationDefOf.Haul));
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
            for (int i = 0; i < thingList.Count; i++)
            {
                var thing = thingList[i];
                if (thing.def.building != null && thing.Faction == Faction.OfPlayer && !thing.def.mineable)
                {
                    if (dm.DesignationOn(thing, DesignationDefOf.Deconstruct) != null)
                    {
                        skipReason = "already designated";
                        return false;
                    }
                    dm.AddDesignation(new Designation(thing, DesignationDefOf.Deconstruct));
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
                    dm.AddDesignation(new Designation(pawn, DesignationDefOf.Hunt));
                    skipReason = null;
                    return true;
                }
            }
            skipReason = "no wild animal";
            return false;
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
