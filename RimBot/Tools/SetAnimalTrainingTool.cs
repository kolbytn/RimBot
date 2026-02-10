using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using RimWorld;
using Verse;

namespace RimBot.Tools
{
    public class SetAnimalTrainingTool : ITool
    {
        public string Name => "set_animal_training";

        public ToolDefinition GetDefinition()
        {
            return new ToolDefinition
            {
                Name = Name,
                Description = "Set whether a training type is wanted for a tamed animal. " +
                    "Training prerequisites are handled automatically.",
                ParametersJson = "{\"type\":\"object\",\"properties\":{" +
                    "\"animal_name\":{\"type\":\"string\",\"description\":\"Name of the tamed animal\"}," +
                    "\"trainable\":{\"type\":\"string\",\"description\":\"Training type (e.g. Obedience, Release, Rescue, Haul)\"}," +
                    "\"enabled\":{\"type\":\"boolean\",\"description\":\"true to enable training, false to disable\"}}" +
                    ",\"required\":[\"animal_name\",\"trainable\",\"enabled\"]}"
            };
        }

        public void Execute(ToolCall call, ToolContext context, Action<ToolResult> onComplete)
        {
            string animalName = call.Arguments["animal_name"]?.Value<string>();
            string trainableName = call.Arguments["trainable"]?.Value<string>();
            bool enabled = call.Arguments["enabled"]?.Value<bool>() ?? true;

            if (string.IsNullOrEmpty(animalName))
            {
                onComplete(new ToolResult { ToolCallId = call.Id, ToolName = Name, Success = false, Content = "animal_name is required." });
                return;
            }
            if (string.IsNullOrEmpty(trainableName))
            {
                onComplete(new ToolResult { ToolCallId = call.Id, ToolName = Name, Success = false, Content = "trainable is required." });
                return;
            }

            Log.Message("[RimBot] [AGENT] [" + context.PawnLabel + "] set_animal_training(" + animalName + ", " + trainableName + ", " + enabled + ")");

            var map = context.Map;
            Pawn animal = FindAnimalByName(map, animalName);
            if (animal == null)
            {
                onComplete(new ToolResult
                {
                    ToolCallId = call.Id,
                    ToolName = Name,
                    Success = false,
                    Content = "No tamed animal named '" + animalName + "'. " + GetAnimalNames(map)
                });
                return;
            }

            // Find trainable def
            TrainableDef trainDef = null;
            string lowerTrainable = trainableName.ToLower();
            foreach (var td in DefDatabase<TrainableDef>.AllDefs)
            {
                if (td.defName.ToLower() == lowerTrainable || td.label.ToLower() == lowerTrainable)
                {
                    trainDef = td;
                    break;
                }
            }

            if (trainDef == null)
            {
                var available = new List<string>();
                foreach (var td in DefDatabase<TrainableDef>.AllDefs)
                    available.Add(td.label);
                onComplete(new ToolResult
                {
                    ToolCallId = call.Id,
                    ToolName = Name,
                    Success = false,
                    Content = "Unknown trainable '" + trainableName + "'. Available: " + string.Join(", ", available)
                });
                return;
            }

            if (animal.training == null)
            {
                onComplete(new ToolResult
                {
                    ToolCallId = call.Id,
                    ToolName = Name,
                    Success = false,
                    Content = animal.LabelShort + " cannot be trained."
                });
                return;
            }

            animal.training.SetWantedRecursive(trainDef, enabled);

            string action = enabled ? "enabled" : "disabled";
            string label = animal.Name != null ? animal.Name.ToStringShort : animal.def.label;
            onComplete(new ToolResult
            {
                ToolCallId = call.Id,
                ToolName = Name,
                Success = true,
                Content = action.Substring(0, 1).ToUpper() + action.Substring(1) + " " + trainDef.label + " training for " + label + "."
            });
        }

        internal static Pawn FindAnimalByName(Map map, string name)
        {
            string lower = name.ToLower();
            foreach (var p in map.mapPawns.PawnsInFaction(Faction.OfPlayer))
            {
                if (p.RaceProps != null && p.RaceProps.Animal)
                {
                    if (p.Name != null && p.Name.ToStringShort.ToLower() == lower)
                        return p;
                }
            }
            return null;
        }

        internal static string GetAnimalNames(Map map)
        {
            var names = new List<string>();
            foreach (var p in map.mapPawns.PawnsInFaction(Faction.OfPlayer))
            {
                if (p.RaceProps != null && p.RaceProps.Animal && p.Name != null)
                    names.Add(p.Name.ToStringShort);
            }
            if (names.Count == 0)
                return "No named tamed animals.";
            return "Available: " + string.Join(", ", names);
        }
    }
}
