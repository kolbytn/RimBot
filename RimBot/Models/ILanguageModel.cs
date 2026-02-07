using System.Collections.Generic;
using System.Threading.Tasks;

namespace RimBot.Models
{
    public interface ILanguageModel
    {
        LLMProviderType ProviderType { get; }
        Task<ModelResponse> SendChatRequest(List<ChatMessage> messages, string model, string apiKey, int maxTokens);
        string[] GetAvailableModels();
    }
}
