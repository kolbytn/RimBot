using System;

namespace RimBot.Tools
{
    public class SetGoalTool : ITool
    {
        public string Name => "set_goal";

        public ToolDefinition GetDefinition()
        {
            return new ToolDefinition
            {
                Name = Name,
                Description = "Set your current goal. This goal will be shown to you at the start of every future cycle as a persistent reminder of what you're working toward. Set to empty string to clear.",
                ParametersJson = "{\"type\":\"object\",\"properties\":{" +
                    "\"goal\":{\"type\":\"string\",\"description\":\"Your current goal or objective. Describe what you are trying to accomplish. Set to empty string to clear.\"}}" +
                    ",\"required\":[\"goal\"]}"
            };
        }

        public void Execute(ToolCall call, ToolContext context, Action<ToolResult> onComplete)
        {
            string goal = (string)call.Arguments["goal"];
            if (goal == null)
            {
                onComplete(new ToolResult
                {
                    ToolCallId = call.Id,
                    ToolName = Name,
                    Success = false,
                    Content = "goal parameter is required."
                });
                return;
            }

            string previous = context.Brain.Goal;
            context.Brain.Goal = string.IsNullOrEmpty(goal) ? null : goal;

            string message;
            if (string.IsNullOrEmpty(goal))
                message = "Goal cleared.";
            else if (!string.IsNullOrEmpty(previous))
                message = "Goal updated to: " + goal;
            else
                message = "Goal set: " + goal;

            Verse.Log.Message("[RimBot] [AGENT] [" + context.PawnLabel + "] " + message);

            onComplete(new ToolResult
            {
                ToolCallId = call.Id,
                ToolName = Name,
                Success = true,
                Content = message
            });
        }
    }
}
