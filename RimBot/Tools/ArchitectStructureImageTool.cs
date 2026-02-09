using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using RimBot.Models;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimBot.Tools
{
    public class ArchitectStructureImageTool : ITool
    {
        public string Name => "architect_structure_image";

        public ToolDefinition GetDefinition()
        {
            return new ToolDefinition
            {
                Name = Name,
                Description = "Place structures using image-based mask generation. Describe what and where to build " +
                    "relative to your position, and an image model will generate a binary mask of placement locations. " +
                    "Only works with providers that support image output (Google, OpenAI).",
                ParametersJson = "{\"type\":\"object\",\"properties\":{" +
                    "\"description\":{\"type\":\"string\"," +
                    "\"description\":\"Natural language description of where and what to build relative to your position\"}," +
                    "\"structure_type\":{\"type\":\"string\",\"enum\":[\"wall\",\"door\"]," +
                    "\"description\":\"Structure to build (default: wall)\"}},\"required\":[\"description\"]}"
            };
        }

        public void Execute(ToolCall call, ToolContext context, Action<ToolResult> onComplete)
        {
            string description = call.Arguments["description"]?.ToString();
            if (string.IsNullOrEmpty(description))
            {
                onComplete(new ToolResult
                {
                    ToolCallId = call.Id,
                    ToolName = Name,
                    Success = false,
                    Content = "No description provided."
                });
                return;
            }

            string structureType = "wall";
            if (call.Arguments["structure_type"] != null)
                structureType = call.Arguments["structure_type"].ToString();

            var brain = context.Brain;
            var llm = LLMModelFactory.GetModel(brain.Provider);

            if (!llm.SupportsImageOutput)
            {
                onComplete(new ToolResult
                {
                    ToolCallId = call.Id,
                    ToolName = Name,
                    Success = false,
                    Content = "Provider " + brain.Provider + " does not support image output. Use architect_structure with coordinates instead."
                });
                return;
            }

            Log.Message("[RimBot] [AGENT] [" + context.PawnLabel + "] architect_structure_image: " + description);

            // Step 1: Capture screenshot
            var requests = new List<ScreenshotCapture.CaptureRequest>
            {
                new ScreenshotCapture.CaptureRequest
                {
                    CenterCell = context.PawnPosition,
                    CameraSize = 24f,
                    PixelSize = 512
                }
            };

            var capturedContext = context;
            var capturedCallId = call.Id;
            var capturedStructureType = structureType;

            ScreenshotCapture.StartBatchCapture(requests, results =>
            {
                var base64 = results[0];
                if (base64 == null)
                {
                    onComplete(new ToolResult
                    {
                        ToolCallId = capturedCallId,
                        ToolName = Name,
                        Success = false,
                        Content = "Screenshot capture failed."
                    });
                    return;
                }

                // Step 2: Send to LLM for mask generation
                var maskPrompt = "You are a precise image analysis assistant. This is a top-down view of a game map. " +
                    "The image is centered on an observer at (0, 0). The visible area spans 48x48 tiles, " +
                    "from (-24,-24) to (24,24). Positive X is east (right), positive Z is north (up). " +
                    "Generate a binary mask image: a black background with white pixels where " +
                    capturedStructureType + "s should be placed. " + description + " " +
                    "Output ONLY the mask image.";

                var messages = new List<ChatMessage>
                {
                    new ChatMessage("user", new List<ContentPart>
                    {
                        ContentPart.FromText(maskPrompt),
                        ContentPart.FromImage(base64, "image/png")
                    })
                };

                var maxTokens = RimBotMod.Settings.maxTokens;

                System.Threading.Tasks.Task.Run(async () =>
                {
                    try
                    {
                        var response = await llm.SendImageRequest(messages, brain.Model, brain.ApiKey, maxTokens);
                        BrainManager.EnqueueMainThread(() =>
                        {
                            if (!response.Success || response.ImageBase64 == null)
                            {
                                onComplete(new ToolResult
                                {
                                    ToolCallId = capturedCallId,
                                    ToolName = Name,
                                    Success = false,
                                    Content = "Mask generation failed: " + (response.ErrorMessage ?? "No image in response")
                                });
                                return;
                            }

                            // Step 3: Parse mask into coordinates
                            var coords = ParseMask(response.ImageBase64);
                            if (coords.Count == 0)
                            {
                                onComplete(new ToolResult
                                {
                                    ToolCallId = capturedCallId,
                                    ToolName = Name,
                                    Success = false,
                                    Content = "Mask contained no placement locations."
                                });
                                return;
                            }

                            // Step 4: Place blueprints
                            var map = capturedContext.Map;
                            var observerPos = capturedContext.PawnPosition;
                            ThingDef buildDef = capturedStructureType == "door" ? ThingDefOf.Door : ThingDefOf.Wall;
                            var stuffDef = ThingDefOf.WoodLog;

                            int placed = 0;
                            int skipped = 0;

                            foreach (var relCoord in coords)
                            {
                                var cell = new IntVec3(observerPos.x + relCoord.x, 0, observerPos.z + relCoord.z);
                                if (!cell.InBounds(map)) { skipped++; continue; }
                                if (cell.GetEdifice(map) != null) { skipped++; continue; }

                                bool hasBlueprint = false;
                                var thingList = cell.GetThingList(map);
                                for (int i = 0; i < thingList.Count; i++)
                                {
                                    if (thingList[i] is Blueprint) { hasBlueprint = true; break; }
                                }
                                if (hasBlueprint) { skipped++; continue; }

                                GenConstruct.PlaceBlueprintForBuild(buildDef, cell, map, Rot4.North, Faction.OfPlayer, stuffDef);
                                placed++;
                            }

                            Log.Message("[RimBot] [AGENT] [" + capturedContext.PawnLabel +
                                "] architect_structure_image: placed=" + placed + " skipped=" + skipped);

                            onComplete(new ToolResult
                            {
                                ToolCallId = capturedCallId,
                                ToolName = Name,
                                Success = true,
                                Content = "Placed " + placed + " " + capturedStructureType +
                                    " blueprints via image mask (" + skipped + " skipped). Mask had " + coords.Count + " target cells."
                            });
                        });
                    }
                    catch (Exception ex)
                    {
                        BrainManager.EnqueueMainThread(() =>
                        {
                            onComplete(new ToolResult
                            {
                                ToolCallId = capturedCallId,
                                ToolName = Name,
                                Success = false,
                                Content = "Mask generation error: " + ex.Message
                            });
                        });
                    }
                });
            });
        }

        public static List<IntVec3> ParseMask(string imageBase64)
        {
            byte[] imageBytes = Convert.FromBase64String(imageBase64);
            var tex = new Texture2D(2, 2);
            var coords = new List<IntVec3>();

            if (!tex.LoadImage(imageBytes))
            {
                UnityEngine.Object.Destroy(tex);
                return coords;
            }

            int width = tex.width;
            int height = tex.height;
            var pixels = tex.GetPixels32();
            UnityEngine.Object.Destroy(tex);

            const int gridSize = 48;
            float cellW = width / (float)gridSize;
            float cellH = height / (float)gridSize;

            for (int row = 0; row < gridSize; row++)
            {
                int pyStart = (int)(row * cellH);
                int pyEnd = (int)((row + 1) * cellH);

                for (int col = 0; col < gridSize; col++)
                {
                    int pxStart = (int)(col * cellW);
                    int pxEnd = (int)((col + 1) * cellW);

                    bool hot = false;
                    for (int py = pyStart; py < pyEnd && !hot; py++)
                    {
                        for (int px = pxStart; px < pxEnd && !hot; px++)
                        {
                            var c = pixels[py * width + px];
                            if ((c.r + c.g + c.b) / 3 > 128)
                                hot = true;
                        }
                    }

                    if (hot)
                    {
                        // GetPixels32: y=0 is bottom (south). row 0 = south = tile Z -24
                        coords.Add(new IntVec3(col - 24, 0, row - 24));
                    }
                }
            }

            return coords;
        }
    }
}
