using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RimBot.Tools;

namespace RimBot.Models
{
    public class AnthropicModel : ILanguageModel
    {
        private static readonly HttpClient Client = new HttpClient();

        public LLMProviderType ProviderType => LLMProviderType.Anthropic;
        public bool SupportsImageOutput => false;

        public Task<ModelResponse> SendImageRequest(List<ChatMessage> messages, string model, string apiKey, int maxTokens)
        {
            return Task.FromResult(ModelResponse.FromError("Anthropic does not support image output."));
        }

        public string[] GetAvailableModels()
        {
            return new[]
            {
                "claude-haiku-4-5-20251001",
                "claude-sonnet-4-5-20250929"
            };
        }

        public async Task<ModelResponse> SendChatRequest(List<ChatMessage> messages, string model, string apiKey, int maxTokens)
        {
            try
            {
                var requestBody = BuildRequestBody(messages, model, maxTokens, null, 0);

                var request = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages");
                request.Headers.Add("x-api-key", apiKey);
                request.Headers.Add("anthropic-version", "2023-06-01");
                request.Content = new StringContent(requestBody.ToString(), Encoding.UTF8, "application/json");

                var response = await Client.SendAsync(request);
                var responseJson = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    var flat = responseJson.Replace("\n", " ").Replace("\r", "");
                    return ModelResponse.FromError("Anthropic API error (" + response.StatusCode + "): " + flat);
                }

                var parsed = JObject.Parse(responseJson);
                var content = parsed["content"]?[0]?["text"]?.ToString();
                var usage = parsed["usage"];
                int inputTokens = usage?["input_tokens"]?.Value<int>() ?? 0;
                int outputTokens = usage?["output_tokens"]?.Value<int>() ?? 0;
                int cacheRead = usage?["cache_read_input_tokens"]?.Value<int>() ?? 0;

                return new ModelResponse
                {
                    Success = true,
                    Content = content,
                    RawJson = responseJson,
                    TokensUsed = inputTokens + outputTokens,
                    InputTokens = inputTokens,
                    OutputTokens = outputTokens,
                    CacheReadTokens = cacheRead
                };
            }
            catch (Exception ex)
            {
                return ModelResponse.FromError("Anthropic request failed: " + ex.Message);
            }
        }

