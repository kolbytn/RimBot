using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using RimBot.Models;
using RimBot.Tools;
using UnityEngine;
using Verse;

namespace RimBot
{
    public class Brain
    {
        private enum State { Idle, WaitingForLLM }

        public int PawnId { get; }
        public string PawnLabel { get; }
        public LLMProviderType Provider { get; }
        public string Model { get; }
        public string ApiKey { get; }
        public string ProfileId { get; }

        private State state = State.Idle;
        private readonly List<HistoryEntry> history = new List<HistoryEntry>();
        private const int MaxHistoryEntries = 50;
        private List<ChatMessage> agentConversation;
        private const int ConversationTrimThreshold = 40;
        private const int ConversationTrimTarget = 24;
        private float lastRunStartedAt = float.MinValue;
        private float pauseUntil;

        public bool IsIdle => state == State.Idle;
        public float LastRunStartedAt => lastRunStartedAt;
        public bool IsPaused => Time.realtimeSinceStartup < pauseUntil;
        private const float ErrorPauseSeconds = 30f;
        public IReadOnlyList<HistoryEntry> History => history;

        public Brain(int pawnId, string label, LLMProviderType provider, string model, string apiKey, string profileId = null)
        {
            PawnId = pawnId;
            PawnLabel = label;
            Provider = provider;
            Model = model;
            ApiKey = apiKey;
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

            var profile = RimBotMod.Settings.GetProfileById(ProfileId);
            var thinkingLevel = profile?.ThinkingLevel ?? Models.ThinkingLevel.Medium;

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

                // After max iterations the conversation ends with user(tool_results).
                // Insert a synthetic assistant message to prevent consecutive user messages
                // which violates Google's alternating role requirement and causes hallucinated tool calls.
                if (agentConversation.Count > 0 && agentConversation[agentConversation.Count - 1].Role == "user")
                {
                    agentConversation.Add(new ChatMessage("assistant",
                        "I've used all my actions for this cycle. I'll reassess the situation next cycle."));
                }

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

            Action<AgentTurn, int> onTurnComplete = (turn, index) =>
            {
                BrainManager.EnqueueMainThread(() => RecordSingleTurn(turn, index, sysPromptForHistory));
            };

            Task.Run(async () =>
            {
                try
                {
                    var result = await AgentRunner.RunAgent(
                        this, messages, tools, llmModel, model, apiKey, maxTokens, thinkingLevel, toolContext, onTurnComplete);

                    BrainManager.EnqueueMainThread(() =>
                    {
                        if (result.Success)
                        {
                            Log.Message("[RimBot] [AGENT] [" + label + "] Completed in " +
                                result.Turns.Count + " iterations");
                        }
                        else
                        {
                            Log.Warning("[RimBot] [AGENT] [" + label + "] Failed: " +
                                result.ErrorMessage);

                            // Pause on rate limit or persistent API errors
                            if (IsRateLimitError(result.ErrorMessage))
                                PauseFor(ErrorPauseSeconds);
                        }

                        // Persist the conversation for next cycle
                        if (result.FinalConversation != null)
                            agentConversation = result.FinalConversation;
                    });
                }
                catch (Exception ex)
                {
                    BrainManager.EnqueueMainThread(() =>
                    {
                        Log.Warning("[RimBot] [AGENT] [" + label + "] Exception: " + ex.Message);
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
            if (agentConversation == null || agentConversation.Count <= ConversationTrimThreshold)
                return;

            // Trim down to target to maximize cache hits between trims
            int keepFromEnd = ConversationTrimTarget - 2;
            if (keepFromEnd < 2) keepFromEnd = 2;

            int startIdx = agentConversation.Count - keepFromEnd;
            if (startIdx < 2) startIdx = 2;

            // Find a clean boundary: must be a plain assistant message (no tool_use parts)
            // to ensure proper role alternation after system(0) + first user(1).
            // Skipping user messages here prevents consecutive user messages in the trimmed result.
            while (startIdx < agentConversation.Count - 2)
            {
                var msg = agentConversation[startIdx];
                if (msg.HasToolResult || msg.HasToolUse || msg.Role != "assistant")
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

        private void RecordSingleTurn(AgentTurn turn, int iterIndex, string systemPrompt)
        {
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
                SystemPrompt = iterIndex == 0 ? systemPrompt : null,
                UserQuery = null,
                ResponseText = string.IsNullOrEmpty(responseText) ? turn.ErrorMessage : responseText,
                Success = turn.ErrorMessage == null,
                Provider = Provider,
                ModelName = Model,
                ScreenshotBase64 = screenshotBase64,
                ToolCalls = toolCallRecords,
                ToolResults = toolResultRecords,
                AgentIteration = iterIndex + 1,
                ThinkingText = string.IsNullOrEmpty(thinkingText) ? null : thinkingText,
                InputTokens = turn.InputTokens,
                OutputTokens = turn.OutputTokens,
                CacheReadTokens = turn.CacheReadTokens,
                ReasoningTokens = turn.ReasoningTokens
            };

            history.Insert(0, entry);

            while (history.Count > MaxHistoryEntries)
            {
                history[history.Count - 1].DisposeTexture();
                history.RemoveAt(history.Count - 1);
            }
        }

    }
}
