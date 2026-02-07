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

        public string[] GetAvailableModels()
        {
            return new[]
            {
                "gemini-3-pro-preview",
                "gemini-3-flash-preview",
                "gemini-2.5-flash",
                "gemini-2.5-pro"
            };
        }

        public async Task<ModelResponse> SendChatRequest(List<ChatMessage> messages, string model, string apiKey, int maxTokens)
        {
            try
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
                    contentsArray.Add(new JObject
                    {
                        ["role"] = role,
                        ["parts"] = new JArray
                        {
                            new JObject { ["text"] = msg.Content }
                        }
                    });
                }
                requestBody["contents"] = contentsArray;

                requestBody["generationConfig"] = new JObject
                {
                    ["maxOutputTokens"] = maxTokens
                };

                var url = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent?key={apiKey}";
                var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Content = new StringContent(requestBody.ToString(), Encoding.UTF8, "application/json");

                var response = await Client.SendAsync(request);
                var responseJson = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    return ModelResponse.FromError($"Google API error ({response.StatusCode}): {responseJson}");
                }

                var parsed = JObject.Parse(responseJson);
                var content = parsed["candidates"]?[0]?["content"]?["parts"]?[0]?["text"]?.ToString();
                var tokensUsed = parsed["usageMetadata"]?["totalTokenCount"]?.Value<int>() ?? 0;

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
                return ModelResponse.FromError($"Google request failed: {ex.Message}");
            }
        }
    }
}
