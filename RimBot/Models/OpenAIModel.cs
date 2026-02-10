using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RimBot.Tools;

namespace RimBot.Models
{
    public class OpenAIModel : ILanguageModel
    {
        private static readonly HttpClient Client = new HttpClient();

        public LLMProviderType ProviderType => LLMProviderType.OpenAI;
        public bool SupportsImageOutput => true;

        public string[] GetAvailableModels()
        {
            return new[]
            {
                "gpt-5-mini",
                "gpt-5.2"
            };
        }

        public async Task<ModelResponse> SendChatRequest(List<ChatMessage> messages, string model, string apiKey, int maxTokens)
        {
            try
            {
                var requestBody = BuildChatRequestBody(messages, model, maxTokens);

                var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                request.Content = new StringContent(requestBody.ToString(), Encoding.UTF8, "application/json");

                var response = await Client.SendAsync(request);
                var responseJson = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    var flat = responseJson.Replace("\n", " ").Replace("\r", "");
                    return ModelResponse.FromError("OpenAI API error (" + response.StatusCode + "): " + flat);
                }

                var parsed = JObject.Parse(responseJson);
                var content = parsed["choices"]?[0]?["message"]?["content"]?.ToString();
                var usage = parsed["usage"];
                int inputTokens = usage?["prompt_tokens"]?.Value<int>() ?? 0;
                int outputTokens = usage?["completion_tokens"]?.Value<int>() ?? 0;
                int reasoningTokens = usage?["completion_tokens_details"]?["reasoning_tokens"]?.Value<int>() ?? 0;
                int tokensUsed = usage?["total_tokens"]?.Value<int>() ?? 0;

                return new ModelResponse
                {
                    Success = true,
                    Content = content,
                    RawJson = responseJson,
                    TokensUsed = tokensUsed,
                    InputTokens = inputTokens,
                    OutputTokens = outputTokens,
                    ReasoningTokens = reasoningTokens
                };
            }
            catch (Exception ex)
            {
                return ModelResponse.FromError("OpenAI request failed: " + ex.Message);
            }
        }

