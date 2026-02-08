using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using RimBot.Models;
using UnityEngine;
using Verse;

namespace RimBot
{
    public enum MapSelectionMode { Coordinates, Mask }

    public class Brain
    {
        private enum State { Idle, WaitingForLLM }

        public int PawnId { get; }
        public string PawnLabel { get; }
        public LLMProviderType Provider { get; }
        public string Model { get; }
        public string ApiKey { get; }
        public MapSelectionMode PreferredMode { get; }

        private State state = State.Idle;

        public bool IsIdle => state == State.Idle;

        public Brain(int pawnId, string label, LLMProviderType provider, string model, string apiKey, MapSelectionMode preferredMode)
        {
            PawnId = pawnId;
            PawnLabel = label;
            Provider = provider;
            Model = model;
            ApiKey = apiKey;
            PreferredMode = preferredMode;
        }

        public void SendToLLM(string base64)
        {
            if (base64 == null)
                return;
            if (state != State.Idle)
                return;

            state = State.WaitingForLLM;
            Log.Message("[RimBot] [" + PawnLabel + "] Screenshot captured, sending to LLM...");

            var maxTokens = RimBotMod.Settings.maxTokens;
            var llmModel = LLMModelFactory.GetModel(Provider);
            var apiKey = ApiKey;
            var model = Model;
            var label = PawnLabel;
            var messages = new List<ChatMessage>
            {
                new ChatMessage("system",
                    "You are the inner mind of a RimWorld colonist named " + label +
                    ". Describe what you see briefly from this colonist's perspective."),
                new ChatMessage("user", new List<ContentPart>
                {
                    ContentPart.FromText("What do you see?"),
                    ContentPart.FromImage(base64, "image/png")
                })
            };

            Task.Run(async () =>
            {
                try
                {
                    var response = await llmModel.SendChatRequest(messages, model, apiKey, maxTokens);
                    BrainManager.EnqueueMainThread(() =>
                    {
                        if (response.Success)
                        {
                            Log.Message("[RimBot] [" + label + "] Vision: " + response.Content);
                        }
                        else
                        {
                            Log.Error("[RimBot] [" + label + "] Vision error: " + response.ErrorMessage);
                        }
                    });
                }
                catch (Exception ex)
                {
                    BrainManager.EnqueueMainThread(() =>
                    {
                        Log.Error("[RimBot] [" + label + "] Vision request failed: " + ex.Message);
                    });
                }
                finally
                {
                    state = State.Idle;
                }
            });
        }

        public void GenerateMapSelection(string base64, string query, MapSelectionMode mode, int[] expectedX, int[] expectedZ, string objectType = null)
        {
            if (base64 == null)
                return;
            if (state != State.Idle)
                return;

            var maxTokens = RimBotMod.Settings.maxTokens;
            var llmModel = LLMModelFactory.GetModel(Provider);
            var apiKey = ApiKey;
            var model = Model;
            var objType = objectType;

            if (mode == MapSelectionMode.Mask && !llmModel.SupportsImageOutput)
            {
                Log.Warning("[RimBot] [SELECT] [" + PawnLabel + "] Provider " + Provider
                    + " does not support image output, falling back to coordinates.");
                mode = MapSelectionMode.Coordinates;
            }

            state = State.WaitingForLLM;

            var modeTag = mode == MapSelectionMode.Mask ? "mask" : "coords";
            Log.Message("[RimBot] [SELECT:" + modeTag + "] [" + PawnLabel + "] query=\"" + query
                + "\" (" + expectedX.Length + " expected)");

            var label = PawnLabel;
            var systemPrompt = "You are a precise image analysis assistant. This is a top-down view of a game map. "
                + "The image is centered on an observer at (0, 0). The visible area spans 48x48 tiles, "
                + "from (-24,-24) to (24,24). Positive X is east (right), positive Z is north (up). ";

            if (mode == MapSelectionMode.Coordinates)
            {
                systemPrompt += "List the tile coordinates of every matching location. "
                    + "Respond with ONLY coordinates, one per line, in the format (x, z). Nothing else.";
            }
            else
            {
                systemPrompt += "Generate a binary mask image: a black background "
                    + "with white pixels at each indicated location. Output ONLY the mask image.";
            }

            var messages = new List<ChatMessage>
            {
                new ChatMessage("system", systemPrompt),
                new ChatMessage("user", new List<ContentPart>
                {
                    ContentPart.FromText(query),
                    ContentPart.FromImage(base64, "image/png")
                })
            };

            var expX = (int[])expectedX.Clone();
            var expZ = (int[])expectedZ.Clone();
            var selMode = mode;
            var tag = "SELECT:" + modeTag;
            var provider = Provider;

            Task.Run(async () =>
            {
                try
                {
                    ModelResponse response;
                    if (selMode == MapSelectionMode.Mask)
                        response = await llmModel.SendImageRequest(messages, model, apiKey, maxTokens);
                    else
                        response = await llmModel.SendChatRequest(messages, model, apiKey, maxTokens);

                    BrainManager.EnqueueMainThread(() =>
                    {
                        if (selMode == MapSelectionMode.Mask)
                            HandleMaskResponse(tag, label, expX, expZ, response, provider, selMode, objType);
                        else
                            HandleCoordResponse(tag, label, expX, expZ, response, provider, selMode, objType);
                    });
                }
                catch (Exception ex)
                {
                    BrainManager.EnqueueMainThread(() =>
                    {
                        Log.Error("[RimBot] [" + tag + "] [" + label + "] Request failed: " + ex.Message);
                    });
                }
                finally
                {
                    state = State.Idle;
                }
            });
        }