        public async Task<ModelResponse> SendToolRequest(List<ChatMessage> messages, List<ToolDefinition> tools,
            string model, string apiKey, int maxTokens)
        {
            try
            {
                int thinkingBudget = RimBotMod.Settings.thinkingBudget;
                var requestBody = BuildRequestBody(messages, model, maxTokens, tools, thinkingBudget);

                var request = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages");
                request.Headers.Add("x-api-key", apiKey);
                request.Headers.Add("anthropic-version", "2023-06-01");
                if (thinkingBudget > 0)
                    request.Headers.Add("anthropic-beta", "interleaved-thinking-2025-05-14");
                request.Content = new StringContent(requestBody.ToString(), Encoding.UTF8, "application/json");

                var response = await Client.SendAsync(request);
                var responseJson = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    var flat = responseJson.Replace("\n", " ").Replace("\r", "");
                    return ModelResponse.FromError("Anthropic API error (" + response.StatusCode + "): " + flat);
                }

                return ParseToolResponse(responseJson);
            }
            catch (Exception ex)
            {
                return ModelResponse.FromError("Anthropic tool request failed: " + ex.Message);
            }
        }

        private static JObject BuildRequestBody(List<ChatMessage> messages, string model, int maxTokens,
            List<ToolDefinition> tools, int thinkingBudget)
        {
            var systemMessages = messages.Where(m => m.Role == "system").ToList();
            var nonSystemMessages = messages.Where(m => m.Role != "system").ToList();

            // Ensure max_tokens >= thinking budget when thinking is enabled
            if (thinkingBudget > 0 && maxTokens < thinkingBudget + 1024)
                maxTokens = thinkingBudget + 1024;

            var requestBody = new JObject
            {
                ["model"] = model,
                ["max_tokens"] = maxTokens
            };

            // Enable extended thinking if budget > 0
            if (thinkingBudget > 0)
            {
                requestBody["thinking"] = new JObject
                {
                    ["type"] = "enabled",
                    ["budget_tokens"] = thinkingBudget
                };
            }

            if (systemMessages.Any())
            {
                requestBody["system"] = string.Join("\n\n", systemMessages.Select(m => m.Content));
            }

            // Add tools if provided
            if (tools != null && tools.Count > 0)
            {
                var toolsArray = new JArray();
                foreach (var tool in tools)
                {
                    toolsArray.Add(new JObject
                    {
                        ["name"] = tool.Name,
                        ["description"] = tool.Description,
                        ["input_schema"] = JObject.Parse(tool.ParametersJson)
                    });
                }
                requestBody["tools"] = toolsArray;
            }

            var messagesArray = new JArray();
            foreach (var msg in nonSystemMessages)
            {
                var msgObj = new JObject { ["role"] = msg.Role };
                var contentArray = new JArray();

                foreach (var part in msg.ContentParts)
                {
                    if (part.Type == "thinking")
                    {
                        // Serialize thinking parts for multi-turn context
                        if (part.IsRedacted)
                        {
                            contentArray.Add(new JObject
                            {
                                ["type"] = "redacted_thinking",
                                ["data"] = part.RedactedData
                            });
                        }
                        else
                        {
                            contentArray.Add(new JObject
                            {
                                ["type"] = "thinking",
                                ["thinking"] = part.Text ?? "",
                                ["signature"] = part.Signature ?? ""
                            });
                        }
                    }
                    else if (part.Type == "text")
                    {
                        contentArray.Add(new JObject
                        {
                            ["type"] = "text",
                            ["text"] = part.Text
                        });
                    }
                    else if (part.Type == "image_url")
                    {
                        contentArray.Add(new JObject
                        {
                            ["type"] = "image",
                            ["source"] = new JObject
                            {
                                ["type"] = "base64",
                                ["media_type"] = part.MediaType,
                                ["data"] = part.Base64Data
                            }
                        });
                    }
                    else if (part.Type == "tool_use")
                    {
                        contentArray.Add(new JObject
                        {
                            ["type"] = "tool_use",
                            ["id"] = part.ToolCallId,
                            ["name"] = part.ToolName,
                            ["input"] = part.ToolArguments ?? new JObject()
                        });
                    }
                    else if (part.Type == "tool_result")
                    {
                        var resultContent = new JArray();
                        if (!string.IsNullOrEmpty(part.Text))
                        {
                            resultContent.Add(new JObject
                            {
                                ["type"] = "text",
                                ["text"] = part.Text
                            });
                        }
                        if (!string.IsNullOrEmpty(part.Base64Data))
                        {
                            resultContent.Add(new JObject
                            {
                                ["type"] = "image",
                                ["source"] = new JObject
                                {
                                    ["type"] = "base64",
                                    ["media_type"] = part.MediaType ?? "image/png",
                                    ["data"] = part.Base64Data
                                }
                            });
                        }
                        contentArray.Add(new JObject
                        {
                            ["type"] = "tool_result",
                            ["tool_use_id"] = part.ToolCallId,
                            ["content"] = resultContent
                        });
                    }
                }

                msgObj["content"] = contentArray;
                messagesArray.Add(msgObj);
            }
            requestBody["messages"] = messagesArray;

            return requestBody;
        }

        private static ModelResponse ParseToolResponse(string responseJson)
        {
            var parsed = JObject.Parse(responseJson);
            var stopReason = parsed["stop_reason"]?.ToString();
            var contentArray = parsed["content"] as JArray;
            var usage = parsed["usage"];
            int inputTokens = usage?["input_tokens"]?.Value<int>() ?? 0;
            int outputTokens = usage?["output_tokens"]?.Value<int>() ?? 0;
            int cacheRead = usage?["cache_read_input_tokens"]?.Value<int>() ?? 0;

            var assistantParts = new List<ContentPart>();
            var toolCalls = new List<ToolCall>();
            string textContent = null;

            if (contentArray != null)
            {
                foreach (var item in contentArray)
                {
                    var type = item["type"]?.ToString();
                    if (type == "text")
                    {
                        var text = item["text"]?.ToString();
                        assistantParts.Add(ContentPart.FromText(text));
                        if (textContent == null)
                            textContent = text;
                    }
                    else if (type == "tool_use")
                    {
                        var id = item["id"]?.ToString();
                        var name = item["name"]?.ToString();
                        var input = item["input"] as JObject ?? new JObject();

                        assistantParts.Add(ContentPart.FromToolUse(id, name, input));
                        toolCalls.Add(new ToolCall { Id = id, Name = name, Arguments = input });
                    }
                    else if (type == "thinking")
                    {
                        var thinkingText = item["thinking"]?.ToString();
                        var signature = item["signature"]?.ToString();
                        assistantParts.Add(ContentPart.FromThinking(null, thinkingText, signature));
                    }
                    else if (type == "redacted_thinking")
                    {
                        var data = item["data"]?.ToString();
                        assistantParts.Add(ContentPart.FromRedactedThinking(data));
                    }
                }
            }

            return new ModelResponse
            {
                Success = true,
                Content = textContent,
                RawJson = responseJson,
                TokensUsed = inputTokens + outputTokens,
                InputTokens = inputTokens,
                OutputTokens = outputTokens,
                CacheReadTokens = cacheRead,
                StopReason = stopReason == "tool_use" ? StopReason.ToolUse : StopReason.EndTurn,
                ToolCalls = toolCalls,
                AssistantParts = assistantParts
            };
        }
    }
}
