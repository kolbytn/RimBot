using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace RimBot.Models
{
    public class AnthropicModel : ILanguageModel
    {
        private static readonly HttpClient Client = new HttpClient();

        public LLMProviderType ProviderType => LLMProviderType.Anthropic;

        public string[] GetAvailableModels()
        {
            return new[]
            {
                "claude-opus-4-6",
                "claude-sonnet-4-5-20250929",
                "claude-haiku-4-5-20251001"
            };
        }

        public async Task<ModelResponse> SendChatRequest(List<ChatMessage> messages, string model, string apiKey, int maxTokens)
        {
            try
            {
                var systemMessages = messages.Where(m => m.Role == "system").ToList();
                var nonSystemMessages = messages.Where(m => m.Role != "system").ToList();

                var requestBody = new JObject
                {
                    ["model"] = model,
                    ["max_tokens"] = maxTokens
                };

                if (systemMessages.Any())
                {
                    requestBody["system"] = string.Join("\n\n", systemMessages.Select(m => m.Content));
                }

                var messagesArray = new JArray();
                foreach (var msg in nonSystemMessages)
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
                                    ["type"] = "image",
                                    ["source"] = new JObject
                                    {
                                        ["type"] = "base64",
                                        ["media_type"] = part.MediaType,
                                        ["data"] = part.Base64Data
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
                requestBody["messages"] = messagesArray;

                var request = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages");
                request.Headers.Add("x-api-key", apiKey);
                request.Headers.Add("anthropic-version", "2023-06-01");
                request.Content = new StringContent(requestBody.ToString(), Encoding.UTF8, "application/json");

                var response = await Client.SendAsync(request);
                var responseJson = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    return ModelResponse.FromError($"Anthropic API error ({response.StatusCode}): {responseJson}");
                }

                var parsed = JObject.Parse(responseJson);
                var content = parsed["content"]?[0]?["text"]?.ToString();
                var tokensUsed = parsed["usage"]?["output_tokens"]?.Value<int>() ?? 0;

                return new ModelResponse
                {
                    Success = true,
                    Content = content,
                    RawJson = responseJson,
                    TokensUsed = tokensUsed
                };
            }
            catch (Exception ex)
            {
                return ModelResponse.FromError($"Anthropic request failed: {ex.Message}");
            }
        }
    }
}
