using System;
using Newtonsoft.Json.Linq;
using RimWorld;
using Verse;

namespace RimBot.Tools
{
    public class SetAnimalMasterTool : ITool
    {
        public string Name => "set_animal_master";

        public ToolDefinition GetDefinition()
        {
            return new ToolDefinition
            {
                Name = Name,
                Description = "Assign a colonist as the master of a tamed animal, or unassign the current master. " +
                    "Use master_name \"None\" to unassign.",
                ParametersJson = "{\"type\":\"object\",\"properties\":{" +
                    "\"animal_name\":{\"type\":\"string\",\"description\":\"Name of the tamed animal\"}," +
                    "\"master_name\":{\"type\":\"string\",\"description\":\"Colonist name, or 'None' to unassign\"}}" +
                    ",\"required\":[\"animal_name\",\"master_name\"]}"
            };
        }

        public void Execute(ToolCall call, ToolContext context, Action<ToolResult> onComplete)
        {
            string animalName = call.Arguments["animal_name"]?.Value<string>();
            string masterName = call.Arguments["master_name"]?.Value<string>();

            if (string.IsNullOrEmpty(animalName))
            {
                onComplete(new ToolResult { ToolCallId = call.Id, ToolName = Name, Success = false, Content = "animal_name is required." });
                return;
            }
            if (string.IsNullOrEmpty(masterName))
            {
                onComplete(new ToolResult { ToolCallId = call.Id, ToolName = Name, Success = false, Content = "master_name is required." });
                return;
            }

            Log.Message("[RimBot] [AGENT] [" + context.PawnLabel + "] set_animal_master(" + animalName + ", " + masterName + ")");

            var map = context.Map;
            Pawn animal = SetAnimalTrainingTool.FindAnimalByName(map, animalName);
            if (animal == null)
            {
                onComplete(new ToolResult
                {
                    ToolCallId = call.Id,
                    ToolName = Name,
                    Success = false,
                    Content = "No tamed animal named '" + animalName + "'. " + SetAnimalTrainingTool.GetAnimalNames(map)
                });
                return;
            }

            if (animal.playerSettings == null)
            {
                onComplete(new ToolResult
                {
                    ToolCallId = call.Id,
                    ToolName = Name,
                    Success = false,
                    Content = "Animal has no player settings."
                });
                return;
            }

            string animalLabel = animal.Name != null ? animal.Name.ToStringShort : animal.def.label;

            if (masterName.ToLower() == "none")
            {
                animal.playerSettings.Master = null;
                onComplete(new ToolResult
                {
                    ToolCallId = call.Id,
                    ToolName = Name,
                    Success = true,
                    Content = "Unassigned master from " + animalLabel + "."
                });
                return;
            }

            Pawn master = ListWorkPrioritiesTool.FindColonistByName(map, masterName);
            if (master == null)
            {
                onComplete(new ToolResult
                {
                    ToolCallId = call.Id,
                    ToolName = Name,
                    Success = false,
                    Content = "No colonist named '" + masterName + "'. Available: " + ListWorkPrioritiesTool.GetColonistNames(map)
                });
                return;
            }

            animal.playerSettings.Master = master;
            onComplete(new ToolResult
            {
                ToolCallId = call.Id,
                ToolName = Name,
                Success = true,
                Content = "Assigned " + master.LabelShort + " as master of " + animalLabel + "."
            });
        }
    }
}
