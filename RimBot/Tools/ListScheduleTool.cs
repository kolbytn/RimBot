using System;
using System.Text;
using Newtonsoft.Json.Linq;
using Verse;

namespace RimBot.Tools
{
    public class ListScheduleTool : ITool
    {
        public string Name => "list_schedule";

        public ToolDefinition GetDefinition()
        {
            return new ToolDefinition
            {
                Name = Name,
                Description = "List a colonist's 24-hour schedule showing the assignment for each hour. " +
                    "Omit pawn_name to check yourself.",
                ParametersJson = "{\"type\":\"object\",\"properties\":{" +
                    "\"pawn_name\":{\"type\":\"string\",\"description\":\"Colonist name (omit to check yourself)\"}}" +
                    ",\"required\":[]}"
            };
        }

        public void Execute(ToolCall call, ToolContext context, Action<ToolResult> onComplete)
        {
            string pawnName = null;
            if (call.Arguments != null && call.Arguments["pawn_name"] != null)
                pawnName = call.Arguments["pawn_name"].Value<string>();

            var map = context.Map;
            Pawn pawn;

            if (string.IsNullOrEmpty(pawnName))
            {
                pawn = BrainManager.FindPawnById(context.PawnId);
                Log.Message("[RimBot] [AGENT] [" + context.PawnLabel + "] list_schedule(self)");
            }
            else
            {
                Log.Message("[RimBot] [AGENT] [" + context.PawnLabel + "] list_schedule(" + pawnName + ")");
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

            var sb = new StringBuilder();
            sb.AppendLine("=== Schedule for " + pawn.LabelShort + " ===");

            if (pawn.timetable == null)
            {
                sb.AppendLine("No timetable available.");
            }
            else
            {
                for (int hour = 0; hour < 24; hour++)
                {
                    var assignment = pawn.timetable.GetAssignment(hour);
                    string hourStr = hour.ToString("D2") + ":00";
                    sb.AppendLine("  " + hourStr + " - " + assignment.label);
                }
            }

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
