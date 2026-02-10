using System.Collections.Generic;
using System.Threading.Tasks;
using RimBot.Tools;

namespace RimBot.Models
{
    public interface ILanguageModel
    {
        LLMProviderType ProviderType { get; }
        bool SupportsImageOutput { get; }
        Task<ModelResponse> SendChatRequest(List<ChatMessage> messages, string model, string apiKey, int maxTokens);
        Task<ModelResponse> SendImageRequest(List<ChatMessage> messages, string model, string apiKey, int maxTokens);
        Task<ModelResponse> SendToolRequest(List<ChatMessage> messages, List<ToolDefinition> tools,
            string model, string apiKey, int maxTokens, ThinkingLevel thinkingLevel);
        string[] GetAvailableModels();
    }
}
