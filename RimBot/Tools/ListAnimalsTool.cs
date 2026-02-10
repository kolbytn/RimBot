using System;
using System.Collections.Generic;
using System.Text;
using RimWorld;
using Verse;

namespace RimBot.Tools
{
    public class ListAnimalsTool : ITool
    {
        public string Name => "list_animals";

        public ToolDefinition GetDefinition()
        {
            return new ToolDefinition
            {
                Name = Name,
                Description = "List all tamed colony animals with species, name, master, training status, " +
                    "area restriction, and medical care level.",
                ParametersJson = "{\"type\":\"object\",\"properties\":{},\"required\":[]}"
            };
        }

        public void Execute(ToolCall call, ToolContext context, Action<ToolResult> onComplete)
        {
            Log.Message("[RimBot] [AGENT] [" + context.PawnLabel + "] list_animals()");

            var map = context.Map;
            var animals = new List<Pawn>();

            foreach (var p in map.mapPawns.PawnsInFaction(Faction.OfPlayer))
            {
                if (p.RaceProps != null && p.RaceProps.Animal)
                    animals.Add(p);
            }

            if (animals.Count == 0)
            {
                onComplete(new ToolResult
                {
                    ToolCallId = call.Id,
                    ToolName = Name,
                    Success = true,
                    Content = "No tamed animals in the colony."
                });
                return;
            }

            var sb = new StringBuilder();
            sb.AppendLine("=== Colony Animals (" + animals.Count + ") ===");

            foreach (var animal in animals)
            {
                string name = animal.Name != null ? animal.Name.ToStringShort : "unnamed";
                sb.AppendLine("\n  " + name + " (" + animal.def.label + ")");

                // Health
                float healthPct = animal.health.summaryHealth.SummaryHealthPercent;
                sb.AppendLine("    Health: " + (healthPct * 100).ToString("F0") + "%");

                // Age/sex
                sb.AppendLine("    Age: " + animal.ageTracker.AgeBiologicalYears + ", " + animal.gender);

                // Master
                if (animal.playerSettings != null && animal.playerSettings.Master != null)
                    sb.AppendLine("    Master: " + animal.playerSettings.Master.LabelShort);
                else
                    sb.AppendLine("    Master: none");

                // Area restriction
                if (animal.playerSettings != null && animal.playerSettings.AreaRestrictionInPawnCurrentMap != null)
                    sb.AppendLine("    Area: " + animal.playerSettings.AreaRestrictionInPawnCurrentMap.Label);
                else
                    sb.AppendLine("    Area: unrestricted");

                // Medical care
                if (animal.playerSettings != null)
                    sb.AppendLine("    Medical care: " + animal.playerSettings.medCare.GetLabel());

                // Training
                if (animal.training != null)
                {
                    var trainParts = new List<string>();
                    foreach (var td in DefDatabase<TrainableDef>.AllDefs)
                    {
                        if (animal.training.HasLearned(td))
                            trainParts.Add(td.label + " (learned)");
                        else if (animal.training.GetWanted(td))
                            trainParts.Add(td.label + " (wanted)");
                    }
                    if (trainParts.Count > 0)
                        sb.AppendLine("    Training: " + string.Join(", ", trainParts));
                    else
                        sb.AppendLine("    Training: none");
                }

                // Position relative to observer
                int relX = animal.Position.x - context.PawnPosition.x;
                int relZ = animal.Position.z - context.PawnPosition.z;
                sb.AppendLine("    Position: (" + relX + ", " + relZ + ") relative to you");
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
