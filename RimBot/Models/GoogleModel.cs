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
    public class GoogleModel : ILanguageModel
    {
        private static readonly HttpClient Client = new HttpClient();

        public LLMProviderType ProviderType => LLMProviderType.Google;
        public bool SupportsImageOutput => true;

        public string[] GetAvailableModels()
        {
            return new[]
            {
                "gemini-3-flash-preview",
                "gemini-3-pro-preview"
            };
        }

        public async Task<ModelResponse> SendChatRequest(List<ChatMessage> messages, string model, string apiKey, int maxTokens)
        {
            try
            {
                var requestBody = BuildRequestBody(messages, maxTokens, null);

                var url = "https://generativelanguage.googleapis.com/v1beta/models/" + model + ":generateContent?key=" + apiKey;
                var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Content = new StringContent(requestBody.ToString(), Encoding.UTF8, "application/json");

                var response = await Client.SendAsync(request);
                var responseJson = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    var flat = responseJson.Replace("\n", " ").Replace("\r", "");
                    return ModelResponse.FromError("Google API error (" + response.StatusCode + "): " + flat);
                }

                return ParseResponse(responseJson);
            }
            catch (Exception ex)
            {
                return ModelResponse.FromError("Google request failed: " + ex.Message);
            }
        }

        public async Task<ModelResponse> SendToolRequest(List<ChatMessage> messages, List<ToolDefinition> tools,
            string model, string apiKey, int maxTokens)
        {
            try
            {
                var requestBody = BuildRequestBody(messages, maxTokens, tools);

                var url = "https://generativelanguage.googleapis.com/v1beta/models/" + model + ":generateContent?key=" + apiKey;
                var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Content = new StringContent(requestBody.ToString(), Encoding.UTF8, "application/json");

                var response = await Client.SendAsync(request);
                var responseJson = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    var flat = responseJson.Replace("\n", " ").Replace("\r", "");
                    return ModelResponse.FromError("Google API error (" + response.StatusCode + "): " + flat);
                }

                return ParseToolResponse(responseJson);
            }
            catch (Exception ex)
            {
                return ModelResponse.FromError("Google tool request failed: " + ex.Message);
            }
        }

        private static string GetImageModel(string model)
        {
            return "gemini-2.5-flash-image";
        }

        public async Task<ModelResponse> SendImageRequest(List<ChatMessage> messages, string model, string apiKey, int maxTokens)
        {
            try
            {
                var imageModel = GetImageModel(model);
                // Image generation needs far more tokens than text (thinking + image encoding)
                var imageMaxTokens = Math.Max(maxTokens, 8192);

                // Image model may not support systemInstruction — fold system text into user content
                var mergedMessages = FoldSystemIntoUser(messages);
                var requestBody = BuildRequestBody(mergedMessages, imageMaxTokens, null);

                var genConfig = (JObject)requestBody["generationConfig"];
                genConfig["responseModalities"] = new JArray("TEXT", "IMAGE");

                var url = "https://generativelanguage.googleapis.com/v1beta/models/" + imageModel + ":generateContent?key=" + apiKey;
                var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Content = new StringContent(requestBody.ToString(), Encoding.UTF8, "application/json");

                var response = await Client.SendAsync(request);
                var responseJson = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    var flat = responseJson.Replace("\n", " ").Replace("\r", "");
                    return ModelResponse.FromError("Google API error (" + response.StatusCode + "): " + flat);
                }

                return ParseResponse(responseJson);
            }
            catch (Exception ex)
            {
                return ModelResponse.FromError("Google image request failed: " + ex.Message);
            }
        }

        private static List<ChatMessage> FoldSystemIntoUser(List<ChatMessage> messages)
        {
            var systemText = new List<string>();
            var result = new List<ChatMessage>();

            foreach (var msg in messages)
            {
                if (msg.Role == "system")
                    systemText.Add(msg.Content);
                else
                    result.Add(msg);
            }

            if (systemText.Count > 0 && result.Count > 0)
            {
                var first = result[0];
                var prefix = string.Join("\n\n", systemText);
                if (first.HasImages)
                {
                    var newParts = new List<ContentPart>();
                    newParts.Add(ContentPart.FromText(prefix));
                    newParts.AddRange(first.ContentParts);
                    result[0] = new ChatMessage(first.Role, newParts);
                }
                else
                {
                    result[0] = new ChatMessage(first.Role, prefix + "\n\n" + first.Content);
                }
            }

            return result;
        }

        private static JObject BuildRequestBody(List<ChatMessage> messages, int maxTokens, List<ToolDefinition> tools)
        {
            var systemMessages = messages.Where(m => m.Role == "system").ToList();
            var nonSystemMessages = messages.Where(m => m.Role != "system").ToList();

            var requestBody = new JObject();

            if (systemMessages.Any())
            {
                var systemParts = new JArray();
                foreach (var sysMsg in systemMessages)
                {
                    systemParts.Add(new JObject { ["text"] = sysMsg.Content });
                }
                requestBody["systemInstruction"] = new JObject
                {
                    ["parts"] = systemParts
                };
            }

            // Add tools if provided
            if (tools != null && tools.Count > 0)
            {
                var funcDecls = new JArray();
                foreach (var tool in tools)
                {
                    funcDecls.Add(new JObject
                    {
                        ["name"] = tool.Name,
                        ["description"] = tool.Description,
                        ["parameters"] = JObject.Parse(tool.ParametersJson)
                    });
                }
                requestBody["tools"] = new JArray(new JObject
                {
                    ["functionDeclarations"] = funcDecls
                });
            }

            var contentsArray = new JArray();
            foreach (var msg in nonSystemMessages)
            {
                var role = msg.Role == "assistant" ? "model" : msg.Role;
                var partsArray = new JArray();

                foreach (var part in msg.ContentParts)
                {
                    // Skip Google thought parts — they're informational only, don't echo back
                    if (part.Type == "thinking" && part.IsThought)
                        continue;

                    if (part.Type == "text" && !string.IsNullOrEmpty(part.Text))
                    {
                        partsArray.Add(new JObject { ["text"] = part.Text });
                    }
                    else if (part.Type == "image_url")
                    {
                        partsArray.Add(new JObject
                        {
                            ["inline_data"] = new JObject
                            {
                                ["mime_type"] = part.MediaType,
                                ["data"] = part.Base64Data
                            }
                        });
                    }
                    else if (part.Type == "tool_use")
                    {
                        var fcPart = new JObject
                        {
                            ["functionCall"] = new JObject
                            {
                                ["name"] = part.ToolName,
                                ["args"] = part.ToolArguments ?? new JObject()
                            }
                        };
                        // Gemini 3 requires thoughtSignature echoed back
                        if (!string.IsNullOrEmpty(part.ThoughtSignature))
                            fcPart["thoughtSignature"] = part.ThoughtSignature;
                        partsArray.Add(fcPart);
                    }
                    else if (part.Type == "tool_result")
                    {
                        var responseObj = new JObject
                        {
                            ["content"] = part.Text ?? ""
                        };
                        // Add image data inline if present
                        if (!string.IsNullOrEmpty(part.Base64Data))
                        {
                            responseObj["image_data"] = part.Base64Data;
                        }
                        partsArray.Add(new JObject
                        {
                            ["functionResponse"] = new JObject
                            {
                                ["name"] = part.ToolName,
                                ["response"] = responseObj
                            }
                        });
                    }
                }

                if (partsArray.Count > 0)
                {
                    contentsArray.Add(new JObject
                    {
                        ["role"] = role,
                        ["parts"] = partsArray
                    });
                }
            }
            requestBody["contents"] = contentsArray;

            requestBody["generationConfig"] = new JObject
            {
                ["maxOutputTokens"] = maxTokens
            };

            return requestBody;
        }

        private static ModelResponse ParseResponse(string responseJson)
        {
            var parsed = JObject.Parse(responseJson);
            var parts = parsed["candidates"]?[0]?["content"]?["parts"] as JArray;
            var usageMeta = parsed["usageMetadata"];
            int inputTokens = usageMeta?["promptTokenCount"]?.Value<int>() ?? 0;
            int outputTokens = usageMeta?["candidatesTokenCount"]?.Value<int>() ?? 0;
            int cacheRead = usageMeta?["cachedContentTokenCount"]?.Value<int>() ?? 0;
            int tokensUsed = usageMeta?["totalTokenCount"]?.Value<int>() ?? 0;

            string textContent = null;
            string imageBase64 = null;
            string imageMimeType = null;

            if (parts != null)
            {
                foreach (var part in parts)
                {
                    if (part["text"] != null && textContent == null)
                    {
                        textContent = part["text"].ToString();
                    }
                    else if (part["inlineData"] != null && imageBase64 == null)
                    {
                        imageMimeType = part["inlineData"]["mimeType"]?.ToString();
                        imageBase64 = part["inlineData"]["data"]?.ToString();
                    }
                }
            }

            return new ModelResponse
            {
                Success = true,
                Content = textContent,
                ImageBase64 = imageBase64,
                ImageMediaType = imageMimeType,
                RawJson = responseJson,
                TokensUsed = tokensUsed,
                InputTokens = inputTokens,
                OutputTokens = outputTokens,
                CacheReadTokens = cacheRead
            };
        }

        private static ModelResponse ParseToolResponse(string responseJson)
        {
            var parsed = JObject.Parse(responseJson);
            var parts = parsed["candidates"]?[0]?["content"]?["parts"] as JArray;
            var usageMeta = parsed["usageMetadata"];
            int inputTokens = usageMeta?["promptTokenCount"]?.Value<int>() ?? 0;
            int outputTokens = usageMeta?["candidatesTokenCount"]?.Value<int>() ?? 0;
            int cacheRead = usageMeta?["cachedContentTokenCount"]?.Value<int>() ?? 0;
            int tokensUsed = usageMeta?["totalTokenCount"]?.Value<int>() ?? 0;

            var assistantParts = new List<ContentPart>();
            var toolCalls = new List<ToolCall>();
            string textContent = null;

            if (parts != null)
            {
                foreach (var part in parts)
                {
                    // Check for thought parts (Gemini 3 thinking)
                    if (part["thought"]?.Value<bool>() == true && part["text"] != null)
                    {
                        var thoughtPart = ContentPart.FromThinking(null, part["text"].ToString());
                        thoughtPart.IsThought = true;
                        assistantParts.Add(thoughtPart);
                    }
                    else if (part["text"] != null)
                    {
                        var text = part["text"].ToString();
                        assistantParts.Add(ContentPart.FromText(text));
                        if (textContent == null)
                            textContent = text;
                    }
                    else if (part["functionCall"] != null)
                    {
                        var fc = part["functionCall"];
                        var name = fc["name"]?.ToString();
                        var args = fc["args"] as JObject ?? new JObject();
                        // Google doesn't use IDs, generate synthetic ones
                        var id = "google_" + name + "_" + Guid.NewGuid().ToString("N").Substring(0, 8);

                        var contentPart = ContentPart.FromToolUse(id, name, args);
                        // Capture thought signature for Gemini 3
                        var thoughtSig = part["thoughtSignature"]?.ToString();
                        if (!string.IsNullOrEmpty(thoughtSig))
                            contentPart.ThoughtSignature = thoughtSig;
                        assistantParts.Add(contentPart);
                        toolCalls.Add(new ToolCall { Id = id, Name = name, Arguments = args });
                    }
                }
            }

            return new ModelResponse
            {
                Success = true,
                Content = textContent,
                RawJson = responseJson,
                TokensUsed = tokensUsed,
                InputTokens = inputTokens,
                OutputTokens = outputTokens,
                CacheReadTokens = cacheRead,
                StopReason = toolCalls.Count > 0 ? StopReason.ToolUse : StopReason.EndTurn,
                ToolCalls = toolCalls,
                AssistantParts = assistantParts
            };
        }
    }
}