        private void HandleMaskResponse(string tag, string label, int[] expX, int[] expZ, ModelResponse response,
            LLMProviderType provider, MapSelectionMode mode, string objectType)
        {
            if (response.Success && response.ImageBase64 != null)
            {
                ParseMaskAndLog(tag, label, expX, expZ, response.ImageBase64, provider, mode, objectType);
            }
            else if (response.Success)
            {
                Log.Warning("[RimBot] [" + tag + "] [" + label + "] No image in response. Text: " + response.Content);
            }
            else
            {
                Log.Error("[RimBot] [" + tag + "] [" + label + "] LLM error: " + response.ErrorMessage);
            }
        }

        private void HandleCoordResponse(string tag, string label, int[] expX, int[] expZ, ModelResponse response,
            LLMProviderType provider, MapSelectionMode mode, string objectType)
        {
            if (response.Success)
            {
                var coordMatches = Regex.Matches(response.Content ?? "", @"\((-?\d+\.?\d*),\s*(-?\d+\.?\d*)\)");
                var reported = new List<double[]>();
                foreach (Match m in coordMatches)
                {
                    reported.Add(new double[] {
                        double.Parse(m.Groups[1].Value),
                        double.Parse(m.Groups[2].Value)
                    });
                }

                LogMatchResults(tag, label, expX, expZ, reported, provider, mode, objectType);

                if (reported.Count == 0)
                {
                    Log.Warning("[RimBot] [" + tag + "] [" + label + "] Raw response: " + response.Content);
                }
            }
            else
            {
                Log.Error("[RimBot] [" + tag + "] [" + label + "] LLM error: " + response.ErrorMessage);
            }
        }

        private void ParseMaskAndLog(string tag, string label, int[] expX, int[] expZ, string imageBase64,
            LLMProviderType provider, MapSelectionMode mode, string objectType)
        {
            byte[] imageBytes = Convert.FromBase64String(imageBase64);
            var tex = new Texture2D(2, 2);
            if (!tex.LoadImage(imageBytes))
            {
                Log.Error("[RimBot] [" + tag + "] [" + label + "] Failed to decode mask image.");
                return;
            }

            int width = tex.width;
            int height = tex.height;

            var pixels = tex.GetPixels32();
            UnityEngine.Object.Destroy(tex);

            // Divide mask into a 48x48 grid matching the tile view and find hot cells
            const int gridSize = 48;
            float cellW = width / (float)gridSize;
            float cellH = height / (float)gridSize;

            var reported = new List<double[]>();

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
                        reported.Add(new double[] { col - 24, row - 24 });
                    }
                }
            }

            Log.Message("[RimBot] [" + tag + "] [" + label + "] Mask: " + width + "x" + height
                + ", " + reported.Count + " hot cells");

            LogMatchResults(tag, label, expX, expZ, reported, provider, mode, objectType);
        }

        private static void LogMatchResults(string tag, string label,
            int[] expX, int[] expZ, List<double[]> reported,
            LLMProviderType provider, MapSelectionMode mode, string objectType)
        {
            var unmatched = new List<int>();
            for (int j = 0; j < expX.Length; j++)
                unmatched.Add(j);

            int matchedCount = 0;
            double totalError = 0;
            int extraCount = 0;

            foreach (var rep in reported)
            {
                int bestIdx = -1;
                double bestDist = double.MaxValue;
                foreach (int e in unmatched)
                {
                    double dx = rep[0] - expX[e];
                    double dz = rep[1] - expZ[e];
                    double dist = Math.Sqrt(dx * dx + dz * dz);
                    if (dist < bestDist)
                    {
                        bestDist = dist;
                        bestIdx = e;
                    }
                }

                if (bestIdx >= 0 && bestDist <= 3.0)
                {
                    matchedCount++;
                    totalError += bestDist;
                    unmatched.Remove(bestIdx);
                }
                else
                {
                    extraCount++;
                }
            }

            double avgError = matchedCount > 0 ? totalError / matchedCount : 0;
            Log.Message("[RimBot] [" + tag + "] [" + label + "] expected=" + expX.Length
                + " reported=" + reported.Count
                + " matched=" + matchedCount + "/" + expX.Length
                + " avg_error=" + avgError.ToString("F1") + " tiles"
                + " extra=" + extraCount);

            if (objectType != null)
            {
                SelectionTest.RecordResult(new TestResult
                {
                    PawnLabel = label,
                    Provider = provider,
                    Mode = mode,
                    ObjectType = objectType,
                    Expected = expX.Length,
                    Reported = reported.Count,
                    Matched = matchedCount,
                    AvgError = avgError,
                    Extra = extraCount
                });
            }
        }
    }
}
