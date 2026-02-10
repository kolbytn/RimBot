using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using RimBot.Models;
using RimBot.Tools;
using Verse;

namespace RimBot
{
    public class AgentTurn
    {
        public List<ContentPart> AssistantParts;
        public List<ToolCall> ToolCalls;
        public List<ToolResult> ToolResults;
        public StopReason StopReason;
        public string ErrorMessage;
        public int InputTokens;
        public int OutputTokens;
        public int CacheReadTokens;
        public int ReasoningTokens;
    }

    public class AgentResult
    {
        public List<AgentTurn> Turns = new List<AgentTurn>();
        public List<ChatMessage> FinalConversation;
        public bool Success;
        public string ErrorMessage;
    }

    public static class AgentRunner
    {
        private const int MaxIterations = 10;

        public static async Task<AgentResult> RunAgent(
            Brain brain, List<ChatMessage> messages, List<ToolDefinition> tools,
            ILanguageModel llm, string model, string apiKey, int maxTokens,
            ThinkingLevel thinkingLevel, ToolContext toolContext, Action<AgentTurn, int> onTurnComplete = null)
        {
            var conversation = new List<ChatMessage>(messages);
            var result = new AgentResult();

            for (int i = 0; i < MaxIterations; i++)
            {
                // 1. LLM call (background thread)
                ModelResponse response;
                try
                {
                    response = await llm.SendToolRequest(conversation, tools, model, apiKey, maxTokens, thinkingLevel);
                }
                catch (Exception ex)
                {
                    result.Success = false;
                    result.ErrorMessage = "LLM call failed: " + ex.Message;
                    result.Turns.Add(new AgentTurn { ErrorMessage = result.ErrorMessage });
                    return result;
                }

                if (!response.Success)
                {
                    result.Success = false;
                    result.ErrorMessage = response.ErrorMessage;
                    result.Turns.Add(new AgentTurn { ErrorMessage = response.ErrorMessage });
                    return result;
                }

                // 2. If max tokens with no tool calls, discard this response and retry
                var hasToolCalls = response.ToolCalls != null && response.ToolCalls.Count > 0;
                if (response.StopReason == StopReason.MaxTokens && !hasToolCalls)
                {
                    Log.Warning("[RimBot] Response hit max tokens with no tool calls — discarding and retrying (iteration " + i + ").");
                    continue;
                }

                // 3. Append assistant message to conversation
                var assistantParts = response.AssistantParts ?? new List<ContentPart>();
                conversation.Add(new ChatMessage("assistant", assistantParts));

                var turn = new AgentTurn
                {
                    AssistantParts = assistantParts,
                    ToolCalls = response.ToolCalls ?? new List<ToolCall>(),
                    StopReason = response.StopReason,
                    InputTokens = response.InputTokens,
                    OutputTokens = response.OutputTokens,
                    CacheReadTokens = response.CacheReadTokens,
                    ReasoningTokens = response.ReasoningTokens
                };

                // 4. If no tool calls, done
                if (response.StopReason != StopReason.ToolUse || !hasToolCalls)
                {
                    result.Turns.Add(turn);
                    onTurnComplete?.Invoke(turn, i);
                    result.Success = true;
                    result.FinalConversation = conversation;
                    return result;
                }

                // 4. Execute tools on main thread via TaskCompletionSource bridge
                List<ToolResult> toolResults;
                try
                {
                    toolResults = await ExecuteToolsOnMainThread(response.ToolCalls, toolContext);
                }
                catch (Exception ex)
                {
                    result.Success = false;
                    result.ErrorMessage = "Tool execution failed: " + ex.Message;
                    turn.ErrorMessage = result.ErrorMessage;
                    result.Turns.Add(turn);
                    return result;
                }

                turn.ToolResults = toolResults;
                result.Turns.Add(turn);
                onTurnComplete?.Invoke(turn, i);

                // 5. Append tool results as user message and loop
                var resultParts = new List<ContentPart>();
                foreach (var tr in toolResults)
                {
                    resultParts.Add(ContentPart.FromToolResult(
                        tr.ToolCallId, tr.ToolName, tr.Success,
                        tr.Content, tr.ImageBase64, tr.ImageMediaType));
                }
                conversation.Add(new ChatMessage("user", resultParts));
            }

            result.Success = true;
            result.ErrorMessage = "Reached max iterations (" + MaxIterations + ")";
            result.FinalConversation = conversation;
            return result;
        }

        private static Task<List<ToolResult>> ExecuteToolsOnMainThread(List<ToolCall> toolCalls, ToolContext context)
        {
            var tcs = new TaskCompletionSource<List<ToolResult>>();

            BrainManager.EnqueueMainThread(() =>
            {
                var results = new List<ToolResult>();
                int remaining = toolCalls.Count;
                var lockObj = new object();

                for (int i = 0; i < toolCalls.Count; i++)
                {
                    var call = toolCalls[i];
                    var tool = ToolRegistry.GetTool(call.Name);

                    if (tool == null)
                    {
                        lock (lockObj)
                        {
                            results.Add(new ToolResult
                            {
                                ToolCallId = call.Id,
                                ToolName = call.Name,
                                Success = false,
                                Content = "Unknown tool: " + call.Name
                            });
                            remaining--;
                            if (remaining == 0)
                                tcs.TrySetResult(results);
                        }
                        continue;
                    }

                    try
                    {
                        tool.Execute(call, context, toolResult =>
                        {
                            lock (lockObj)
                            {
                                results.Add(toolResult);
                                remaining--;
                                if (remaining == 0)
                                    tcs.TrySetResult(results);
                            }
                        });
                    }
                    catch (Exception ex)
                    {
                        lock (lockObj)
                        {
                            results.Add(new ToolResult
                            {
                                ToolCallId = call.Id,
                                ToolName = call.Name,
                                Success = false,
                                Content = "Tool execution error: " + ex.Message
                            });
                            remaining--;
                            if (remaining == 0)
                                tcs.TrySetResult(results);
                        }
                    }
                }
            });

            return tcs.Task;
        }
    }
}
