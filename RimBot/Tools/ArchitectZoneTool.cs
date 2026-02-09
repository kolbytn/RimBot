using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using RimWorld;
using Verse;

namespace RimBot.Tools
{
    public class ArchitectZoneTool : ITool
    {
        public string Name => "architect_zone";

        public ToolDefinition GetDefinition()
        {
            return new ToolDefinition
            {
                Name = Name,
                Description = "Manage zones and areas. Create growing zones, stockpiles, and toggle home/roof/snow areas. " +
                    "Coordinates are relative to your position (0,0). +X=east, +Z=north.",
                ParametersJson = "{\"type\":\"object\",\"properties\":{" +
                    "\"zone_type\":{\"type\":\"string\"," +
                    "\"enum\":[\"growing_zone\",\"stockpile\",\"dumping_stockpile\"," +
                    "\"expand_home\",\"clear_home\",\"expand_roof\",\"clear_roof\",\"delete_zone\"]," +
                    "\"description\":\"The zone or area operation\"}," +
                    "\"coordinates\":{\"type\":\"array\",\"items\":{\"type\":\"object\"," +
                    "\"properties\":{\"x\":{\"type\":\"integer\"},\"z\":{\"type\":\"integer\"}}," +
                    "\"required\":[\"x\",\"z\"]},\"description\":\"Relative coordinates for zone cells\"}}" +
                    ",\"required\":[\"zone_type\",\"coordinates\"]}"
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

            string zoneType = call.Arguments["zone_type"]?.Value<string>();
            if (string.IsNullOrEmpty(zoneType))
            {
                onComplete(new ToolResult
                {
                    ToolCallId = call.Id,
                    ToolName = Name,
                    Success = false,
                    Content = "zone_type parameter is required."
                });
                return;
            }

            Log.Message("[RimBot] [AGENT] [" + context.PawnLabel + "] architect_zone(" + zoneType +
                ", " + coordsArray.Count + " cells)");

            var map = context.Map;
            var observerPos = context.PawnPosition;

            // Resolve cells
            var cells = new List<IntVec3>();
            int outOfBounds = 0;
            foreach (var coordObj in coordsArray)
            {
                int rx = coordObj["x"]?.Value<int>() ?? 0;
                int rz = coordObj["z"]?.Value<int>() ?? 0;
                var cell = new IntVec3(observerPos.x + rx, 0, observerPos.z + rz);

                if (!cell.InBounds(map))
                {
                    outOfBounds++;
                    continue;
                }
                cells.Add(cell);
            }

            if (cells.Count == 0)
            {
                onComplete(new ToolResult
                {
                    ToolCallId = call.Id,
                    ToolName = Name,
                    Success = false,
                    Content = "All coordinates were out of bounds."
                });
                return;
            }

            string resultMsg;
            switch (zoneType)
            {
                case "growing_zone":
                    resultMsg = CreateOrExpandGrowingZone(map, cells);
                    break;
                case "stockpile":
                    resultMsg = CreateOrExpandStockpile(map, cells, false);
                    break;
                case "dumping_stockpile":
                    resultMsg = CreateOrExpandStockpile(map, cells, true);
                    break;
                case "expand_home":
                    resultMsg = SetArea(map, cells, map.areaManager.Home, true);
                    break;
                case "clear_home":
                    resultMsg = SetArea(map, cells, map.areaManager.Home, false);
                    break;
                case "expand_roof":
                    resultMsg = SetArea(map, cells, map.areaManager.BuildRoof, true);
                    break;
                case "clear_roof":
                    resultMsg = SetArea(map, cells, map.areaManager.NoRoof, true);
                    break;
                case "delete_zone":
                    resultMsg = DeleteZones(map, cells);
                    break;
                default:
                    resultMsg = "Unknown zone_type '" + zoneType + "'.";
                    onComplete(new ToolResult
                    {
                        ToolCallId = call.Id,
                        ToolName = Name,
                        Success = false,
                        Content = resultMsg
                    });
                    return;
            }

            if (outOfBounds > 0)
                resultMsg += " " + outOfBounds + " coordinates were out of bounds.";

            Log.Message("[RimBot] [AGENT] [" + context.PawnLabel + "] architect_zone(" + zoneType + "): " + resultMsg);

            onComplete(new ToolResult
            {
                ToolCallId = call.Id,
                ToolName = Name,
                Success = true,
                Content = resultMsg
            });
        }

        private static string CreateOrExpandGrowingZone(Map map, List<IntVec3> cells)
        {
            var zm = map.zoneManager;
            int added = 0;
            int skipped = 0;

            // Try to find an existing growing zone at or adjacent to cells
            Zone_Growing existingZone = null;
            foreach (var cell in cells)
            {
                var z = zm.ZoneAt(cell) as Zone_Growing;
                if (z != null)
                {
                    existingZone = z;
                    break;
                }
            }

            if (existingZone == null)
            {
                foreach (var cell in cells)
                {
                    foreach (var adj in GenAdj.CardinalDirections)
                    {
                        var adjCell = cell + adj;
                        if (adjCell.InBounds(map))
                        {
                            var z = zm.ZoneAt(adjCell) as Zone_Growing;
                            if (z != null)
                            {
                                existingZone = z;
                                break;
                            }
                        }
                    }
                    if (existingZone != null) break;
                }
            }

            if (existingZone == null)
            {
                existingZone = new Zone_Growing(zm);
                zm.RegisterZone(existingZone);
            }

            foreach (var cell in cells)
            {
                if (zm.ZoneAt(cell) != null)
                {
                    skipped++;
                    continue;
                }
                existingZone.AddCell(cell);
                added++;
            }

            return "Added " + added + " cells to growing zone '" + existingZone.label + "'. " + skipped + " skipped (already zoned).";
        }

        private static string CreateOrExpandStockpile(Map map, List<IntVec3> cells, bool isDumping)
        {
            var zm = map.zoneManager;
            int added = 0;
            int skipped = 0;

            // Try to find existing stockpile at or adjacent to cells
            Zone_Stockpile existingZone = null;
            foreach (var cell in cells)
            {
                var z = zm.ZoneAt(cell) as Zone_Stockpile;
                if (z != null)
                {
                    existingZone = z;
                    break;
                }
            }

            if (existingZone == null)
            {
                foreach (var cell in cells)
                {
                    foreach (var adj in GenAdj.CardinalDirections)
                    {
                        var adjCell = cell + adj;
                        if (adjCell.InBounds(map))
                        {
                            var z = zm.ZoneAt(adjCell) as Zone_Stockpile;
                            if (z != null)
                            {
                                existingZone = z;
                                break;
                            }
                        }
                    }
                    if (existingZone != null) break;
                }
            }

            if (existingZone == null)
            {
                var preset = isDumping ? StorageSettingsPreset.DumpingStockpile : StorageSettingsPreset.DefaultStockpile;
                existingZone = new Zone_Stockpile(preset, zm);
                zm.RegisterZone(existingZone);
            }

            foreach (var cell in cells)
            {
                if (zm.ZoneAt(cell) != null)
                {
                    skipped++;
                    continue;
                }
                existingZone.AddCell(cell);
                added++;
            }

            string kind = isDumping ? "dumping stockpile" : "stockpile";
            return "Added " + added + " cells to " + kind + " '" + existingZone.label + "'. " + skipped + " skipped (already zoned).";
        }

        private static string SetArea(Map map, List<IntVec3> cells, Area area, bool value)
        {
            int changed = 0;
            int skipped = 0;

            foreach (var cell in cells)
            {
                if (area[cell] == value)
                {
                    skipped++;
                    continue;
                }
                area[cell] = value;
                changed++;
            }

            string action = value ? "set" : "cleared";
            return action + " " + changed + " cells in " + area.Label + ". " + skipped + " already " + action + ".";
        }

        private static string DeleteZones(Map map, List<IntVec3> cells)
        {
            var zm = map.zoneManager;
            var deletedZones = new HashSet<Zone>();
            int cellsAffected = 0;
            int noZone = 0;

            foreach (var cell in cells)
            {
                var zone = zm.ZoneAt(cell);
                if (zone == null)
                {
                    noZone++;
                    continue;
                }

                if (!deletedZones.Contains(zone))
                {
                    cellsAffected += zone.Cells.Count;
                    zone.Delete();
                    deletedZones.Add(zone);
                }
            }

            return "Deleted " + deletedZones.Count + " zone(s) covering " + cellsAffected + " cells. " +
                noZone + " cells had no zone.";
        }
    }
}
