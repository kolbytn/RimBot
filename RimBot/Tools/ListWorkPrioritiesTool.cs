using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json.Linq;
using RimWorld;
using Verse;

namespace RimBot.Tools
{
    public class ListWorkPrioritiesTool : ITool
    {
        public string Name => "list_work_priorities";

        public ToolDefinition GetDefinition()
        {
            return new ToolDefinition
            {
                Name = Name,
                Description = "List all work types and their priorities for a colonist. " +
                    "Priority 0 means disabled, 1 is highest priority, 4 is lowest. " +
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
                Log.Message("[RimBot] [AGENT] [" + context.PawnLabel + "] list_work_priorities(self)");
            }
            else
            {
                Log.Message("[RimBot] [AGENT] [" + context.PawnLabel + "] list_work_priorities(" + pawnName + ")");
                pawn = FindColonistByName(map, pawnName);
                if (pawn == null)
                {
                    onComplete(new ToolResult
                    {
                        ToolCallId = call.Id,
                        ToolName = Name,
                        Success = false,
                        Content = "No colonist named '" + pawnName + "'. Available: " + GetColonistNames(map)
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
            sb.AppendLine("=== Work Priorities for " + pawn.LabelShort + " ===");

            if (pawn.workSettings == null)
            {
                sb.AppendLine("No work settings available.");
            }
            else
            {
                foreach (var workType in DefDatabase<WorkTypeDef>.AllDefs)
                {
                    bool disabled = pawn.WorkTypeIsDisabled(workType);
                    if (disabled)
                    {
                        sb.AppendLine("  " + workType.labelShort + ": PERMANENTLY DISABLED (incapable)");
                    }
                    else
                    {
                        int priority = pawn.workSettings.GetPriority(workType);
                        string status = priority == 0 ? "off" : priority.ToString();
                        sb.AppendLine("  " + workType.labelShort + ": " + status);
                    }
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

        internal static Pawn FindColonistByName(Map map, string name)
        {
            string lower = name.ToLower();
            foreach (var c in map.mapPawns.FreeColonistsSpawned)
            {
                if (c.LabelShort.ToLower() == lower)
                    return c;
            }
            return null;
        }

        internal static string GetColonistNames(Map map)
        {
            var names = new List<string>();
            foreach (var c in map.mapPawns.FreeColonistsSpawned)
                names.Add(c.LabelShort);
            return string.Join(", ", names);
        }
    }
}
