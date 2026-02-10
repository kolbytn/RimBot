using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using RimWorld;
using Verse;

namespace RimBot.Tools
{
    public class SetAnimalOperationTool : ITool
    {
        public string Name => "set_animal_operation";

        public ToolDefinition GetDefinition()
        {
            return new ToolDefinition
            {
                Name = Name,
                Description = "Perform an operation on a tamed animal: slaughter, sterilize, release to wild, " +
                    "or change medical care level.",
                ParametersJson = "{\"type\":\"object\",\"properties\":{" +
                    "\"animal_name\":{\"type\":\"string\",\"description\":\"Name of the tamed animal\"}," +
                    "\"operation\":{\"type\":\"string\",\"enum\":[\"slaughter\",\"sterilize\",\"release\"]," +
                    "\"description\":\"Operation to perform\"}," +
                    "\"medical_care\":{\"type\":\"string\"," +
                    "\"enum\":[\"NoCare\",\"NoMeds\",\"HerbalOrWorse\",\"NormalOrWorse\",\"Best\"]," +
                    "\"description\":\"Set medical care level (optional, can be used alone or with an operation)\"}}" +
                    ",\"required\":[\"animal_name\"]}"
            };
        }

        public void Execute(ToolCall call, ToolContext context, Action<ToolResult> onComplete)
        {
            string animalName = call.Arguments["animal_name"]?.Value<string>();
            string operation = call.Arguments["operation"]?.Value<string>();
            string medCare = call.Arguments["medical_care"]?.Value<string>();

            if (string.IsNullOrEmpty(animalName))
            {
                onComplete(new ToolResult { ToolCallId = call.Id, ToolName = Name, Success = false, Content = "animal_name is required." });
                return;
            }

            if (string.IsNullOrEmpty(operation) && string.IsNullOrEmpty(medCare))
            {
                onComplete(new ToolResult { ToolCallId = call.Id, ToolName = Name, Success = false, Content = "At least one of operation or medical_care is required." });
                return;
            }

            Log.Message("[RimBot] [AGENT] [" + context.PawnLabel + "] set_animal_operation(" + animalName + ", " + (operation ?? "none") + ", med=" + (medCare ?? "none") + ")");

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

            string label = animal.Name != null ? animal.Name.ToStringShort : animal.def.label;
            var results = new List<string>();

            // Handle medical care
            if (!string.IsNullOrEmpty(medCare))
            {
                MedicalCareCategory care;
                if (ResolveMedicalCare(medCare, out care))
                {
                    if (animal.playerSettings != null)
                    {
                        animal.playerSettings.medCare = care;
                        results.Add("Set medical care to " + care.GetLabel());
                    }
                    else
                    {
                        results.Add("Cannot set medical care (no player settings)");
                    }
                }
                else
                {
                    onComplete(new ToolResult
                    {
                        ToolCallId = call.Id,
                        ToolName = Name,
                        Success = false,
                        Content = "Unknown medical care '" + medCare + "'. Valid: NoCare, NoMeds, HerbalOrWorse, NormalOrWorse, Best."
                    });
                    return;
                }
            }

            // Handle operation
            if (!string.IsNullOrEmpty(operation))
            {
                switch (operation.ToLower())
                {
                    case "slaughter":
                    {
                        var dm = map.designationManager;
                        if (dm.DesignationOn(animal, DesignationDefOf.Slaughter) != null)
                        {
                            results.Add("Already designated for slaughter");
                        }
                        else
                        {
                            dm.AddDesignation(new Designation(animal, DesignationDefOf.Slaughter));
                            results.Add("Designated " + label + " for slaughter");
                        }
                        break;
                    }
                    case "sterilize":
                    {
                        var sterilizeDef = DefDatabase<RecipeDef>.GetNamed("Sterilize", false);
                        if (sterilizeDef == null)
                        {
                            results.Add("Sterilize recipe not found");
                        }
                        else
                        {
                            animal.health.surgeryBills.AddBill(new Bill_Medical(sterilizeDef, null));
                            results.Add("Added sterilization bill for " + label);
                        }
                        break;
                    }
                    case "release":
                    {
                        animal.SetFaction(null);
                        results.Add("Released " + label + " to the wild");
                        break;
                    }
                    default:
                    {
                        onComplete(new ToolResult
                        {
                            ToolCallId = call.Id,
                            ToolName = Name,
                            Success = false,
                            Content = "Unknown operation '" + operation + "'. Valid: slaughter, sterilize, release."
                        });
                        return;
                    }
                }
            }

            onComplete(new ToolResult
            {
                ToolCallId = call.Id,
                ToolName = Name,
                Success = true,
                Content = string.Join(". ", results) + "."
            });
        }

        private static bool ResolveMedicalCare(string name, out MedicalCareCategory care)
        {
            switch (name.ToLower())
            {
                case "nocare": care = MedicalCareCategory.NoCare; return true;
                case "nomeds": care = MedicalCareCategory.NoMeds; return true;
                case "herbalorworse": care = MedicalCareCategory.HerbalOrWorse; return true;
                case "normalorworse": care = MedicalCareCategory.NormalOrWorse; return true;
                case "best": care = MedicalCareCategory.Best; return true;
                default: care = MedicalCareCategory.NoCare; return false;
            }
        }
    }
}
