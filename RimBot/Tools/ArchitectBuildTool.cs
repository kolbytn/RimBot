using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using RimWorld;
using Verse;

namespace RimBot.Tools
{
    public class ArchitectBuildTool : ITool
    {
        private readonly string categoryDefName;
        private readonly string toolName;
        private readonly string categoryLabel;

        // Map from friendly tool-name suffix to RimWorld DesignationCategoryDef defName
        private static readonly Dictionary<string, string> CategoryDefNames = new Dictionary<string, string>
        {
            { "structure", "Structure" },
            { "production", "Production" },
            { "furniture", "Furniture" },
            { "power", "Power" },
            { "security", "Security" },
            { "misc", "Misc" },
            { "floors", "Floors" },
            { "ship", "Ship" },
            { "temperature", "Temperature" },
            { "joy", "Joy" }
        };

        public ArchitectBuildTool(string category)
        {
            categoryLabel = category.ToLower();
            toolName = "architect_" + categoryLabel;

            string defName;
            if (!CategoryDefNames.TryGetValue(categoryLabel, out defName))
                defName = category; // fallback to raw name
            categoryDefName = defName;
        }

        public string Name => toolName;

        public ToolDefinition GetDefinition()
        {
            return new ToolDefinition
            {
                Name = Name,
                Description = "Place " + categoryLabel + " blueprints. Use list_buildables to see available items. " +
                    "Coordinates are relative to you at (0,0). +X=east, +Z=north. Wood is used as material where applicable.",
                ParametersJson = "{\"type\":\"object\",\"properties\":{" +
                    "\"item\":{\"type\":\"string\"," +
                    "\"description\":\"defName of the thing to build (e.g. Wall, Door, Bed). Use list_buildables to find valid names.\"}," +
                    "\"coordinates\":{\"type\":\"array\",\"items\":{\"type\":\"object\"," +
                    "\"properties\":{\"x\":{\"type\":\"integer\"},\"z\":{\"type\":\"integer\"}}," +
                    "\"required\":[\"x\",\"z\"]},\"description\":\"Relative coordinates for placement\"}," +
                    "\"rotation\":{\"type\":\"integer\"," +
                    "\"description\":\"Rotation: 0=north, 1=east, 2=south, 3=west (default: 0). Affects multi-tile structures.\",\"minimum\":0,\"maximum\":3}}" +
                    ",\"required\":[\"item\",\"coordinates\"]}"
            };
        }

        public void Execute(ToolCall call, ToolContext context, Action<ToolResult> onComplete)
        {
            string item = call.Arguments["item"]?.ToString();
            if (string.IsNullOrEmpty(item))
            {
                onComplete(new ToolResult
                {
                    ToolCallId = call.Id,
                    ToolName = Name,
                    Success = false,
                    Content = "item parameter is required."
                });
                return;
            }

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

            int rotInt = 0;
            if (call.Arguments["rotation"] != null)
                rotInt = call.Arguments["rotation"].Value<int>();
            var rotation = new Rot4(rotInt);

            // Look up def: try ThingDef first, then TerrainDef
            BuildableDef buildDef = DefDatabase<ThingDef>.GetNamed(item, false);
            if (buildDef == null)
                buildDef = DefDatabase<TerrainDef>.GetNamed(item, false);

            if (buildDef == null)
            {
                onComplete(new ToolResult
                {
                    ToolCallId = call.Id,
                    ToolName = Name,
                    Success = false,
                    Content = "Unknown item '" + item + "'. Use list_buildables to see available items for " + categoryLabel + "."
                });
                return;
            }

            // Verify the def belongs to this category
            if (buildDef.designationCategory == null || buildDef.designationCategory.defName != categoryDefName)
            {
                onComplete(new ToolResult
                {
                    ToolCallId = call.Id,
                    ToolName = Name,
                    Success = false,
                    Content = "'" + item + "' is not in the " + categoryLabel + " category. Use list_buildables to find the right tool."
                });
                return;
            }

            // Check research prerequisites
            var thingDef = buildDef as ThingDef;
            if (thingDef != null && thingDef.researchPrerequisites != null)
            {
                foreach (var req in thingDef.researchPrerequisites)
                {
                    if (!req.IsFinished)
                    {
                        onComplete(new ToolResult
                        {
                            ToolCallId = call.Id,
                            ToolName = Name,
                            Success = false,
                            Content = "'" + item + "' requires unfinished research: " + req.label + "."
                        });
                        return;
                    }
                }
            }

            // Determine stuff: use WoodLog for stuff-based things, null otherwise
            ThingDef stuffDef = null;
            if (thingDef != null && thingDef.MadeFromStuff)
                stuffDef = ThingDefOf.WoodLog;

            var map = context.Map;
            var observerPos = context.PawnPosition;

            int placed = 0;
            int skipped = 0;
            var skipReasons = new Dictionary<string, int>();

            foreach (var coordObj in coordsArray)
            {
                int rx = coordObj["x"]?.Value<int>() ?? 0;
                int rz = coordObj["z"]?.Value<int>() ?? 0;
                var cell = new IntVec3(observerPos.x + rx, 0, observerPos.z + rz);

                if (!cell.InBounds(map))
                {
                    AddSkipReason(skipReasons, "out of bounds");
                    skipped++;
                    continue;
                }

                var report = GenConstruct.CanPlaceBlueprintAt(buildDef, cell, rotation, map);
                if (!report.Accepted)
                {
                    string reason = report.Reason ?? "blocked";
                    AddSkipReason(skipReasons, reason);
                    skipped++;
                    continue;
                }

                GenConstruct.PlaceBlueprintForBuild(buildDef, cell, map, rotation, Faction.OfPlayer, stuffDef);
                placed++;
            }

            // Build skip reason summary
            string skipInfo = "";
            if (skipReasons.Count > 0)
            {
                var parts = new List<string>();
                foreach (var kvp in skipReasons)
                    parts.Add(kvp.Value + " " + kvp.Key);
                skipInfo = " (" + string.Join(", ", parts) + ")";
            }

            Log.Message("[RimBot] [AGENT] [" + context.PawnLabel + "] " + toolName + "(" +
                item + ", rot=" + rotInt + "): placed=" + placed + " skipped=" + skipped);

            onComplete(new ToolResult
            {
                ToolCallId = call.Id,
                ToolName = Name,
                Success = true,
                Content = "Placed " + placed + " " + item + " blueprints. " +
                    skipped + " skipped" + skipInfo + "."
            });
        }

        private static void AddSkipReason(Dictionary<string, int> reasons, string reason)
        {
            if (reasons.ContainsKey(reason))
                reasons[reason]++;
            else
                reasons[reason] = 1;
        }
    }
}
