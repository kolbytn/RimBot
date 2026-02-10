using System.Collections.Generic;
using RimBot.Tools;

namespace RimBot.Models
{
    public enum StopReason
    {
        EndTurn,
        ToolUse,
        MaxTokens,
        Error
    }

    public class ModelResponse
    {
        public bool Success { get; set; }
        public string Content { get; set; }
        public string ErrorMessage { get; set; }
        public string RawJson { get; set; }
        public int TokensUsed { get; set; }
        public int InputTokens { get; set; }
        public int OutputTokens { get; set; }
        public int CacheReadTokens { get; set; }
        public int ReasoningTokens { get; set; }
        public string ImageBase64 { get; set; }
        public string ImageMediaType { get; set; }

        // Tool calling fields
        public StopReason StopReason { get; set; }
        public List<ToolCall> ToolCalls { get; set; }
        public List<ContentPart> AssistantParts { get; set; }

        public static ModelResponse FromError(string errorMessage)
        {
            return new ModelResponse
            {
                Success = false,
                Content = null,
                ErrorMessage = errorMessage,
                RawJson = null,
                TokensUsed = 0,
                StopReason = StopReason.Error
            };
        }
    }
}
