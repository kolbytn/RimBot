using System;
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
                Description = "Set a colonist's priority for a specific work type. " +
                    "Priority 0 disables the work type, 1 is highest priority, 4 is lowest. " +
                    "Omit pawn_name to set your own priority.",
                ParametersJson = "{\"type\":\"object\",\"properties\":{" +
                    "\"pawn_name\":{\"type\":\"string\",\"description\":\"Colonist name (omit to set your own)\"}," +
                    "\"work_type\":{\"type\":\"string\",\"description\":\"Work type name (e.g. Mining, Cooking, Construction, Growing, etc.)\"}," +
                    "\"priority\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":4,\"description\":\"Priority level: 0=disabled, 1=highest, 4=lowest\"}}" +
                    ",\"required\":[\"work_type\",\"priority\"]}"
            };
        }

        public void Execute(ToolCall call, ToolContext context, Action<ToolResult> onComplete)
        {
            string pawnName = null;
            if (call.Arguments != null && call.Arguments["pawn_name"] != null)
                pawnName = call.Arguments["pawn_name"].Value<string>();

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

            var map = context.Map;
            Pawn pawn;

            if (string.IsNullOrEmpty(pawnName))
            {
                pawn = BrainManager.FindPawnById(context.PawnId);
                Log.Message("[RimBot] [AGENT] [" + context.PawnLabel + "] set_work_priority(self, " + workTypeName + ", " + priority + ")");
            }
            else
            {
                Log.Message("[RimBot] [AGENT] [" + context.PawnLabel + "] set_work_priority(" + pawnName + ", " + workTypeName + ", " + priority + ")");
                pawn = ListWorkPrioritiesTool.FindColonistByName(map, pawnName);
                if (pawn == null)
                {
                    onComplete(new ToolResult
                    {
                        ToolCallId = call.Id,
                        ToolName = Name,
                        Success = false,
                        Content = "No colonist named '" + pawnName + "'. Available: " + ListWorkPrioritiesTool.GetColonistNames(map)
                    });
                    return;
                }
            }

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

            // Find the work type def
            WorkTypeDef workDef = null;
            string lowerWork = workTypeName.ToLower();
            foreach (var wt in DefDatabase<WorkTypeDef>.AllDefs)
            {
                if (wt.defName.ToLower() == lowerWork || wt.labelShort.ToLower() == lowerWork)
                {
                    workDef = wt;
                    break;
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
    }
}
