using RimBot.Models;
using Verse;

namespace RimBot
{
    public class RimBotSettings : ModSettings
    {
        public LLMProviderType activeProvider = LLMProviderType.Anthropic;
        public string anthropicApiKey = "";
        public string openAIApiKey = "";
        public string googleApiKey = "";
        public string anthropicModel = "claude-opus-4-6";
        public string openAIModel = "gpt-5.2";
        public string googleModel = "gemini-3-flash-preview";
        public int maxTokens = 1024;

        public override void ExposeData()
        {
            Scribe_Values.Look(ref activeProvider, "activeProvider", LLMProviderType.Anthropic);
            Scribe_Values.Look(ref anthropicApiKey, "anthropicApiKey", "");
            Scribe_Values.Look(ref openAIApiKey, "openAIApiKey", "");
            Scribe_Values.Look(ref googleApiKey, "googleApiKey", "");
            Scribe_Values.Look(ref anthropicModel, "anthropicModel", "claude-opus-4-6");
            Scribe_Values.Look(ref openAIModel, "openAIModel", "gpt-5.2");
            Scribe_Values.Look(ref googleModel, "googleModel", "gemini-3-flash-preview");
            Scribe_Values.Look(ref maxTokens, "maxTokens", 1024);
            base.ExposeData();
        }

        public string GetActiveApiKey()
        {
            switch (activeProvider)
            {
                case LLMProviderType.Anthropic: return anthropicApiKey;
                case LLMProviderType.OpenAI: return openAIApiKey;
                case LLMProviderType.Google: return googleApiKey;
                default: return "";
            }
        }

        public string GetActiveModel()
        {
            switch (activeProvider)
            {
                case LLMProviderType.Anthropic: return anthropicModel;
                case LLMProviderType.OpenAI: return openAIModel;
                case LLMProviderType.Google: return googleModel;
                default: return "";
            }
        }

        public void SetActiveModel(string model)
        {
            switch (activeProvider)
            {
                case LLMProviderType.Anthropic:
                    anthropicModel = model;
                    break;
                case LLMProviderType.OpenAI:
                    openAIModel = model;
                    break;
                case LLMProviderType.Google:
                    googleModel = model;
                    break;
            }
        }

        public void SetActiveApiKey(string key)
        {
            switch (activeProvider)
            {
                case LLMProviderType.Anthropic:
                    anthropicApiKey = key;
                    break;
                case LLMProviderType.OpenAI:
                    openAIApiKey = key;
                    break;
                case LLMProviderType.Google:
                    googleApiKey = key;
                    break;
            }
        }
    }
}
