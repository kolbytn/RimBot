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

            // Stage 1: exact defName
            var def = DefDatabase<ThingDef>.GetNamed(thingName, false);

            // Stage 2: case-insensitive defName or label
            if (def == null)
            {
                string lower = thingName.ToLower();
                foreach (var d in DefDatabase<ThingDef>.AllDefs)
                {
                    if (d.defName.ToLower() == lower || (d.label != null && d.label.ToLower() == lower))
                    {
                        def = d;
                        break;
                    }
                }
            }

            // Stage 3: normalized containment — input is substring of defName
            // Handles "BoltActionRifle" matching "Gun_BoltActionRifle" (prefix stripped)
            if (def == null)
            {
                string normalizedInput = thingName.ToLower().Replace("_", "").Replace(" ", "");
                // Strip parenthetical suffixes like "(blueprint)" or "(frame)"
                int pIdx = normalizedInput.IndexOf('(');
                if (pIdx > 0) normalizedInput = normalizedInput.Substring(0, pIdx);
                foreach (var d in DefDatabase<ThingDef>.AllDefs)
                {
                    string normalizedDef = d.defName.ToLower().Replace("_", "").Replace(" ", "");
                    if (normalizedDef == normalizedInput || normalizedDef.Contains(normalizedInput))
                    {
                        def = d;
                        break;
                    }
                }
            }

            // Stage 4: word overlap — handles reordered words like "MealPackagedSurvival" → "MealSurvivalPack"
            if (def == null)
            {
                var inputWords = new HashSet<string>();
                foreach (var word in System.Text.RegularExpressions.Regex.Split(thingName, @"(?=[A-Z])|[_\s(]+"))
                {
                    if (word.Length >= 3) inputWords.Add(word.ToLower());
                }
                if (inputWords.Count >= 2)
                {
                    foreach (var d in DefDatabase<ThingDef>.AllDefs)
                    {
                        string dl = d.defName.ToLower();
                        int matches = 0;
                        foreach (var word in inputWords)
                        {
                            if (dl.Contains(word)) matches++;
                        }
                        if (matches >= 2)
                        {
                            def = d;
                            break;
                        }
                    }
                }
            }

            if (def == null)
            {
                // Suggest similar names using substring match and word overlap
                string lower = thingName.ToLower();
                // Split input into word fragments (split on uppercase boundaries and common delimiters)
                var inputWords = new HashSet<string>();
                foreach (var word in System.Text.RegularExpressions.Regex.Split(thingName, @"(?=[A-Z])|[_\s]+"))
                {
                    if (word.Length >= 3) inputWords.Add(word.ToLower());
                }

                var suggestions = new List<string>();
                foreach (var d in DefDatabase<ThingDef>.AllDefs)
                {
                    string dl = d.defName.ToLower();
                    string ll = d.label != null ? d.label.ToLower() : "";
                    // Direct substring match
                    if (dl.Contains(lower) || ll.Contains(lower))
                    {
                        suggestions.Add(d.defName);
                    }
                    // Word overlap: if 2+ input words appear in the defName or label
                    else if (inputWords.Count >= 2)
                    {
                        int matches = 0;
                        foreach (var word in inputWords)
                        {
                            if (dl.Contains(word) || ll.Contains(word)) matches++;
                        }
                        if (matches >= 2) suggestions.Add(d.defName);
                    }
                    if (suggestions.Count >= 10) break;
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