        public async Task<ModelResponse> SendToolRequest(List<ChatMessage> messages, List<ToolDefinition> tools,
            string model, string apiKey, int maxTokens, ThinkingLevel thinkingLevel)
        {
            try
            {
                var requestBody = BuildResponsesRequestBody(messages, model, maxTokens, tools, thinkingLevel);

                var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/responses");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                request.Content = new StringContent(requestBody.ToString(), Encoding.UTF8, "application/json");

                var response = await Client.SendAsync(request);
                var responseJson = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    var flat = responseJson.Replace("\n", " ").Replace("\r", "");
                    return ModelResponse.FromError("OpenAI API error (" + response.StatusCode + "): " + flat);
                }

                return ParseResponsesApiResponse(responseJson);
            }
            catch (Exception ex)
            {
                return ModelResponse.FromError("OpenAI tool request failed: " + ex.Message);
            }
        }

        private static string MapThinkingLevelToEffort(ThinkingLevel level)
        {
            switch (level)
            {
                case ThinkingLevel.Low: return "low";
                case ThinkingLevel.Medium: return "medium";
                case ThinkingLevel.High: return "high";
                default: return null;
            }
        }

        /// <summary>
        /// Builds request body for the Responses API (/v1/responses).
        /// </summary>
        private static JObject BuildResponsesRequestBody(List<ChatMessage> messages, string model, int maxTokens,
            List<ToolDefinition> tools, ThinkingLevel thinkingLevel)
        {
            // Extract system messages into top-level instructions
            string instructions = null;
            var systemTexts = new List<string>();
            foreach (var msg in messages)
            {
                if (msg.Role == "system")
                    systemTexts.Add(msg.Content);
            }
            if (systemTexts.Count > 0)
                instructions = string.Join("\n\n", systemTexts);

            // Build input array from non-system messages
            var inputArray = new JArray();
            foreach (var msg in messages)
            {
                if (msg.Role == "system")
                    continue;

                if (msg.HasToolUse && msg.Role == "assistant")
                {
                    // Assistant text as a message item
                    string textContent = null;
                    foreach (var part in msg.ContentParts)
                    {
                        if (part.Type == "text" && !string.IsNullOrEmpty(part.Text))
                            textContent = part.Text;
                    }
                    if (textContent != null)
                    {
                        inputArray.Add(new JObject
                        {
                            ["role"] = "assistant",
                            ["content"] = new JArray(new JObject
                            {
                                ["type"] = "output_text",
                                ["text"] = textContent
                            })
                        });
                    }

                    // Each tool_use part becomes a function_call item
                    foreach (var part in msg.ToolUseParts)
                    {
                        inputArray.Add(new JObject
                        {
                            ["type"] = "function_call",
                            ["name"] = part.ToolName,
                            ["arguments"] = (part.ToolArguments ?? new JObject()).ToString(Newtonsoft.Json.Formatting.None),
                            ["call_id"] = part.ToolCallId
                        });
                    }
                }
                else if (msg.HasToolResult)
                {
                    // Each tool result becomes a function_call_output item
                    foreach (var part in msg.ToolResultParts)
                    {
                        inputArray.Add(new JObject
                        {
                            ["type"] = "function_call_output",
                            ["call_id"] = part.ToolCallId,
                            ["output"] = part.Text ?? ""
                        });

                        // If tool result has an image, add a follow-up user message with the image
                        if (!string.IsNullOrEmpty(part.Base64Data))
                        {
                            inputArray.Add(new JObject
                            {
                                ["role"] = "user",
                                ["content"] = new JArray(
                                    new JObject
                                    {
                                        ["type"] = "input_text",
                                        ["text"] = "Image result from tool " + part.ToolName + ":"
                                    },
                                    new JObject
                                    {
                                        ["type"] = "input_image",
                                        ["image_url"] = "data:" + (part.MediaType ?? "image/png") + ";base64," + part.Base64Data
                                    }
                                )
                            });
                        }
                    }
                }
                else
                {
                    // Standard user/assistant message
                    var msgObj = new JObject { ["role"] = msg.Role };

                    if (msg.HasImages)
                    {
                        var contentArray = new JArray();
                        foreach (var part in msg.ContentParts)
                        {
                            if (part.Type == "text")
                            {
                                contentArray.Add(new JObject
                                {
                                    ["type"] = "input_text",
                                    ["text"] = part.Text
                                });
                            }
                            else if (part.Type == "image_url")
                            {
                                contentArray.Add(new JObject
                                {
                                    ["type"] = "input_image",
                                    ["image_url"] = "data:" + part.MediaType + ";base64," + part.Base64Data
                                });
                            }
                        }
                        msgObj["content"] = contentArray;
                    }
                    else if (msg.Role == "assistant")
                    {
                        msgObj["content"] = new JArray(new JObject
                        {
                            ["type"] = "output_text",
                            ["text"] = msg.Content ?? ""
                        });
                    }
                    else
                    {
                        msgObj["content"] = new JArray(new JObject
                        {
                            ["type"] = "input_text",
                            ["text"] = msg.Content ?? ""
                        });
                    }

                    inputArray.Add(msgObj);
                }
            }

            var requestBody = new JObject
            {
                ["model"] = model,
                ["max_output_tokens"] = maxTokens,
                ["input"] = inputArray
            };

            if (instructions != null)
                requestBody["instructions"] = instructions;

            // Add reasoning config
            var effort = MapThinkingLevelToEffort(thinkingLevel);
            if (effort != null)
            {
                requestBody["reasoning"] = new JObject
                {
                    ["effort"] = effort,
                    ["summary"] = "concise"
                };
            }

            // Add tools
            if (tools != null && tools.Count > 0)
            {
                var toolsArray = new JArray();
                foreach (var tool in tools)
                {
                    toolsArray.Add(new JObject
                    {
                        ["type"] = "function",
                        ["name"] = tool.Name,
                        ["description"] = tool.Description,
                        ["parameters"] = JObject.Parse(tool.ParametersJson)
                    });
                }
                requestBody["tools"] = toolsArray;
            }

            return requestBody;
        }

        /// <summary>
        /// Parses response from the Responses API (/v1/responses).
        /// </summary>
        private static ModelResponse ParseResponsesApiResponse(string responseJson)
        {
            var parsed = JObject.Parse(responseJson);
            var outputArray = parsed["output"] as JArray;
            var usage = parsed["usage"];
            int inputTokens = usage?["input_tokens"]?.Value<int>() ?? 0;
            int outputTokens = usage?["output_tokens"]?.Value<int>() ?? 0;
            int reasoningTokens = usage?["output_tokens_details"]?["reasoning_tokens"]?.Value<int>() ?? 0;

            var assistantParts = new List<ContentPart>();
            var toolCalls = new List<ToolCall>();
            string textContent = null;

            if (outputArray != null)
            {
                foreach (var item in outputArray)
                {
                    var itemType = item["type"]?.ToString();

                    if (itemType == "reasoning")
                    {
                        // Extract reasoning summary text
                        var summaryArray = item["summary"] as JArray;
                        if (summaryArray != null)
                        {
                            foreach (var summaryItem in summaryArray)
                            {
                                if (summaryItem["type"]?.ToString() == "summary_text")
                                {
                                    var summaryText = summaryItem["text"]?.ToString();
                                    if (!string.IsNullOrEmpty(summaryText))
                                        assistantParts.Add(ContentPart.FromThinking(null, summaryText));
                                }
                            }
                        }
                    }
                    else if (itemType == "message")
                    {
                        var contentArr = item["content"] as JArray;
                        if (contentArr != null)
                        {
                            foreach (var contentItem in contentArr)
                            {
                                if (contentItem["type"]?.ToString() == "output_text")
                                {
                                    var text = contentItem["text"]?.ToString();
                                    if (!string.IsNullOrEmpty(text))
                                    {
                                        assistantParts.Add(ContentPart.FromText(text));
                                        if (textContent == null)
                                            textContent = text;
                                    }
                                }
                            }
                        }
                    }
                    else if (itemType == "function_call")
                    {
                        var callId = item["call_id"]?.ToString();
                        var name = item["name"]?.ToString();
                        var argsStr = item["arguments"]?.ToString();
                        JObject args;
                        try { args = JObject.Parse(argsStr ?? "{}"); }
                        catch { args = new JObject(); }

                        assistantParts.Add(ContentPart.FromToolUse(callId, name, args));
                        toolCalls.Add(new ToolCall { Id = callId, Name = name, Arguments = args });
                    }
                }
            }

            var stopReason = parsed["status"]?.ToString();

            return new ModelResponse
            {
                Success = true,
                Content = textContent,
                RawJson = responseJson,
                TokensUsed = inputTokens + outputTokens,
                InputTokens = inputTokens,
                OutputTokens = outputTokens,
                ReasoningTokens = reasoningTokens,
                StopReason = toolCalls.Count > 0 ? StopReason.ToolUse
                    : stopReason == "incomplete" ? StopReason.MaxTokens : StopReason.EndTurn,
                ToolCalls = toolCalls,
                AssistantParts = assistantParts
            };
        }

        /// <summary>
        /// Builds request body for the Chat Completions API (used by SendChatRequest only).
        /// </summary>
        private static JObject BuildChatRequestBody(List<ChatMessage> messages, string model, int maxTokens)
        {
            var messagesArray = new JArray();
            foreach (var msg in messages)
            {
                var msgObj = new JObject { ["role"] = msg.Role };

                if (msg.HasImages)
                {
                    var contentArray = new JArray();
                    foreach (var part in msg.ContentParts)
                    {
                        if (part.Type == "text")
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
                                ["type"] = "image_url",
                                ["image_url"] = new JObject
                                {
                                    ["url"] = "data:" + part.MediaType + ";base64," + part.Base64Data
                                }
                            });
                        }
                    }
                    msgObj["content"] = contentArray;
                }
                else
                {
                    msgObj["content"] = msg.Content;
                }

                messagesArray.Add(msgObj);
            }

            return new JObject
            {
                ["model"] = model,
                ["max_completion_tokens"] = maxTokens,
                ["messages"] = messagesArray
            };
        }

        public async Task<ModelResponse> SendImageRequest(List<ChatMessage> messages, string model, string apiKey, int maxTokens)
        {
            try
            {
                // Collect prompt text from all messages and find the input image
                var promptParts = new List<string>();
                byte[] imageBytes = null;

                foreach (var msg in messages)
                {
                    foreach (var part in msg.ContentParts)
                    {
                        if (part.Type == "text" && !string.IsNullOrEmpty(part.Text))
                            promptParts.Add(part.Text);
                        else if (part.Type == "image_url" && imageBytes == null)
                            imageBytes = Convert.FromBase64String(part.Base64Data);
                    }
                }

                if (imageBytes == null)
                    return ModelResponse.FromError("OpenAI image edit request requires an input image.");

                var prompt = string.Join("\n", promptParts);

                // Build multipart form data for /v1/images/edits
                var form = new MultipartFormDataContent();

                var imageContent = new ByteArrayContent(imageBytes);
                imageContent.Headers.ContentType = new MediaTypeHeaderValue("image/png");
                form.Add(imageContent, "image[]", "image.png");

                form.Add(new StringContent(prompt), "prompt");
                form.Add(new StringContent("gpt-image-1.5"), "model");
                form.Add(new StringContent("1024x1024"), "size");
                form.Add(new StringContent("low"), "quality");

                var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/images/edits");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                request.Content = form;

                var response = await Client.SendAsync(request);
                var responseJson = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    var flat = responseJson.Replace("\n", " ").Replace("\r", "");
                    return ModelResponse.FromError("OpenAI API error (" + response.StatusCode + "): " + flat);
                }

                var parsedResp = JObject.Parse(responseJson);
                var imageBase64 = parsedResp["data"]?[0]?["b64_json"]?.ToString();
                var imgUsage = parsedResp["usage"];
                int imgTokensUsed = imgUsage?["total_tokens"]?.Value<int>() ?? 0;
                int imgInputTokens = imgUsage?["prompt_tokens"]?.Value<int>() ?? 0;
                int imgOutputTokens = imgUsage?["completion_tokens"]?.Value<int>() ?? 0;

                return new ModelResponse
                {
                    Success = true,
                    Content = null,
                    ImageBase64 = imageBase64,
                    ImageMediaType = imageBase64 != null ? "image/png" : null,
                    RawJson = responseJson,
                    TokensUsed = imgTokensUsed,
                    InputTokens = imgInputTokens,
                    OutputTokens = imgOutputTokens
                };
            }
            catch (Exception ex)
            {
                return ModelResponse.FromError("OpenAI image request failed: " + ex.Message);
            }
        }
    }
}
