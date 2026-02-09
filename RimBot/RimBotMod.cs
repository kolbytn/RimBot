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

            listing.Label("Anthropic API Key:");
            var newAnthropicKey = listing.TextEntry(Settings.anthropicApiKey);
            if (newAnthropicKey != Settings.anthropicApiKey)
                Settings.anthropicApiKey = newAnthropicKey;

            listing.Gap();

            listing.Label("OpenAI API Key:");
            var newOpenAIKey = listing.TextEntry(Settings.openAIApiKey);
            if (newOpenAIKey != Settings.openAIApiKey)
                Settings.openAIApiKey = newOpenAIKey;

            listing.Gap();

            listing.Label("Google API Key:");
            var newGoogleKey = listing.TextEntry(Settings.googleApiKey);
            if (newGoogleKey != Settings.googleApiKey)
                Settings.googleApiKey = newGoogleKey;

            listing.GapLine();

            Settings.maxTokens = (int)listing.Slider(Settings.maxTokens, 64, 4096);
            listing.Label("Max Tokens: " + Settings.maxTokens);

            listing.Gap();

            Settings.thinkingBudget = (int)(listing.Slider(Settings.thinkingBudget, 0, 8192) / 256) * 256;
            listing.Label("Thinking Budget: " + (Settings.thinkingBudget == 0 ? "Disabled" : Settings.thinkingBudget.ToString()));

            listing.GapLine();

            if (listing.ButtonText("Send Test Message"))
            {
                LLMTestUtility.SendTestMessage();
            }

            if (listing.ButtonText(SelectionTest.IsRunning ? "Stop Selection Test" : "Run Selection Test"))
            {
                SelectionTest.Toggle();
            }

            if (listing.ButtonText("View Selection Test Results"))
            {
                Find.WindowStack.Add(new SelectionTestWindow());
            }

            if (listing.ButtonText(ArchitectMode.IsRunning ? "Stop Architect Mode" : "Start Architect Mode"))
            {
                ArchitectMode.Toggle();
            }

            listing.End();
        }
    }
}
