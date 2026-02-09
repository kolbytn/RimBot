using System;
using System.Collections.Generic;
using RimBot.Models;
using UnityEngine;

namespace RimBot
{
    public class ToolCallRecord
    {
        public string Id;
        public string Name;
        public string ArgumentsJson;
    }

    public class ToolResultRecord
    {
        public string ToolCallId;
        public bool Success;
        public string Content;
        public bool HasImage;
    }

    public class HistoryEntry
    {
        public int GameTick;
        public string Mode;
        public string SystemPrompt;
        public string UserQuery;
        public string ResponseText;
        public bool Success;
        public LLMProviderType Provider;
        public string ModelName;
        public string ScreenshotBase64;
        public List<ToolCallRecord> ToolCalls;
        public List<ToolResultRecord> ToolResults;
        public int? AgentIteration;
        public string ThinkingText;
        public int InputTokens;
        public int OutputTokens;
        public int CacheReadTokens;
        public int ReasoningTokens;

        private Texture2D cachedTexture;

        public Texture2D GetTexture()
        {
            if (cachedTexture != null)
                return cachedTexture;
            if (string.IsNullOrEmpty(ScreenshotBase64))
                return null;

            try
            {
                byte[] bytes = Convert.FromBase64String(ScreenshotBase64);
                cachedTexture = new Texture2D(2, 2);
                cachedTexture.LoadImage(bytes);
            }
            catch
            {
                cachedTexture = null;
            }

            return cachedTexture;
        }

        public void DisposeTexture()
        {
            if (cachedTexture != null)
            {
                UnityEngine.Object.Destroy(cachedTexture);
                cachedTexture = null;
            }
        }
    }
}
