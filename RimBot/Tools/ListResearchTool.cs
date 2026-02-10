using System;
using System.Collections.Generic;
using System.Text;
using RimWorld;
using Verse;

namespace RimBot.Tools
{
    public class ListResearchTool : ITool
    {
        public string Name => "list_research";

        public ToolDefinition GetDefinition()
        {
            return new ToolDefinition
            {
                Name = Name,
                Description = "List the current research project and all available (unfinished, prerequisites met) research projects " +
                    "with cost and progress percentage.",
                ParametersJson = "{\"type\":\"object\",\"properties\":{},\"required\":[]}"
            };
        }

        public void Execute(ToolCall call, ToolContext context, Action<ToolResult> onComplete)
        {
            Log.Message("[RimBot] [AGENT] [" + context.PawnLabel + "] list_research()");

            var rm = Find.ResearchManager;
            var sb = new StringBuilder();

            // Current project
            var current = rm.GetProject();
            if (current != null)
            {
                sb.AppendLine("=== Current Research ===");
                sb.AppendLine("  " + current.label + " — " + (current.ProgressPercent * 100).ToString("F0") + "% (" +
                    current.ProgressApparent.ToString("F0") + "/" + current.CostApparent.ToString("F0") + ")");
            }
            else
            {
                sb.AppendLine("=== No research project selected ===");
            }

            // Available projects
            var available = new List<ResearchProjectDef>();
            var locked = new List<ResearchProjectDef>();

            foreach (var proj in DefDatabase<ResearchProjectDef>.AllDefs)
            {
                if (proj.IsFinished)
                    continue;
                if (proj == current)
                    continue;

                if (proj.CanStartNow)
                    available.Add(proj);
                else
                    locked.Add(proj);
            }

            available.Sort((a, b) => string.Compare(a.label, b.label, StringComparison.OrdinalIgnoreCase));

            sb.AppendLine("\n=== Available Research (" + available.Count + ") ===");
            foreach (var proj in available)
            {
                string progress = proj.ProgressPercent > 0
                    ? " — " + (proj.ProgressPercent * 100).ToString("F0") + "% done"
                    : "";
                sb.AppendLine("  " + proj.label + " (cost: " + proj.CostApparent.ToString("F0") + ")" + progress);
            }

            sb.AppendLine("\n(" + locked.Count + " more projects locked behind prerequisites)");

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
