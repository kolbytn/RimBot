using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace RimBot.Models
{
    public class OpenAIModel : ILanguageModel
    {
        private static readonly HttpClient Client = new HttpClient();

        public LLMProviderType ProviderType => LLMProviderType.OpenAI;

        public string[] GetAvailableModels()
        {
            return new[]
            {
                "gpt-5.2",
                "gpt-4o",
                "o3-mini"
            };
        }

        public async Task<ModelResponse> SendChatRequest(List<ChatMessage> messages, string model, string apiKey, int maxTokens)
        {
            try
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
                                        ["url"] = $"data:{part.MediaType};base64,{part.Base64Data}"
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

                var requestBody = new JObject
                {
                    ["model"] = model,
                    ["max_tokens"] = maxTokens,
                    ["messages"] = messagesArray
                };

                var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                request.Content = new StringContent(requestBody.ToString(), Encoding.UTF8, "application/json");

                var response = await Client.SendAsync(request);
                var responseJson = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    return ModelResponse.FromError($"OpenAI API error ({response.StatusCode}): {responseJson}");
                }

                var parsed = JObject.Parse(responseJson);
                var content = parsed["choices"]?[0]?["message"]?["content"]?.ToString();
                var tokensUsed = parsed["usage"]?["total_tokens"]?.Value<int>() ?? 0;

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
                return ModelResponse.FromError($"OpenAI request failed: {ex.Message}");
            }
        }
    }
}
