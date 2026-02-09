using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json.Linq;
using RimWorld;
using Verse;

namespace RimBot.Tools
{
    public class ScanAreaTool : ITool
    {
        public string Name => "scan_area";

        public ToolDefinition GetDefinition()
        {
            return new ToolDefinition
            {
                Name = Name,
                Description = "Scan an area and list all objects grouped by category (buildings, items, pawns, resources). " +
                    "Default center is your position, default radius 10. Coordinates are relative to you at (0,0).",
                ParametersJson = "{\"type\":\"object\",\"properties\":{" +
                    "\"x\":{\"type\":\"integer\",\"description\":\"X offset of scan center (default 0)\"}," +
                    "\"z\":{\"type\":\"integer\",\"description\":\"Z offset of scan center (default 0)\"}," +
                    "\"radius\":{\"type\":\"integer\",\"description\":\"Scan radius in tiles (default 10, max 15)\"}}" +
                    ",\"required\":[]}"
            };
        }

        public void Execute(ToolCall call, ToolContext context, Action<ToolResult> onComplete)
        {
            int cx = 0, cz = 0, radius = 10;
            if (call.Arguments != null)
            {
                if (call.Arguments["x"] != null) cx = call.Arguments["x"].Value<int>();
                if (call.Arguments["z"] != null) cz = call.Arguments["z"].Value<int>();
                if (call.Arguments["radius"] != null) radius = call.Arguments["radius"].Value<int>();
            }
            radius = Math.Max(1, Math.Min(15, radius));

            var map = context.Map;
            var center = new IntVec3(context.PawnPosition.x + cx, 0, context.PawnPosition.z + cz);

            Log.Message("[RimBot] [AGENT] [" + context.PawnLabel + "] scan_area(" + cx + ", " + cz + ", r=" + radius + ")");

            var seenThings = new HashSet<int>(); // thingIDNumber to deduplicate multi-tile things
            var buildings = new List<string>();
            var items = new List<string>();
            var pawns = new List<string>();
            var plants = new List<string>();
            int totalEntries = 0;
            const int maxEntries = 100;
            bool truncated = false;

            for (int dx = -radius; dx <= radius && !truncated; dx++)
            {
                for (int dz = -radius; dz <= radius && !truncated; dz++)
                {
                    // Circular filter
                    if (dx * dx + dz * dz > radius * radius)
                        continue;

                    var cell = new IntVec3(center.x + dx, 0, center.z + dz);
                    if (!cell.InBounds(map))
                        continue;

                    var thingList = cell.GetThingList(map);
                    for (int i = 0; i < thingList.Count; i++)
                    {
                        var thing = thingList[i];

                        // Skip noise categories
                        if (thing.def.category == ThingCategory.Mote ||
                            thing.def.category == ThingCategory.Filth ||
                            thing.def.category == ThingCategory.Projectile)
                            continue;

                        // Deduplicate
                        if (!seenThings.Add(thing.thingIDNumber))
                            continue;

                        // Relative coords from pawn
                        int relX = thing.Position.x - context.PawnPosition.x;
                        int relZ = thing.Position.z - context.PawnPosition.z;
                        string coords = "(" + relX + ", " + relZ + ")";

                        if (thing is Pawn p)
                        {
                            string faction = p.Faction != null ? p.Faction.Name : "wild";
                            pawns.Add(p.LabelShort + " (" + faction + ") at " + coords);
                            totalEntries++;
                        }
                        else if (thing.def.building != null)
                        {
                            string hp = thing.HitPoints + "/" + thing.MaxHitPoints;
                            string stuff = thing.Stuff != null ? " (" + thing.Stuff.label + ")" : "";
                            buildings.Add(thing.def.label + stuff + " [HP: " + hp + "] at " + coords);
                            totalEntries++;
                        }
                        else if (thing.def.category == ThingCategory.Item)
                        {
                            items.Add(thing.LabelNoCount + " x" + thing.stackCount + " at " + coords);
                            totalEntries++;
                        }
                        else if (thing.def.category == ThingCategory.Plant)
                        {
                            var plant = thing as Plant;
                            if (plant != null && plant.HarvestableNow)
                            {
                                plants.Add(thing.def.label + " [harvestable] at " + coords);
                                totalEntries++;
                            }
                        }

                        if (totalEntries >= maxEntries)
                        {
                            truncated = true;
                            break;
                        }
                    }
                }
            }

            var sb = new StringBuilder();
            sb.AppendLine("Scan area center=(" + cx + ", " + cz + ") radius=" + radius + ":");

            sb.AppendLine("\nBuildings (" + buildings.Count + "):");
            if (buildings.Count > 0)
                foreach (var b in buildings) sb.AppendLine("  " + b);
            else
                sb.AppendLine("  none");

            sb.AppendLine("\nItems (" + items.Count + "):");
            if (items.Count > 0)
                foreach (var item in items) sb.AppendLine("  " + item);
            else
                sb.AppendLine("  none");

            sb.AppendLine("\nPawns (" + pawns.Count + "):");
            if (pawns.Count > 0)
                foreach (var p in pawns) sb.AppendLine("  " + p);
            else
                sb.AppendLine("  none");

            sb.AppendLine("\nHarvestable plants (" + plants.Count + "):");
            if (plants.Count > 0)
                foreach (var pl in plants) sb.AppendLine("  " + pl);
            else
                sb.AppendLine("  none");

            if (truncated)
                sb.AppendLine("\n[Results truncated at " + maxEntries + " entries]");

            onComplete(new ToolResult
            {
                ToolCallId = call.Id,
                ToolName = Name,
                Success = true,
                Content = sb.ToString().TrimEnd()
            });
        }
    }
}
