using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

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
                var requestBody = BuildRequestBody(messages, maxTokens);

                var url = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent?key={apiKey}";
                var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Content = new StringContent(requestBody.ToString(), Encoding.UTF8, "application/json");

                var response = await Client.SendAsync(request);
                var responseJson = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    var flat = responseJson.Replace("\n", " ").Replace("\r", "");
                    return ModelResponse.FromError($"Google API error ({response.StatusCode}): {flat}");
                }

                return ParseResponse(responseJson);
            }
            catch (Exception ex)
            {
                return ModelResponse.FromError($"Google request failed: {ex.Message}");
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
                var requestBody = BuildRequestBody(mergedMessages, imageMaxTokens);

                var genConfig = (JObject)requestBody["generationConfig"];
                genConfig["responseModalities"] = new JArray("TEXT", "IMAGE");

                var url = $"https://generativelanguage.googleapis.com/v1beta/models/{imageModel}:generateContent?key={apiKey}";
                var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Content = new StringContent(requestBody.ToString(), Encoding.UTF8, "application/json");

                var response = await Client.SendAsync(request);
                var responseJson = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    var flat = responseJson.Replace("\n", " ").Replace("\r", "");
                    return ModelResponse.FromError($"Google API error ({response.StatusCode}): {flat}");
                }

                return ParseResponse(responseJson);
            }
            catch (Exception ex)
            {
                return ModelResponse.FromError($"Google image request failed: {ex.Message}");
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

        private static JObject BuildRequestBody(List<ChatMessage> messages, int maxTokens)
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

            var contentsArray = new JArray();
            foreach (var msg in nonSystemMessages)
            {
                var role = msg.Role == "assistant" ? "model" : msg.Role;
                var partsArray = new JArray();

                if (msg.HasImages)
                {
                    foreach (var part in msg.ContentParts)
                    {
                        if (part.Type == "text")
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
                    }
                }
                else
                {
                    partsArray.Add(new JObject { ["text"] = msg.Content });
                }

                contentsArray.Add(new JObject
                {
                    ["role"] = role,
                    ["parts"] = partsArray
                });
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
            var tokensUsed = parsed["usageMetadata"]?["totalTokenCount"]?.Value<int>() ?? 0;

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
                TokensUsed = tokensUsed
            };
        }
    }
}
