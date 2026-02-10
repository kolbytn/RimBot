using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using RimWorld;
using Verse;

namespace RimBot.Tools
{
    public class SetResearchTool : ITool
    {
        public string Name => "set_research";

        public ToolDefinition GetDefinition()
        {
            return new ToolDefinition
            {
                Name = Name,
                Description = "Set the current research project. The project must be available (prerequisites met, not finished).",
                ParametersJson = "{\"type\":\"object\",\"properties\":{" +
                    "\"project_name\":{\"type\":\"string\",\"description\":\"Name of the research project\"}}" +
                    ",\"required\":[\"project_name\"]}"
            };
        }

        public void Execute(ToolCall call, ToolContext context, Action<ToolResult> onComplete)
        {
            string projectName = call.Arguments["project_name"]?.Value<string>();
            if (string.IsNullOrEmpty(projectName))
            {
                onComplete(new ToolResult
                {
                    ToolCallId = call.Id,
                    ToolName = Name,
                    Success = false,
                    Content = "project_name is required."
                });
                return;
            }

            Log.Message("[RimBot] [AGENT] [" + context.PawnLabel + "] set_research(" + projectName + ")");

            // Find project by label (case-insensitive)
            ResearchProjectDef found = null;
            string lower = projectName.ToLower();
            var availableNames = new List<string>();

            foreach (var proj in DefDatabase<ResearchProjectDef>.AllDefs)
            {
                if (proj.IsFinished)
                    continue;

                if (proj.CanStartNow)
                    availableNames.Add(proj.label);

                if (proj.label.ToLower() == lower || proj.defName.ToLower() == lower)
                {
                    found = proj;
                    break;
                }
            }

            if (found == null)
            {
                availableNames.Sort();
                onComplete(new ToolResult
                {
                    ToolCallId = call.Id,
                    ToolName = Name,
                    Success = false,
                    Content = "No research project named '" + projectName + "'. Available: " + string.Join(", ", availableNames)
                });
                return;
            }

            if (found.IsFinished)
            {
                onComplete(new ToolResult
                {
                    ToolCallId = call.Id,
                    ToolName = Name,
                    Success = false,
                    Content = "'" + found.label + "' is already finished."
                });
                return;
            }

            if (!found.CanStartNow)
            {
                // Show missing prerequisites
                var missing = new List<string>();
                if (found.prerequisites != null)
                {
                    foreach (var req in found.prerequisites)
                    {
                        if (!req.IsFinished)
                            missing.Add(req.label);
                    }
                }
                onComplete(new ToolResult
                {
                    ToolCallId = call.Id,
                    ToolName = Name,
                    Success = false,
                    Content = "'" + found.label + "' prerequisites not met. Needs: " + string.Join(", ", missing)
                });
                return;
            }

            Find.ResearchManager.SetCurrentProject(found);

            onComplete(new ToolResult
            {
                ToolCallId = call.Id,
                ToolName = Name,
                Success = true,
                Content = "Set current research to '" + found.label + "' (cost: " + found.CostApparent.ToString("F0") + ")."
            });
        }
    }
}
