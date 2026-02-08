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
                    ["max_completion_tokens"] = maxTokens,
                    ["messages"] = messagesArray
                };

                var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                request.Content = new StringContent(requestBody.ToString(), Encoding.UTF8, "application/json");

                var response = await Client.SendAsync(request);
                var responseJson = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    var flat = responseJson.Replace("\n", " ").Replace("\r", "");
                    return ModelResponse.FromError($"OpenAI API error ({response.StatusCode}): {flat}");
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
                    return ModelResponse.FromError($"OpenAI API error ({response.StatusCode}): {flat}");
                }

                var parsed = JObject.Parse(responseJson);
                var imageBase64 = parsed["data"]?[0]?["b64_json"]?.ToString();
                var tokensUsed = parsed["usage"]?["total_tokens"]?.Value<int>() ?? 0;

                return new ModelResponse
                {
                    Success = true,
                    Content = null,
                    ImageBase64 = imageBase64,
                    ImageMediaType = imageBase64 != null ? "image/png" : null,
                    RawJson = responseJson,
                    TokensUsed = tokensUsed
                };
            }
            catch (Exception ex)
            {
                return ModelResponse.FromError($"OpenAI image request failed: {ex.Message}");
            }
        }
    }
}
