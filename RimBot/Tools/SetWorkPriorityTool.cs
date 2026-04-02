using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using RimWorld;
using Verse;

namespace RimBot.Tools
{
    public class SetWorkPriorityTool : ITool
    {
        public string Name => "set_work_priority";

        public ToolDefinition GetDefinition()
        {
            return new ToolDefinition
            {
                Name = Name,
                Description = "Set your own priority for a specific work type. " +
                    "Priority 0 disables the work type, 1 is highest priority, 4 is lowest.",
                ParametersJson = "{\"type\":\"object\",\"properties\":{" +
                    "\"work_type\":{\"type\":\"string\",\"description\":\"Work type name (e.g. Mining, Cooking, Construction, Growing, etc.)\"}," +
                    "\"priority\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":4,\"description\":\"Priority level: 0=disabled, 1=highest, 4=lowest\"}}" +
                    ",\"required\":[\"work_type\",\"priority\"]}"
            };
        }

        public void Execute(ToolCall call, ToolContext context, Action<ToolResult> onComplete)
        {
            string workTypeName = call.Arguments["work_type"]?.Value<string>();
            if (string.IsNullOrEmpty(workTypeName))
            {
                onComplete(new ToolResult
                {
                    ToolCallId = call.Id,
                    ToolName = Name,
                    Success = false,
                    Content = "work_type parameter is required."
                });
                return;
            }

            int priority = call.Arguments["priority"]?.Value<int>() ?? -1;
            if (priority < 0 || priority > 4)
            {
                onComplete(new ToolResult
                {
                    ToolCallId = call.Id,
                    ToolName = Name,
                    Success = false,
                    Content = "priority must be 0-4."
                });
                return;
            }

            Log.Message("[RimBot] [AGENT] [" + context.PawnLabel + "] set_work_priority(" + workTypeName + ", " + priority + ")");

            var pawn = BrainManager.FindPawnById(context.PawnId);
            if (pawn == null || !pawn.Spawned)
            {
                onComplete(new ToolResult
                {
                    ToolCallId = call.Id,
                    ToolName = Name,
                    Success = false,
                    Content = "Pawn not found or not spawned."
                });
                return;
            }

            // Find the work type def — normalize: lowercase, strip spaces/underscores
            // Then try synonyms, then gerund suffix stripping
            WorkTypeDef workDef = null;
            string normalized = ResolveSynonym(workTypeName.ToLower().Replace(" ", "").Replace("_", ""));
            foreach (var wt in DefDatabase<WorkTypeDef>.AllDefs)
            {
                string normDef = wt.defName.ToLower().Replace(" ", "").Replace("_", "");
                string normLabel = wt.labelShort.ToLower().Replace(" ", "").Replace("_", "");
                if (normDef == normalized || normLabel == normalized)
                {
                    workDef = wt;
                    break;
                }
            }
            // Strip gerund suffix and retry: "constructing" → "construct", "researching" → "research"
            if (workDef == null && normalized.Length > 4 && normalized.EndsWith("ing"))
            {
                string stemmed = normalized.Substring(0, normalized.Length - 3);
                foreach (var wt in DefDatabase<WorkTypeDef>.AllDefs)
                {
                    string normDef = wt.defName.ToLower().Replace(" ", "").Replace("_", "");
                    string normLabel = wt.labelShort.ToLower().Replace(" ", "").Replace("_", "");
                    if (normDef == stemmed || normLabel == stemmed ||
                        normDef.StartsWith(stemmed) || normLabel.StartsWith(stemmed))
                    {
                        workDef = wt;
                        break;
                    }
                }
            }

            if (workDef == null)
            {
                var available = new System.Collections.Generic.List<string>();
                foreach (var wt in DefDatabase<WorkTypeDef>.AllDefs)
                    available.Add(wt.labelShort);
                onComplete(new ToolResult
                {
                    ToolCallId = call.Id,
                    ToolName = Name,
                    Success = false,
                    Content = "Unknown work type '" + workTypeName + "'. Available: " + string.Join(", ", available)
                });
                return;
            }

            if (pawn.WorkTypeIsDisabled(workDef))
            {
                onComplete(new ToolResult
                {
                    ToolCallId = call.Id,
                    ToolName = Name,
                    Success = false,
                    Content = pawn.LabelShort + " is permanently incapable of " + workDef.labelShort + "."
                });
                return;
            }

            pawn.workSettings.SetPriority(workDef, priority);

            string statusLabel = priority == 0 ? "disabled" : "priority " + priority;
            onComplete(new ToolResult
            {
                ToolCallId = call.Id,
                ToolName = Name,
                Success = true,
                Content = "Set " + pawn.LabelShort + "'s " + workDef.labelShort + " to " + statusLabel + "."
            });
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
