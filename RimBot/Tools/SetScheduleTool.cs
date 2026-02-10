using System;
using Newtonsoft.Json.Linq;
using RimWorld;
using Verse;

namespace RimBot.Tools
{
    public class SetScheduleTool : ITool
    {
        public string Name => "set_schedule";

        public ToolDefinition GetDefinition()
        {
            return new ToolDefinition
            {
                Name = Name,
                Description = "Set a colonist's schedule for a range of hours. " +
                    "Hours wrap around (e.g. from_hour=22, to_hour=6 sets 22,23,0,1,2,3,4,5,6). " +
                    "Omit pawn_name to set your own schedule.",
                ParametersJson = "{\"type\":\"object\",\"properties\":{" +
                    "\"pawn_name\":{\"type\":\"string\",\"description\":\"Colonist name (omit to set your own)\"}," +
                    "\"from_hour\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":23,\"description\":\"Starting hour (inclusive)\"}," +
                    "\"to_hour\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":23,\"description\":\"Ending hour (inclusive)\"}," +
                    "\"assignment\":{\"type\":\"string\",\"enum\":[\"Anything\",\"Work\",\"Joy\",\"Sleep\",\"Meditate\"]," +
                    "\"description\":\"The time assignment\"}}" +
                    ",\"required\":[\"from_hour\",\"to_hour\",\"assignment\"]}"
            };
        }

        public void Execute(ToolCall call, ToolContext context, Action<ToolResult> onComplete)
        {
            string pawnName = null;
            if (call.Arguments != null && call.Arguments["pawn_name"] != null)
                pawnName = call.Arguments["pawn_name"].Value<string>();

            int fromHour = call.Arguments["from_hour"]?.Value<int>() ?? -1;
            int toHour = call.Arguments["to_hour"]?.Value<int>() ?? -1;
            string assignmentName = call.Arguments["assignment"]?.Value<string>();

            if (fromHour < 0 || fromHour > 23 || toHour < 0 || toHour > 23)
            {
                onComplete(new ToolResult
                {
                    ToolCallId = call.Id,
                    ToolName = Name,
                    Success = false,
                    Content = "from_hour and to_hour must be 0-23."
                });
                return;
            }

            if (string.IsNullOrEmpty(assignmentName))
            {
                onComplete(new ToolResult
                {
                    ToolCallId = call.Id,
                    ToolName = Name,
                    Success = false,
                    Content = "assignment parameter is required."
                });
                return;
            }

            TimeAssignmentDef assignmentDef = ResolveAssignment(assignmentName);
            if (assignmentDef == null)
            {
                onComplete(new ToolResult
                {
                    ToolCallId = call.Id,
                    ToolName = Name,
                    Success = false,
                    Content = "Unknown assignment '" + assignmentName + "'. Valid: Anything, Work, Joy, Sleep, Meditate."
                });
                return;
            }

            var map = context.Map;
            Pawn pawn;

            if (string.IsNullOrEmpty(pawnName))
            {
                pawn = BrainManager.FindPawnById(context.PawnId);
                Log.Message("[RimBot] [AGENT] [" + context.PawnLabel + "] set_schedule(self, " + fromHour + "-" + toHour + ", " + assignmentName + ")");
            }
            else
            {
                Log.Message("[RimBot] [AGENT] [" + context.PawnLabel + "] set_schedule(" + pawnName + ", " + fromHour + "-" + toHour + ", " + assignmentName + ")");
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

            if (pawn.timetable == null)
            {
                onComplete(new ToolResult
                {
                    ToolCallId = call.Id,
                    ToolName = Name,
                    Success = false,
                    Content = pawn.LabelShort + " has no timetable."
                });
                return;
            }

            int count = 0;
            int hour = fromHour;
            while (true)
            {
                pawn.timetable.SetAssignment(hour, assignmentDef);
                count++;
                if (hour == toHour) break;
                hour = (hour + 1) % 24;
            }

            onComplete(new ToolResult
            {
                ToolCallId = call.Id,
                ToolName = Name,
                Success = true,
                Content = "Set " + count + " hours (" + fromHour + ":00-" + toHour + ":00) to " + assignmentName + " for " + pawn.LabelShort + "."
            });
        }

        private static TimeAssignmentDef ResolveAssignment(string name)
        {
            switch (name.ToLower())
            {
                case "anything": return TimeAssignmentDefOf.Anything;
                case "work": return TimeAssignmentDefOf.Work;
                case "joy": return TimeAssignmentDefOf.Joy;
                case "sleep": return TimeAssignmentDefOf.Sleep;
                case "meditate": return DefDatabase<TimeAssignmentDef>.GetNamed("Meditate", false);
                default: return null;
            }
        }
    }
}
