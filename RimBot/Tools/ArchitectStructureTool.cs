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

        public ToolDefinition GetDefinition()
        {
            return new ToolDefinition
            {
                Name = Name,
                Description = "Place wall or door blueprints at specified coordinates relative to your position. " +
                    "Coordinates are relative: (0,0) is your position, +X is east, +Z is north. " +
                    "Blueprints will be placed for colonists to build.",
                ParametersJson = "{\"type\":\"object\",\"properties\":{" +
                    "\"coordinates\":{\"type\":\"array\",\"items\":{\"type\":\"object\"," +
                    "\"properties\":{\"x\":{\"type\":\"integer\"},\"z\":{\"type\":\"integer\"}}," +
                    "\"required\":[\"x\",\"z\"]},\"description\":\"Relative coordinates for placement\"}," +
                    "\"structure_type\":{\"type\":\"string\",\"enum\":[\"wall\",\"door\"]," +
                    "\"description\":\"Structure to build (default: wall)\"}},\"required\":[\"coordinates\"]}"
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

            var map = context.Map;
            var observerPos = context.PawnPosition;

            ThingDef buildDef;
            if (structureType == "door")
                buildDef = ThingDefOf.Door;
            else
                buildDef = ThingDefOf.Wall;

            var stuffDef = ThingDefOf.WoodLog;

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

                GenConstruct.PlaceBlueprintForBuild(buildDef, cell, map, Rot4.North, Faction.OfPlayer, stuffDef);
                placed++;
            }

            Log.Message("[RimBot] [AGENT] [" + context.PawnLabel + "] architect_structure(" +
                structureType + "): placed=" + placed + " skipped=" + skipped);

            onComplete(new ToolResult
            {
                ToolCallId = call.Id,
                ToolName = Name,
                Success = true,
                Content = "Placed " + placed + " " + structureType + " blueprints (" + skipped + " skipped due to existing structures or out of bounds)."
            });
        }
    }
}
