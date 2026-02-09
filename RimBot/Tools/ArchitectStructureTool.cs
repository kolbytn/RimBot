using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using RimWorld;
using Verse;

namespace RimBot.Tools
{
    public class ArchitectStructureTool : ITool
    {
        public string Name => "architect_structure";

        // Maps tool enum value -> ThingDef defName
        private static readonly Dictionary<string, string> structureDefNames = new Dictionary<string, string>
        {
            // Structural
            { "wall", "Wall" },
            { "door", "Door" },
            { "column", "Column" },
            { "fence", "Fence" },
            { "fence_gate", "FenceGate" },
            // Furniture — no research required
            { "stool", "Stool" },
            { "table_1x2", "Table1x2c" },
            { "table_2x2", "Table2x2c" },
            { "table_2x4", "Table2x4c" },
            { "table_3x3", "Table3x3c" },
            { "sleeping_spot", "SleepingSpot" },
            { "double_sleeping_spot", "DoubleSleepingSpot" },
            { "torch_lamp", "TorchLamp" },
            { "campfire", "Campfire" },
            { "plant_pot", "PlantPot" },
            // Furniture — requires ComplexFurniture (pre-researched for crashlanded)
            { "bed", "Bed" },
            { "double_bed", "DoubleBed" },
            { "dining_chair", "DiningChair" },
            { "shelf", "Shelf" },
            // Production — no research required
            { "crafting_spot", "CraftingSpot" },
            { "butcher_spot", "ButcherSpot" },
            { "butcher_table", "TableButcher" },
            { "simple_research_bench", "SimpleResearchBench" },
            { "art_bench", "TableSculpting" },
            // Security
            { "barricade", "Barricade" },
            { "spike_trap", "TrapSpike" },
            // Animals
            { "animal_sleeping_spot", "AnimalSleepingSpot" },
            { "animal_sleeping_box", "AnimalSleepingBox" },
            { "pen_marker", "PenMarker" },
            { "egg_box", "EggBox" },
            // Misc
            { "horseshoes_pin", "HorseshoesPin" },
            { "grave", "Grave" },
        };

        public ToolDefinition GetDefinition()
        {
            // Build enum list from the dictionary keys
            var enumValues = new List<string>(structureDefNames.Keys);
            var enumJson = "[\"" + string.Join("\",\"", enumValues) + "\"]";

            return new ToolDefinition
            {
                Name = Name,
                Description = "Place structure blueprints at specified coordinates relative to your position. " +
                    "Coordinates are relative: (0,0) is your position, +X is east, +Z is north. " +
                    "Blueprints will be placed for colonists to build. Wood is used as material when applicable. " +
                    "Multi-tile structures (beds, tables, benches) need correct rotation to fit.",
                ParametersJson = "{\"type\":\"object\",\"properties\":{" +
                    "\"coordinates\":{\"type\":\"array\",\"items\":{\"type\":\"object\"," +
                    "\"properties\":{\"x\":{\"type\":\"integer\"},\"z\":{\"type\":\"integer\"}}," +
                    "\"required\":[\"x\",\"z\"]},\"description\":\"Relative coordinates for placement\"}," +
                    "\"structure_type\":{\"type\":\"string\",\"enum\":" + enumJson + "," +
                    "\"description\":\"Structure to build (default: wall)\"}," +
                    "\"rotation\":{\"type\":\"integer\"," +
                    "\"description\":\"Rotation: 0=north, 1=east, 2=south, 3=west (default: 0). Affects multi-tile structures.\",\"minimum\":0,\"maximum\":3}}" +
                    ",\"required\":[\"coordinates\"]}"
            };
        }

        public void Execute(ToolCall call, ToolContext context, Action<ToolResult> onComplete)
        {
            var coordsArray = call.Arguments["coordinates"] as JArray;
            if (coordsArray == null || coordsArray.Count == 0)
            {
                onComplete(new ToolResult
                {
                    ToolCallId = call.Id,
                    ToolName = Name,
                    Success = false,
                    Content = "No coordinates provided."
                });
                return;
            }

            string structureType = "wall";
            if (call.Arguments["structure_type"] != null)
                structureType = call.Arguments["structure_type"].ToString();

            int rotInt = 0;
            if (call.Arguments["rotation"] != null)
                rotInt = call.Arguments["rotation"].Value<int>();
            var rotation = new Rot4(rotInt);

            // Resolve ThingDef
            string defName;
            if (!structureDefNames.TryGetValue(structureType, out defName))
            {
                onComplete(new ToolResult
                {
                    ToolCallId = call.Id,
                    ToolName = Name,
                    Success = false,
                    Content = "Unknown structure_type '" + structureType + "'. Valid types: " +
                        string.Join(", ", new List<string>(structureDefNames.Keys))
                });
                return;
            }

            var buildDef = DefDatabase<ThingDef>.GetNamed(defName, false);
            if (buildDef == null)
            {
                onComplete(new ToolResult
                {
                    ToolCallId = call.Id,
                    ToolName = Name,
                    Success = false,
                    Content = "ThingDef '" + defName + "' not found in game database."
                });
                return;
            }

            // Determine stuff: use WoodLog for stuff-based structures, null otherwise
            ThingDef stuffDef = buildDef.MadeFromStuff ? ThingDefOf.WoodLog : null;

            var map = context.Map;
            var observerPos = context.PawnPosition;

            int placed = 0;
            int skipped = 0;

            foreach (var coordObj in coordsArray)
            {
                int rx = coordObj["x"]?.Value<int>() ?? 0;
                int rz = coordObj["z"]?.Value<int>() ?? 0;
                var cell = new IntVec3(observerPos.x + rx, 0, observerPos.z + rz);

                if (!cell.InBounds(map))
                {
                    skipped++;
                    continue;
                }

                if (cell.GetEdifice(map) != null)
                {
                    skipped++;
                    continue;
                }

                bool hasBlueprint = false;
                var thingList = cell.GetThingList(map);
                for (int i = 0; i < thingList.Count; i++)
                {
                    if (thingList[i] is Blueprint)
                    {
                        hasBlueprint = true;
                        break;
                    }
                }
                if (hasBlueprint)
                {
                    skipped++;
                    continue;
                }

                GenConstruct.PlaceBlueprintForBuild(buildDef, cell, map, rotation, Faction.OfPlayer, stuffDef);
                placed++;
            }

            Log.Message("[RimBot] [AGENT] [" + context.PawnLabel + "] architect_structure(" +
                structureType + ", rot=" + rotInt + "): placed=" + placed + " skipped=" + skipped);

            onComplete(new ToolResult
            {
                ToolCallId = call.Id,
                ToolName = Name,
                Success = true,
                Content = "Placed " + placed + " " + structureType + " blueprints (" + skipped +
                    " skipped due to existing structures or out of bounds)."
            });
        }
    }
}
