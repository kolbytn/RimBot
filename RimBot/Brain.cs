using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using RimBot.Models;
using RimBot.Tools;
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
        public string ProfileId { get; }

        private State state = State.Idle;
        private readonly List<HistoryEntry> history = new List<HistoryEntry>();
        private const int MaxHistoryEntries = 50;
        private List<ChatMessage> agentConversation;
        private const int MaxConversationMessages = 30;
        private float lastRunStartedAt = float.MinValue;
        private float pauseUntil;

        public bool IsIdle => state == State.Idle;
        public float LastRunStartedAt => lastRunStartedAt;
        public bool IsPaused => Time.realtimeSinceStartup < pauseUntil;
        private const float ErrorPauseSeconds = 30f;
        public IReadOnlyList<HistoryEntry> History => history;

        public Brain(int pawnId, string label, LLMProviderType provider, string model, string apiKey, MapSelectionMode preferredMode, string profileId = null)
        {
            PawnId = pawnId;
            PawnLabel = label;
            Provider = provider;
            Model = model;
            ApiKey = apiKey;
            PreferredMode = preferredMode;
            ProfileId = profileId;
        }

        public void ClearConversation()
        {
            agentConversation = null;
            history.Clear();
        }

        public void PauseFor(float seconds)
        {
            pauseUntil = Time.realtimeSinceStartup + seconds;
            Log.Warning("[RimBot] [AGENT] [" + PawnLabel + "] Pausing for " + (int)seconds + "s");
        }

        public void RunAgentLoop()
        {
            if (state != State.Idle)
                return;

            state = State.WaitingForLLM;

            float elapsed = lastRunStartedAt > 0 ? Time.realtimeSinceStartup - lastRunStartedAt : 0;
            lastRunStartedAt = Time.realtimeSinceStartup;

            var maxTokens = RimBotMod.Settings.maxTokens;
            var llmModel = LLMModelFactory.GetModel(Provider);
            var apiKey = ApiKey;
            var model = Model;
            var label = PawnLabel;
            var pawnId = PawnId;

            // Get pawn position on main thread
            var pawn = BrainManager.FindPawnById(pawnId);
            if (pawn == null || !pawn.Spawned)
            {
                state = State.Idle;
                return;
            }

            var pawnPos = pawn.Position;
            var map = Find.CurrentMap;

            // Get pawn's current job for context
            string currentActivity = "idle";
            if (pawn.CurJob != null)
            {
                currentActivity = pawn.CurJob.def.reportString;
                if (string.IsNullOrEmpty(currentActivity))
                    currentActivity = pawn.CurJob.def.label;
            }
            if (pawn.MentalState != null)
                currentActivity = "mental break: " + pawn.MentalState.def.label;

            bool isFirstCycle = agentConversation == null;

            if (isFirstCycle)
            {
                Log.Message("[RimBot] [AGENT] [" + label + "] Starting agent loop...");

                var sysPrompt = "You are the brain of a RimWorld colonist named " + label + ". Play RimWorld. " +
                    "You have tools to observe and interact with the world. Use get_screenshot to see " +
                    "your surroundings, inspect_cell and scan_area to examine locations in detail, " +
                    "find_on_map to locate resources, get_pawn_status to check colonist status. " +
                    "Use architect_* tools to build (structure, production, furniture, power, security, " +
                    "misc, floors, ship, temperature, joy) — call list_buildables first to see available items. " +
                    "Use architect_orders for mining, harvesting, hauling, hunting, deconstructing, and more. " +
                    "Use architect_zone for stockpiles, growing zones, and area management. " +
                    "Coordinates are relative to you at (0,0). +X=east, +Z=north. " +
                    "Build rooms, expand the colony, and make decisions as you see fit.";

                agentConversation = new List<ChatMessage>
                {
                    new ChatMessage("system", sysPrompt),
                    new ChatMessage("user", "Begin. You are currently " + currentActivity +
                        ". Take a screenshot to observe your surroundings, then decide what to do.")
                };
            }
            else
            {
                int elapsedSeconds = (int)elapsed;
                Log.Message("[RimBot] [AGENT] [" + label + "] Continuing conversation (" +
                    agentConversation.Count + " messages, " + elapsedSeconds + "s elapsed)...");

                // Trim conversation if too long — keep system + first user + last N messages
                TrimConversation();

                agentConversation.Add(new ChatMessage("user",
                    "Continue. " + elapsedSeconds + " seconds have passed. You are currently " +
                    currentActivity + ". Take a screenshot to see your surroundings and decide what to do next."));
            }

            var messages = new List<ChatMessage>(agentConversation);

            ToolRegistry.EnsureInitialized();
            var tools = ToolRegistry.GetAllDefinitions();

            var toolContext = new ToolContext
            {
                PawnId = pawnId,
                PawnLabel = label,
                PawnPosition = pawnPos,
                Map = map,
                Brain = this
            };

            var sysPromptForHistory = isFirstCycle ? agentConversation[0].Content : null;

            Task.Run(async () =>
            {
                try
                {
                    var result = await AgentRunner.RunAgent(
                        this, messages, tools, llmModel, model, apiKey, maxTokens, toolContext);

                    BrainManager.EnqueueMainThread(() =>
                    {
                        if (result.Success)
                        {
                            Log.Message("[RimBot] [AGENT] [" + label + "] Completed in " +
                                result.Turns.Count + " iterations");
                        }
                        else
                        {
                            Log.Error("[RimBot] [AGENT] [" + label + "] Failed: " +
                                result.ErrorMessage);

                            // Pause on rate limit or persistent API errors
                            if (IsRateLimitError(result.ErrorMessage))
                                PauseFor(ErrorPauseSeconds);
                        }

                        // Persist the conversation for next cycle
                        if (result.FinalConversation != null)
                            agentConversation = result.FinalConversation;

                        RecordAgentHistory(result, sysPromptForHistory);
                    });
                }
                catch (Exception ex)
                {
                    BrainManager.EnqueueMainThread(() =>
                    {
                        Log.Error("[RimBot] [AGENT] [" + label + "] Exception: " + ex.Message);
                    });
                }
                finally
                {
                    state = State.Idle;
                }
            });
        }

        private static bool IsRateLimitError(string errorMessage)
        {
            if (string.IsNullOrEmpty(errorMessage))
                return false;
            // Anthropic: "rate_limit_error" / "TooManyRequests"
            // OpenAI: "Rate limit" / HTTP 429
            // Google: "RESOURCE_EXHAUSTED" / HTTP 429
            var lower = errorMessage.ToLower();
            return lower.Contains("rate_limit") || lower.Contains("rate limit") ||
                   lower.Contains("toomanyrequests") || lower.Contains("429") ||
                   lower.Contains("resource_exhausted") || lower.Contains("quota");
        }

        private void TrimConversation()
        {
            if (agentConversation == null || agentConversation.Count <= MaxConversationMessages)
                return;

            // Keep: system message (index 0) + first user message (index 1) + last N messages
            int keepFromEnd = MaxConversationMessages - 2;
            if (keepFromEnd < 2) keepFromEnd = 2;

            int startIdx = agentConversation.Count - keepFromEnd;
            if (startIdx < 2) startIdx = 2;

            // Ensure we don't start on a tool_result (user) message that has no matching
            // tool_use (assistant) message before it — scan forward to find a clean boundary.
            // A clean boundary is a user message with no tool_result parts, or an assistant
            // message with no tool_use parts (plain text response).
            while (startIdx < agentConversation.Count - 2)
            {
                var msg = agentConversation[startIdx];
                if (msg.HasToolResult || msg.HasToolUse)
                {
                    startIdx++;
                    continue;
                }
                break;
            }

            var trimmed = new List<ChatMessage>();
            trimmed.Add(agentConversation[0]); // system
            trimmed.Add(agentConversation[1]); // first user

            for (int i = startIdx; i < agentConversation.Count; i++)
                trimmed.Add(agentConversation[i]);

            agentConversation = trimmed;
            Log.Message("[RimBot] [AGENT] [" + PawnLabel + "] Trimmed conversation to " + agentConversation.Count + " messages");
        }

        private void RecordAgentHistory(AgentResult result, string systemPrompt)
        {
            for (int i = 0; i < result.Turns.Count; i++)
            {
                var turn = result.Turns[i];

                // Extract text and thinking from assistant parts
                string responseText = "";
                string thinkingText = "";
                foreach (var part in turn.AssistantParts ?? new List<ContentPart>())
                {
                    if (part.Type == "text" && !string.IsNullOrEmpty(part.Text))
                        responseText += part.Text + "\n";
                    else if (part.Type == "thinking" && !part.IsRedacted && !string.IsNullOrEmpty(part.Text))
                        thinkingText += part.Text + "\n";
                }
                responseText = responseText.TrimEnd();
                thinkingText = thinkingText.TrimEnd();

                // Find screenshot from tool results if any
                string screenshotBase64 = null;
                if (turn.ToolResults != null)
                {
                    foreach (var tr in turn.ToolResults)
                    {
                        if (!string.IsNullOrEmpty(tr.ImageBase64))
                        {
                            screenshotBase64 = tr.ImageBase64;
                            break;
                        }
                    }
                }

                // Build tool call/result records
                var toolCallRecords = new List<ToolCallRecord>();
                var toolResultRecords = new List<ToolResultRecord>();

                if (turn.ToolCalls != null)
                {
                    foreach (var tc in turn.ToolCalls)
                    {
                        toolCallRecords.Add(new ToolCallRecord
                        {
                            Id = tc.Id,
                            Name = tc.Name,
                            ArgumentsJson = tc.Arguments?.ToString(Newtonsoft.Json.Formatting.None) ?? "{}"
                        });
                    }
                }

                if (turn.ToolResults != null)
                {
                    foreach (var tr in turn.ToolResults)
                    {
                        toolResultRecords.Add(new ToolResultRecord
                        {
                            ToolCallId = tr.ToolCallId,
                            Success = tr.Success,
                            Content = tr.Content,
                            HasImage = !string.IsNullOrEmpty(tr.ImageBase64)
                        });
                    }
                }

                var entry = new HistoryEntry
                {
                    GameTick = Find.TickManager != null ? Find.TickManager.TicksGame : 0,
                    Mode = "Agent",
                    SystemPrompt = i == 0 ? systemPrompt : null,
                    UserQuery = null,
                    ResponseText = string.IsNullOrEmpty(responseText) ? turn.ErrorMessage : responseText,
                    Success = turn.ErrorMessage == null,
                    Provider = Provider,
                    ModelName = Model,
                    ScreenshotBase64 = screenshotBase64,
                    ToolCalls = toolCallRecords,
                    ToolResults = toolResultRecords,
                    AgentIteration = i + 1,
                    ThinkingText = string.IsNullOrEmpty(thinkingText) ? null : thinkingText,
                    InputTokens = turn.InputTokens,
                    OutputTokens = turn.OutputTokens,
                    CacheReadTokens = turn.CacheReadTokens,
                    ReasoningTokens = turn.ReasoningTokens
                };

                history.Insert(0, entry);
            }

            while (history.Count > MaxHistoryEntries)
            {
                history[history.Count - 1].DisposeTexture();
                history.RemoveAt(history.Count - 1);
            }
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
            var sysPrompt = "You are the inner mind of a RimWorld colonist named " + label +
                ". Describe what you see briefly from this colonist's perspective.";
            var userQuery = "What do you see?";
            var messages = new List<ChatMessage>
            {
                new ChatMessage("system", sysPrompt),
                new ChatMessage("user", new List<ContentPart>
                {
                    ContentPart.FromText(userQuery),
                    ContentPart.FromImage(base64, "image/png")
                })
            };

            var capturedBase64 = base64;
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
                            RecordHistory("Vision", sysPrompt, userQuery, capturedBase64, response.Content, true);
                        }
                        else
                        {
                            Log.Error("[RimBot] [" + label + "] Vision error: " + response.ErrorMessage);
                            RecordHistory("Vision", sysPrompt, userQuery, capturedBase64, response.ErrorMessage, false);
                        }
                    });
                }
                catch (Exception ex)
                {
                    BrainManager.EnqueueMainThread(() =>
                    {
                        Log.Error("[RimBot] [" + label + "] Vision request failed: " + ex.Message);
                        RecordHistory("Vision", sysPrompt, userQuery, capturedBase64, ex.Message, false);
                    });
                }
                finally
                {
                    state = State.Idle;
                }
            });
        }

        public void GenerateArchitectPlan(string base64, string systemPrompt, string userQuery,
            IntVec3 observerPos, Action<string, List<IntVec3>> onResult)
        {
            if (base64 == null)
                return;
            if (state != State.Idle)
                return;

            state = State.WaitingForLLM;

            var maxTokens = RimBotMod.Settings.maxTokens;
            var llmModel = LLMModelFactory.GetModel(Provider);
            var apiKey = ApiKey;
            var model = Model;
            var label = PawnLabel;
            var obsPos = observerPos;
            var capturedBase64 = base64;
            var capturedSysPrompt = systemPrompt;
            var capturedUserQuery = userQuery;

            var messages = new List<ChatMessage>
            {
                new ChatMessage("system", systemPrompt),
                new ChatMessage("user", new List<ContentPart>
                {
                    ContentPart.FromText(userQuery),
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
                            var coordMatches = Regex.Matches(response.Content ?? "", @"\((-?\d+\.?\d*),\s*(-?\d+\.?\d*)\)");
                            var worldCoords = new List<IntVec3>();
                            foreach (Match m in coordMatches)
                            {
                                double rx = double.Parse(m.Groups[1].Value);
                                double rz = double.Parse(m.Groups[2].Value);
                                worldCoords.Add(new IntVec3(
                                    obsPos.x + (int)Math.Round(rx),
                                    0,
                                    obsPos.z + (int)Math.Round(rz)));
                            }

                            Log.Message("[RimBot] [ARCHITECT] [" + label + "] LLM returned "
                                + worldCoords.Count + " coordinates");

                            if (worldCoords.Count == 0)
                            {
                                Log.Warning("[RimBot] [ARCHITECT] [" + label + "] Raw response: " + response.Content);
                            }

                            RecordHistory("Architect", capturedSysPrompt, capturedUserQuery, capturedBase64, response.Content, true);
                            onResult(label, worldCoords);
                        }
                        else
                        {
                            Log.Error("[RimBot] [ARCHITECT] [" + label + "] LLM error: " + response.ErrorMessage);
                            RecordHistory("Architect", capturedSysPrompt, capturedUserQuery, capturedBase64, response.ErrorMessage, false);
                        }
                    });
                }
                catch (Exception ex)
                {
                    BrainManager.EnqueueMainThread(() =>
                    {
                        Log.Error("[RimBot] [ARCHITECT] [" + label + "] Request failed: " + ex.Message);
                        RecordHistory("Architect", capturedSysPrompt, capturedUserQuery, capturedBase64, ex.Message, false);
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
            var capturedBase64 = base64;
            var capturedSysPrompt = systemPrompt;
            var capturedQuery = query;
            var selModeLabel = mode == MapSelectionMode.Mask ? "Selection (mask)" : "Selection (coords)";

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
                        var responseText = response.Success
                            ? (response.Content ?? response.ImageBase64 ?? "")
                            : response.ErrorMessage;
                        RecordHistory(selModeLabel, capturedSysPrompt, capturedQuery, capturedBase64,
                            responseText, response.Success);

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
                        RecordHistory(selModeLabel, capturedSysPrompt, capturedQuery, capturedBase64, ex.Message, false);
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

        private void RecordHistory(string mode, string systemPrompt, string userQuery,
            string base64, string responseText, bool success)
        {
            var entry = new HistoryEntry
            {
                GameTick = Find.TickManager != null ? Find.TickManager.TicksGame : 0,
                Mode = mode,
                SystemPrompt = systemPrompt,
                UserQuery = userQuery,
                ResponseText = responseText,
                Success = success,
                Provider = Provider,
                ModelName = Model,
                ScreenshotBase64 = base64
            };

            history.Insert(0, entry);

            while (history.Count > MaxHistoryEntries)
            {
                history[history.Count - 1].DisposeTexture();
                history.RemoveAt(history.Count - 1);
            }
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
