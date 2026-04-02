using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json.Linq;
using RimWorld;
using Verse;

namespace RimBot.Tools
{
    public class SetWorkPriorityTool : ITool
    {
        public string Name => "set_work_priority";

        private const int HighPriority = 1;
        private const int LowPriority = 3;

        public ToolDefinition GetDefinition()
        {
            return new ToolDefinition
            {
                Name = Name,
                Description = "Set work types to high or low priority. All work is always active at low priority. " +
                    "Use this to promote specific work types to high priority based on current needs. " +
                    "You can set multiple work types at once.",
                ParametersJson = "{\"type\":\"object\",\"properties\":{" +
                    "\"work_types\":{\"type\":\"array\",\"items\":{\"type\":\"string\"}," +
                    "\"description\":\"Work type names to change (e.g. ['Construction', 'Research', 'Cook'])\"}," +
                    "\"level\":{\"type\":\"string\",\"enum\":[\"high\",\"low\"]," +
                    "\"description\":\"high = top priority, low = background priority (default)\"}}" +
                    ",\"required\":[\"work_types\",\"level\"]}"
            };
        }

        public void Execute(ToolCall call, ToolContext context, Action<ToolResult> onComplete)
        {
            // Support both old single-value format and new array format
            var workTypeNames = new List<string>();
            var typesArg = call.Arguments["work_types"];
            if (typesArg is JArray arr)
            {
                foreach (var item in arr)
                    workTypeNames.Add(item.Value<string>());
            }
            // Fallback: old format with single "work_type" string
            if (workTypeNames.Count == 0)
            {
                var singleArg = call.Arguments["work_type"]?.Value<string>();
                if (!string.IsNullOrEmpty(singleArg))
                    workTypeNames.Add(singleArg);
            }

            if (workTypeNames.Count == 0)
            {
                onComplete(new ToolResult
                {
                    ToolCallId = call.Id,
                    ToolName = Name,
                    Success = false,
                    Content = "work_types parameter is required."
                });
                return;
            }

            string level = call.Arguments["level"]?.Value<string>()?.ToLower() ?? "high";
            // Fallback: old format with numeric "priority"
            var priorityArg = call.Arguments["priority"];
            if (priorityArg != null)
                level = priorityArg.Value<int>() <= 2 ? "high" : "low";

            int targetPriority = level == "high" ? HighPriority : LowPriority;

            var pawn = BrainManager.FindPawnById(context.PawnId);
            if (pawn == null || !pawn.Spawned)
            {
                onComplete(new ToolResult
                {
                    ToolCallId = call.Id, ToolName = Name, Success = false,
                    Content = "Pawn not found or not spawned."
                });
                return;
            }

            var results = new List<string>();
            int changed = 0;

            foreach (var workTypeName in workTypeNames)
            {
                var workDef = ResolveWorkType(workTypeName);
                if (workDef == null)
                {
                    results.Add(workTypeName + ": unknown");
                    continue;
                }
                if (pawn.WorkTypeIsDisabled(workDef))
                {
                    results.Add(workDef.labelShort + ": incapable");
                    continue;
                }

                int currentPriority = pawn.workSettings.GetPriority(workDef);
                if (currentPriority == targetPriority)
                {
                    results.Add(workDef.labelShort + ": already " + level);
                    continue;
                }

                pawn.workSettings.SetPriority(workDef, targetPriority);
                results.Add(workDef.labelShort + ": → " + level);
                changed++;
            }

            Log.Message("[RimBot] [AGENT] [" + context.PawnLabel + "] set_work_priority(" +
                string.Join(", ", workTypeNames) + " → " + level + "): " + changed + " changed");

            onComplete(new ToolResult
            {
                ToolCallId = call.Id,
                ToolName = Name,
                Success = true,
                Content = string.Join("; ", results)
            });
        }

        private static WorkTypeDef ResolveWorkType(string name)
        {
            string normalized = ResolveSynonym(name.ToLower().Replace(" ", "").Replace("_", ""));

            foreach (var wt in DefDatabase<WorkTypeDef>.AllDefs)
            {
                string normDef = wt.defName.ToLower().Replace(" ", "").Replace("_", "");
                string normLabel = wt.labelShort.ToLower().Replace(" ", "").Replace("_", "");
                if (normDef == normalized || normLabel == normalized)
                    return wt;
            }

            // Strip gerund suffix: "constructing" → "construct"
            if (normalized.Length > 4 && normalized.EndsWith("ing"))
            {
                string stemmed = normalized.Substring(0, normalized.Length - 3);
                foreach (var wt in DefDatabase<WorkTypeDef>.AllDefs)
                {
                    string normDef = wt.defName.ToLower().Replace(" ", "").Replace("_", "");
                    string normLabel = wt.labelShort.ToLower().Replace(" ", "").Replace("_", "");
                    if (normDef == stemmed || normLabel == stemmed ||
                        normDef.StartsWith(stemmed) || normLabel.StartsWith(stemmed))
                        return wt;
                }
            }

            return null;
        }

        private static readonly Dictionary<string, string> Synonyms = new Dictionary<string, string>
        {
            { "medical", "doctor" },
            { "medic", "doctor" },
            { "healing", "doctor" },
            { "farming", "grow" },
            { "cooking", "cook" },
            { "building", "construct" },
            { "woodcutting", "plantcut" },
            { "chopping", "plantcut" },
            { "smithing", "smith" },
            { "sewing", "tailor" },
            { "tailoring", "tailor" },
            { "crafting", "craft" },
            { "sweeping", "clean" },
        };

        private static string ResolveSynonym(string normalized)
        {
            string value;
            if (Synonyms.TryGetValue(normalized, out value))
                return value;
            return normalized;
        }
    }
}
