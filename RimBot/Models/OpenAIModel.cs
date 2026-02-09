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
                var requestBody = BuildRequestBody(messages, model, maxTokens, null);

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
            string model, string apiKey, int maxTokens)
        {
            try
            {
                var requestBody = BuildRequestBody(messages, model, maxTokens, tools);

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

                return ParseToolResponse(responseJson);
            }
            catch (Exception ex)
            {
                return ModelResponse.FromError("OpenAI tool request failed: " + ex.Message);
            }
        }

        private static JObject BuildRequestBody(List<ChatMessage> messages, string model, int maxTokens,
            List<ToolDefinition> tools)
        {
            var messagesArray = new JArray();
            foreach (var msg in messages)
            {
                if (msg.HasToolUse && msg.Role == "assistant")
                {
                    // Assistant message with tool calls
                    var msgObj = new JObject { ["role"] = "assistant" };

                    // Extract text content if any
                    string textContent = null;
                    foreach (var part in msg.ContentParts)
                    {
                        if (part.Type == "text" && !string.IsNullOrEmpty(part.Text))
                            textContent = part.Text;
                    }
                    if (textContent != null)
                        msgObj["content"] = textContent;

                    // Build tool_calls array
                    var toolCallsArr = new JArray();
                    foreach (var part in msg.ToolUseParts)
                    {
                        toolCallsArr.Add(new JObject
                        {
                            ["id"] = part.ToolCallId,
                            ["type"] = "function",
                            ["function"] = new JObject
                            {
                                ["name"] = part.ToolName,
                                ["arguments"] = (part.ToolArguments ?? new JObject()).ToString(Newtonsoft.Json.Formatting.None)
                            }
                        });
                    }
                    msgObj["tool_calls"] = toolCallsArr;
                    messagesArray.Add(msgObj);
                }
                else if (msg.HasToolResult)
                {
                    // Each tool result becomes a separate message with role "tool"
                    foreach (var part in msg.ToolResultParts)
                    {
                        messagesArray.Add(new JObject
                        {
                            ["role"] = "tool",
                            ["tool_call_id"] = part.ToolCallId,
                            ["content"] = part.Text ?? ""
                        });

                        // If tool result has an image, add a follow-up user message with the image
                        if (!string.IsNullOrEmpty(part.Base64Data))
                        {
                            var imgContent = new JArray
                            {
                                new JObject
                                {
                                    ["type"] = "text",
                                    ["text"] = "Image result from tool " + part.ToolName + ":"
                                },
                                new JObject
                                {
                                    ["type"] = "image_url",
                                    ["image_url"] = new JObject
                                    {
                                        ["url"] = "data:" + (part.MediaType ?? "image/png") + ";base64," + part.Base64Data
                                    }
                                }
                            };
                            messagesArray.Add(new JObject
                            {
                                ["role"] = "user",
                                ["content"] = imgContent
                            });
                        }
                    }
                }
                else
                {
                    // Standard message
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
            }

            var requestBody = new JObject
            {
                ["model"] = model,
                ["max_completion_tokens"] = maxTokens,
                ["messages"] = messagesArray
            };

            // Add tools if provided
            if (tools != null && tools.Count > 0)
            {
                var toolsArray = new JArray();
                foreach (var tool in tools)
                {
                    toolsArray.Add(new JObject
                    {
                        ["type"] = "function",
                        ["function"] = new JObject
                        {
                            ["name"] = tool.Name,
                            ["description"] = tool.Description,
                            ["parameters"] = JObject.Parse(tool.ParametersJson)
                        }
                    });
                }
                requestBody["tools"] = toolsArray;
            }

            return requestBody;
        }

        private static ModelResponse ParseToolResponse(string responseJson)
        {
            var parsed = JObject.Parse(responseJson);
            var choice = parsed["choices"]?[0];
            var message = choice?["message"];
            var finishReason = choice?["finish_reason"]?.ToString();
            var usage = parsed["usage"];
            int inputTokens = usage?["prompt_tokens"]?.Value<int>() ?? 0;
            int outputTokens = usage?["completion_tokens"]?.Value<int>() ?? 0;
            int reasoningTokens = usage?["completion_tokens_details"]?["reasoning_tokens"]?.Value<int>() ?? 0;
            int tokensUsed = usage?["total_tokens"]?.Value<int>() ?? 0;

            var assistantParts = new List<ContentPart>();
            var toolCalls = new List<ToolCall>();

            var content = message?["content"]?.ToString();
            if (!string.IsNullOrEmpty(content))
                assistantParts.Add(ContentPart.FromText(content));

            var toolCallsArr = message?["tool_calls"] as JArray;
            if (toolCallsArr != null)
            {
                foreach (var tc in toolCallsArr)
                {
                    var id = tc["id"]?.ToString();
                    var fn = tc["function"];
                    var name = fn?["name"]?.ToString();
                    var argsStr = fn?["arguments"]?.ToString();
                    JObject args;
                    try { args = JObject.Parse(argsStr ?? "{}"); }
                    catch { args = new JObject(); }

                    assistantParts.Add(ContentPart.FromToolUse(id, name, args));
                    toolCalls.Add(new ToolCall { Id = id, Name = name, Arguments = args });
                }
            }

            return new ModelResponse
            {
                Success = true,
                Content = content,
                RawJson = responseJson,
                TokensUsed = tokensUsed,
                InputTokens = inputTokens,
                OutputTokens = outputTokens,
                ReasoningTokens = reasoningTokens,
                StopReason = finishReason == "tool_calls" ? StopReason.ToolUse : StopReason.EndTurn,
                ToolCalls = toolCalls,
                AssistantParts = assistantParts
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
