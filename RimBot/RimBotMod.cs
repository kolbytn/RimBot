using RimBot.Models;
using UnityEngine;
using Verse;

namespace RimBot
{
    public class RimBotMod : Mod
    {
        public static RimBotSettings Settings { get; private set; }

        public RimBotMod(ModContentPack content) : base(content)
        {
            Settings = GetSettings<RimBotSettings>();
        }

        public override string SettingsCategory()
        {
            return "RimBot";
        }

        public override void DoSettingsWindowContents(Rect inRect)
        {
            var listing = new Listing_Standard();
            listing.Begin(inRect);

            listing.Label("LLM Provider:");
            if (listing.RadioButton("Anthropic", Settings.activeProvider == LLMProviderType.Anthropic))
            {
                Settings.activeProvider = LLMProviderType.Anthropic;
            }
            if (listing.RadioButton("OpenAI", Settings.activeProvider == LLMProviderType.OpenAI))
            {
                Settings.activeProvider = LLMProviderType.OpenAI;
            }
            if (listing.RadioButton("Google Gemini", Settings.activeProvider == LLMProviderType.Google))
            {
                Settings.activeProvider = LLMProviderType.Google;
            }

            listing.GapLine();

            listing.Label("API Key (" + Settings.activeProvider + "):");
            var currentKey = Settings.GetActiveApiKey();
            var newKey = listing.TextEntry(currentKey);
            if (newKey != currentKey)
            {
                Settings.SetActiveApiKey(newKey);
            }

            listing.GapLine();

            listing.Label("Model:");
            var llmModel = LLMModelFactory.GetModel(Settings.activeProvider);
            var availableModels = llmModel.GetAvailableModels();
            var activeModel = Settings.GetActiveModel();
            foreach (var model in availableModels)
            {
                if (listing.RadioButton(model, activeModel == model))
                {
                    Settings.SetActiveModel(model);
                }
            }

            listing.GapLine();

            Settings.maxTokens = (int)listing.Slider(Settings.maxTokens, 64, 4096);
            listing.Label("Max Tokens: " + Settings.maxTokens);

            listing.GapLine();

            if (listing.ButtonText("Send Test Message"))
            {
                LLMTestUtility.SendTestMessage();
            }

            listing.End();
        }
    }
}
