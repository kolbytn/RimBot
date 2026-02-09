using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json.Linq;
using RimWorld;
using Verse;

namespace RimBot.Tools
{
    public class GetPawnStatusTool : ITool
    {
        public string Name => "get_pawn_status";

        public ToolDefinition GetDefinition()
        {
            return new ToolDefinition
            {
                Name = Name,
                Description = "Get detailed colonist status: health, needs (food/rest/mood/joy), current job, skills, " +
                    "equipment, apparel. Omit pawn_name to check yourself.",
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
                Log.Message("[RimBot] [AGENT] [" + context.PawnLabel + "] get_pawn_status(self)");
            }
            else
            {
                Log.Message("[RimBot] [AGENT] [" + context.PawnLabel + "] get_pawn_status(" + pawnName + ")");
                pawn = null;
                string lower = pawnName.ToLower();
                var colonists = map.mapPawns.FreeColonistsSpawned;
                foreach (var c in colonists)
                {
                    if (c.LabelShort.ToLower() == lower)
                    {
                        pawn = c;
                        break;
                    }
                }

                if (pawn == null)
                {
                    var names = new List<string>();
                    foreach (var c in colonists)
                        names.Add(c.LabelShort);
                    onComplete(new ToolResult
                    {
                        ToolCallId = call.Id,
                        ToolName = Name,
                        Success = false,
                        Content = "No colonist named '" + pawnName + "'. Available: " + string.Join(", ", names)
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
            int relX = pawn.Position.x - context.PawnPosition.x;
            int relZ = pawn.Position.z - context.PawnPosition.z;

            // Basic info
            sb.AppendLine("=== " + pawn.LabelShort + " ===");
            sb.AppendLine("Age: " + pawn.ageTracker.AgeBiologicalYears);
            sb.AppendLine("Gender: " + pawn.gender);
            sb.AppendLine("Position: (" + relX + ", " + relZ + ") relative to you");

            // Health
            sb.AppendLine("\n--- Health ---");
            float healthPct = pawn.health.summaryHealth.SummaryHealthPercent;
            sb.AppendLine("Overall: " + (healthPct * 100).ToString("F0") + "%");
            var hediffs = pawn.health.hediffSet.hediffs;
            if (hediffs.Count > 0)
            {
                foreach (var hediff in hediffs)
                {
                    string part = hediff.Part != null ? " (" + hediff.Part.Label + ")" : "";
                    string severity = hediff.Severity > 0 ? " severity=" + hediff.Severity.ToString("F2") : "";
                    sb.AppendLine("  " + hediff.Label + part + severity);
                }
            }
            else
            {
                sb.AppendLine("  No conditions");
            }

            // Needs
            sb.AppendLine("\n--- Needs ---");
            if (pawn.needs != null)
            {
                if (pawn.needs.food != null)
                    sb.AppendLine("Food: " + (pawn.needs.food.CurLevelPercentage * 100).ToString("F0") + "%");
                if (pawn.needs.rest != null)
                    sb.AppendLine("Rest: " + (pawn.needs.rest.CurLevelPercentage * 100).ToString("F0") + "%");
                if (pawn.needs.mood != null)
                    sb.AppendLine("Mood: " + (pawn.needs.mood.CurLevelPercentage * 100).ToString("F0") + "%");
                if (pawn.needs.joy != null)
                    sb.AppendLine("Joy: " + (pawn.needs.joy.CurLevelPercentage * 100).ToString("F0") + "%");
            }

            // Current activity
            sb.AppendLine("\n--- Activity ---");
            if (pawn.MentalState != null)
            {
                sb.AppendLine("Mental state: " + pawn.MentalState.def.label);
            }
            if (pawn.CurJob != null)
            {
                string jobLabel = pawn.CurJob.def.reportString;
                if (string.IsNullOrEmpty(jobLabel))
                    jobLabel = pawn.CurJob.def.label;
                sb.AppendLine("Current job: " + jobLabel);
            }
            else
            {
                sb.AppendLine("Current job: idle");
            }

            // Skills
            sb.AppendLine("\n--- Skills ---");
            if (pawn.skills != null)
            {
                foreach (var skill in pawn.skills.skills)
                {
                    string passion = "";
                    if (skill.passion == Passion.Minor) passion = " *";
                    else if (skill.passion == Passion.Major) passion = " **";
                    sb.AppendLine("  " + skill.def.label + ": " + skill.Level + passion);
                }
            }

            // Equipment
            sb.AppendLine("\n--- Equipment ---");
            if (pawn.equipment != null && pawn.equipment.Primary != null)
                sb.AppendLine("Weapon: " + pawn.equipment.Primary.def.label);
            else
                sb.AppendLine("Weapon: none");

            // Apparel
            if (pawn.apparel != null && pawn.apparel.WornApparel != null && pawn.apparel.WornApparel.Count > 0)
            {
                var apparelNames = new List<string>();
                foreach (var a in pawn.apparel.WornApparel)
                    apparelNames.Add(a.def.label);
                sb.AppendLine("Apparel: " + string.Join(", ", apparelNames));
            }
            else
            {
                sb.AppendLine("Apparel: none");
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
