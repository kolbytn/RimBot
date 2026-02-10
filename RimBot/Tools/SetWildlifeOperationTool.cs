using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using RimWorld;
using Verse;

namespace RimBot.Tools
{
    public class SetWildlifeOperationTool : ITool
    {
        public string Name => "set_wildlife_operation";

        public ToolDefinition GetDefinition()
        {
            return new ToolDefinition
            {
                Name = Name,
                Description = "Designate a wild animal for hunting, taming, or cancel an existing designation.",
                ParametersJson = "{\"type\":\"object\",\"properties\":{" +
                    "\"animal_name\":{\"type\":\"string\",\"description\":\"Name or label of the wild animal\"}," +
                    "\"operation\":{\"type\":\"string\",\"enum\":[\"hunt\",\"tame\",\"cancel\"]," +
                    "\"description\":\"Operation to perform\"}}" +
                    ",\"required\":[\"animal_name\",\"operation\"]}"
            };
        }

        public void Execute(ToolCall call, ToolContext context, Action<ToolResult> onComplete)
        {
            string animalName = call.Arguments["animal_name"]?.Value<string>();
            string operation = call.Arguments["operation"]?.Value<string>();

            if (string.IsNullOrEmpty(animalName))
            {
                onComplete(new ToolResult { ToolCallId = call.Id, ToolName = Name, Success = false, Content = "animal_name is required." });
                return;
            }
            if (string.IsNullOrEmpty(operation))
            {
                onComplete(new ToolResult { ToolCallId = call.Id, ToolName = Name, Success = false, Content = "operation is required." });
                return;
            }

            Log.Message("[RimBot] [AGENT] [" + context.PawnLabel + "] set_wildlife_operation(" + animalName + ", " + operation + ")");

            var map = context.Map;
            var dm = map.designationManager;

            // Find wild animal by name or label
            Pawn animal = null;
            string lower = animalName.ToLower();
            var wildNames = new List<string>();

            foreach (var p in map.mapPawns.AllPawnsSpawned)
            {
                if (p.RaceProps == null || !p.RaceProps.Animal || p.Faction != null)
                    continue;

                string name = p.Name != null ? p.Name.ToStringShort : p.LabelShort;
                wildNames.Add(name + " (" + p.def.label + ")");

                if (name.ToLower() == lower || p.def.label.ToLower() == lower)
                {
                    animal = p;
                    break;
                }
            }

            if (animal == null)
            {
                string available = wildNames.Count > 0 ? "Available: " + string.Join(", ", wildNames) : "No wild animals on map.";
                onComplete(new ToolResult
                {
                    ToolCallId = call.Id,
                    ToolName = Name,
                    Success = false,
                    Content = "No wild animal named '" + animalName + "'. " + available
                });
                return;
            }

            string label = animal.Name != null ? animal.Name.ToStringShort : animal.LabelShort;

            switch (operation.ToLower())
            {
                case "hunt":
                {
                    if (dm.DesignationOn(animal, DesignationDefOf.Hunt) != null)
                    {
                        onComplete(new ToolResult { ToolCallId = call.Id, ToolName = Name, Success = true, Content = label + " is already designated for hunting." });
                        return;
                    }
                    // Remove tame designation if present
                    var tameDesig = dm.DesignationOn(animal, DesignationDefOf.Tame);
                    if (tameDesig != null) dm.RemoveDesignation(tameDesig);

                    dm.AddDesignation(new Designation(animal, DesignationDefOf.Hunt));
                    onComplete(new ToolResult { ToolCallId = call.Id, ToolName = Name, Success = true, Content = "Designated " + label + " for hunting." });
                    return;
                }
                case "tame":
                {
                    if (dm.DesignationOn(animal, DesignationDefOf.Tame) != null)
                    {
                        onComplete(new ToolResult { ToolCallId = call.Id, ToolName = Name, Success = true, Content = label + " is already designated for taming." });
                        return;
                    }
                    // Remove hunt designation if present
                    var huntDesig = dm.DesignationOn(animal, DesignationDefOf.Hunt);
                    if (huntDesig != null) dm.RemoveDesignation(huntDesig);

                    dm.AddDesignation(new Designation(animal, DesignationDefOf.Tame));
                    onComplete(new ToolResult { ToolCallId = call.Id, ToolName = Name, Success = true, Content = "Designated " + label + " for taming." });
                    return;
                }
                case "cancel":
                {
                    bool cancelled = false;
                    var huntDesig = dm.DesignationOn(animal, DesignationDefOf.Hunt);
                    if (huntDesig != null) { dm.RemoveDesignation(huntDesig); cancelled = true; }
                    var tameDesig = dm.DesignationOn(animal, DesignationDefOf.Tame);
                    if (tameDesig != null) { dm.RemoveDesignation(tameDesig); cancelled = true; }

                    if (cancelled)
                        onComplete(new ToolResult { ToolCallId = call.Id, ToolName = Name, Success = true, Content = "Cancelled designations on " + label + "." });
                    else
                        onComplete(new ToolResult { ToolCallId = call.Id, ToolName = Name, Success = true, Content = label + " had no designations to cancel." });
                    return;
                }
                default:
                {
                    onComplete(new ToolResult
                    {
                        ToolCallId = call.Id,
                        ToolName = Name,
                        Success = false,
                        Content = "Unknown operation '" + operation + "'. Valid: hunt, tame, cancel."
                    });
                    return;
                }
            }
        }
    }
}
