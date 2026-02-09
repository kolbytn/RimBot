using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json.Linq;
using Verse;

namespace RimBot.Tools
{
    public class FindOnMapTool : ITool
    {
        public string Name => "find_on_map";

        public ToolDefinition GetDefinition()
        {
            return new ToolDefinition
            {
                Name = Name,
                Description = "Search the entire map for all instances of a thing by name. Returns locations as relative coordinates. " +
                    "Examples: 'Steel', 'ComponentIndustrial', 'WoodLog', 'MealSimple', 'Wall', 'Door'.",
                ParametersJson = "{\"type\":\"object\",\"properties\":{" +
                    "\"thing_name\":{\"type\":\"string\",\"description\":\"Name of the thing to search for (defName)\"}}" +
                    ",\"required\":[\"thing_name\"]}"
            };
        }

        public void Execute(ToolCall call, ToolContext context, Action<ToolResult> onComplete)
        {
            string thingName = call.Arguments["thing_name"]?.Value<string>();
            if (string.IsNullOrEmpty(thingName))
            {
                onComplete(new ToolResult
                {
                    ToolCallId = call.Id,
                    ToolName = Name,
                    Success = false,
                    Content = "thing_name parameter is required."
                });
                return;
            }

            Log.Message("[RimBot] [AGENT] [" + context.PawnLabel + "] find_on_map(" + thingName + ")");

            var map = context.Map;

            // Try exact defName first
            var def = DefDatabase<ThingDef>.GetNamed(thingName, false);

            // If not found, try case-insensitive search
            if (def == null)
            {
                string lower = thingName.ToLower();
                foreach (var d in DefDatabase<ThingDef>.AllDefs)
                {
                    if (d.defName.ToLower() == lower)
                    {
                        def = d;
                        break;
                    }
                }
            }

            // If still not found, try partial match on label
            if (def == null)
            {
                string lower = thingName.ToLower();
                foreach (var d in DefDatabase<ThingDef>.AllDefs)
                {
                    if (d.label != null && d.label.ToLower() == lower)
                    {
                        def = d;
                        break;
                    }
                }
            }

            if (def == null)
            {
                // Suggest similar names
                string lower = thingName.ToLower();
                var suggestions = new List<string>();
                foreach (var d in DefDatabase<ThingDef>.AllDefs)
                {
                    if (d.defName.ToLower().Contains(lower) || (d.label != null && d.label.ToLower().Contains(lower)))
                    {
                        suggestions.Add(d.defName);
                        if (suggestions.Count >= 10)
                            break;
                    }
                }

                string msg = "No ThingDef found for '" + thingName + "'.";
                if (suggestions.Count > 0)
                    msg += " Did you mean: " + string.Join(", ", suggestions) + "?";

                onComplete(new ToolResult
                {
                    ToolCallId = call.Id,
                    ToolName = Name,
                    Success = false,
                    Content = msg
                });
                return;
            }

            var things = map.listerThings.ThingsOfDef(def);
            if (things == null || things.Count == 0)
            {
                onComplete(new ToolResult
                {
                    ToolCallId = call.Id,
                    ToolName = Name,
                    Success = true,
                    Content = "Found 0 instances of " + def.defName + " on the map."
                });
                return;
            }

            // Sort by distance from pawn
            var sorted = new List<Thing>(things);
            var pawnPos = context.PawnPosition;
            sorted.Sort((a, b) =>
            {
                float distA = (a.Position - pawnPos).LengthHorizontalSquared;
                float distB = (b.Position - pawnPos).LengthHorizontalSquared;
                return distA.CompareTo(distB);
            });

            int totalCount = sorted.Count;
            const int maxResults = 50;
            bool truncated = totalCount > maxResults;

            var sb = new StringBuilder();
            sb.AppendLine("Found " + totalCount + " instances of " + def.defName + " (" + def.label + "):");

            int shown = Math.Min(totalCount, maxResults);
            for (int i = 0; i < shown; i++)
            {
                var thing = sorted[i];
                int relX = thing.Position.x - pawnPos.x;
                int relZ = thing.Position.z - pawnPos.z;
                float dist = (float)Math.Sqrt(relX * relX + relZ * relZ);

                string entry = "  (" + relX + ", " + relZ + ") dist=" + dist.ToString("F0");
                if (thing.def.category == ThingCategory.Item)
                    entry += " x" + thing.stackCount;
                sb.AppendLine(entry);
            }

            if (truncated)
                sb.AppendLine("[Showing " + maxResults + " of " + totalCount + " total]");

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
