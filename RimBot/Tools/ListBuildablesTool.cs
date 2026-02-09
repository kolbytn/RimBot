using System;
using System.Collections.Generic;
using System.Text;
using Verse;

namespace RimBot.Tools
{
    public class ListBuildablesTool : ITool
    {
        public string Name => "list_buildables";

        private static readonly Dictionary<string, string> CategoryDefNames = new Dictionary<string, string>
        {
            { "structure", "Structure" },
            { "production", "Production" },
            { "furniture", "Furniture" },
            { "power", "Power" },
            { "security", "Security" },
            { "misc", "Misc" },
            { "floors", "Floors" },
            { "ship", "Ship" },
            { "temperature", "Temperature" },
            { "joy", "Joy" }
        };

        public ToolDefinition GetDefinition()
        {
            var cats = new List<string>(CategoryDefNames.Keys);
            var enumJson = "[\"" + string.Join("\",\"", cats) + "\"]";

            return new ToolDefinition
            {
                Name = Name,
                Description = "List available buildable items for a given architect category. " +
                    "Shows defName, label, and research status. Use this to find valid item names for architect_* tools.",
                ParametersJson = "{\"type\":\"object\",\"properties\":{" +
                    "\"category\":{\"type\":\"string\",\"enum\":" + enumJson + "," +
                    "\"description\":\"The architect category to list\"}}" +
                    ",\"required\":[\"category\"]}"
            };
        }

        public void Execute(ToolCall call, ToolContext context, Action<ToolResult> onComplete)
        {
            string category = call.Arguments["category"]?.ToString();
            if (string.IsNullOrEmpty(category))
            {
                onComplete(new ToolResult
                {
                    ToolCallId = call.Id,
                    ToolName = Name,
                    Success = false,
                    Content = "category parameter is required."
                });
                return;
            }

            string defName;
            if (!CategoryDefNames.TryGetValue(category.ToLower(), out defName))
            {
                onComplete(new ToolResult
                {
                    ToolCallId = call.Id,
                    ToolName = Name,
                    Success = false,
                    Content = "Unknown category '" + category + "'. Valid: " + string.Join(", ", new List<string>(CategoryDefNames.Keys))
                });
                return;
            }

            var available = new List<string>();
            var locked = new List<string>();

            // Enumerate ThingDefs
            foreach (var td in DefDatabase<ThingDef>.AllDefs)
            {
                if (td.designationCategory == null || td.designationCategory.defName != defName)
                    continue;

                string lockedReason = GetLockedReason(td.researchPrerequisites);
                string entry = td.defName + " — " + (td.label ?? td.defName);

                if (lockedReason != null)
                    locked.Add(entry + " [LOCKED: needs " + lockedReason + "]");
                else
                    available.Add(entry);
            }

            // Enumerate TerrainDefs
            foreach (var td in DefDatabase<TerrainDef>.AllDefs)
            {
                if (td.designationCategory == null || td.designationCategory.defName != defName)
                    continue;

                string lockedReason = GetLockedReason(td.researchPrerequisites);
                string entry = td.defName + " — " + (td.label ?? td.defName);

                if (lockedReason != null)
                    locked.Add(entry + " [LOCKED: needs " + lockedReason + "]");
                else
                    available.Add(entry);
            }

            available.Sort();
            locked.Sort();

            var sb = new StringBuilder();
            sb.AppendLine("Available " + category + " items (" + available.Count + "):");
            foreach (var item in available)
                sb.AppendLine("  " + item);

            if (locked.Count > 0)
            {
                sb.AppendLine("Locked " + category + " items (" + locked.Count + "):");
                foreach (var item in locked)
                    sb.AppendLine("  " + item);
            }

            Log.Message("[RimBot] [AGENT] [" + context.PawnLabel + "] list_buildables(" + category +
                "): " + available.Count + " available, " + locked.Count + " locked");

            onComplete(new ToolResult
            {
                ToolCallId = call.Id,
                ToolName = Name,
                Success = true,
                Content = sb.ToString().TrimEnd()
            });
        }

        private static string GetLockedReason(List<ResearchProjectDef> prereqs)
        {
            if (prereqs == null || prereqs.Count == 0)
                return null;

            var missing = new List<string>();
            foreach (var req in prereqs)
            {
                if (!req.IsFinished)
                    missing.Add(req.label);
            }

            if (missing.Count == 0)
                return null;

            return string.Join(", ", missing);
        }
    }
}
