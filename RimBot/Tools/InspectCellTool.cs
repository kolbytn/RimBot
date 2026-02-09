using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json.Linq;
using RimWorld;
using Verse;

namespace RimBot.Tools
{
    public class InspectCellTool : ITool
    {
        public string Name => "inspect_cell";

        public ToolDefinition GetDefinition()
        {
            return new ToolDefinition
            {
                Name = Name,
                Description = "Inspect a single map cell at coordinates relative to your position (0,0). " +
                    "Returns terrain, buildings, items, pawns, and roof status.",
                ParametersJson = "{\"type\":\"object\",\"properties\":{" +
                    "\"x\":{\"type\":\"integer\",\"description\":\"X offset from your position (+ is east)\"}," +
                    "\"z\":{\"type\":\"integer\",\"description\":\"Z offset from your position (+ is north)\"}}" +
                    ",\"required\":[\"x\",\"z\"]}"
            };
        }

        public void Execute(ToolCall call, ToolContext context, Action<ToolResult> onComplete)
        {
            int rx = call.Arguments["x"]?.Value<int>() ?? 0;
            int rz = call.Arguments["z"]?.Value<int>() ?? 0;
            var cell = new IntVec3(context.PawnPosition.x + rx, 0, context.PawnPosition.z + rz);
            var map = context.Map;

            Log.Message("[RimBot] [AGENT] [" + context.PawnLabel + "] inspect_cell(" + rx + ", " + rz + ")");

            if (!cell.InBounds(map))
            {
                onComplete(new ToolResult
                {
                    ToolCallId = call.Id,
                    ToolName = Name,
                    Success = false,
                    Content = "Cell (" + rx + ", " + rz + ") is out of map bounds."
                });
                return;
            }

            var sb = new StringBuilder();
            sb.AppendLine("Cell (" + rx + ", " + rz + "):");

            // Terrain
            var terrain = cell.GetTerrain(map);
            sb.AppendLine("Terrain: " + (terrain != null ? terrain.label : "unknown"));

            // Roof
            sb.AppendLine("Roof: " + (cell.Roofed(map) ? "Yes" : "No"));

            // Categorize things on this cell
            var buildings = new List<string>();
            var items = new List<string>();
            var pawns = new List<string>();
            var plants = new List<string>();

            var thingList = cell.GetThingList(map);
            for (int i = 0; i < thingList.Count; i++)
            {
                var thing = thingList[i];
                if (thing is Pawn p)
                {
                    string faction = p.Faction != null ? p.Faction.Name : "wild";
                    pawns.Add(p.LabelShort + " (" + faction + ")");
                }
                else if (thing.def.building != null)
                {
                    string hp = thing.HitPoints + "/" + thing.MaxHitPoints;
                    string stuff = thing.Stuff != null ? " (" + thing.Stuff.label + ")" : "";
                    buildings.Add(thing.def.label + stuff + " [HP: " + hp + "]");
                }
                else if (thing.def.category == ThingCategory.Item)
                {
                    items.Add(thing.LabelNoCount + " x" + thing.stackCount);
                }
                else if (thing.def.category == ThingCategory.Plant)
                {
                    var plant = thing as Plant;
                    if (plant != null)
                    {
                        int growth = (int)(plant.Growth * 100);
                        string harvestable = plant.HarvestableNow ? " [harvestable]" : "";
                        plants.Add(thing.def.label + " (" + growth + "% grown)" + harvestable);
                    }
                }
            }

            sb.AppendLine("Buildings: " + (buildings.Count > 0 ? string.Join(", ", buildings) : "none"));
            sb.AppendLine("Items: " + (items.Count > 0 ? string.Join(", ", items) : "none"));
            sb.AppendLine("Pawns: " + (pawns.Count > 0 ? string.Join(", ", pawns) : "none"));
            sb.AppendLine("Plants: " + (plants.Count > 0 ? string.Join(", ", plants) : "none"));

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
