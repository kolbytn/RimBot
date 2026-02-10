using System;
using System.Collections.Generic;
using RimBot.Models;
using UnityEngine;
using Verse;

namespace RimBot
{
    public class RimBotMod : Mod
    {
        public static RimBotSettings Settings { get; private set; }

        private Vector2 profileScrollPos;
        private int profileToRemove = -1;

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
            Settings.EnsureProfilesLoaded();

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

            // Agent Profiles section
            listing.Label("Agent Profiles:");
            listing.Gap(4f);

            float profileAreaHeight = Math.Min(Settings.profiles.Count * 36f + 8f, 180f);
            var profileOuterRect = listing.GetRect(profileAreaHeight);
            var profileViewRect = new Rect(0f, 0f, profileOuterRect.width - 16f, Settings.profiles.Count * 36f);

            Widgets.DrawBoxSolid(profileOuterRect, new Color(0.15f, 0.15f, 0.15f, 0.5f));
            Widgets.BeginScrollView(profileOuterRect, ref profileScrollPos, profileViewRect);

            profileToRemove = -1;
            for (int i = 0; i < Settings.profiles.Count; i++)
            {
                var profile = Settings.profiles[i];
                float rowY = i * 36f;
                float rowWidth = profileViewRect.width;
                float buttonWidth = (rowWidth - 40f) / 2f;

                // Provider button
                if (Widgets.ButtonText(new Rect(0f, rowY + 2f, buttonWidth, 30f), profile.Provider.ToString()))
                {
                    var options = new List<FloatMenuOption>();
                    foreach (LLMProviderType pType in Enum.GetValues(typeof(LLMProviderType)))
                    {
                        if (string.IsNullOrEmpty(Settings.GetApiKeyForProvider(pType)))
                            continue;
                        var captured = pType;
                        var capturedIdx = i;
                        options.Add(new FloatMenuOption(pType.ToString(), () =>
                        {
                            Settings.profiles[capturedIdx].Provider = captured;
                            var models = LLMModelFactory.GetModel(captured).GetAvailableModels();
                            Settings.profiles[capturedIdx].Model = models.Length > 0 ? models[0] : "";
                            Settings.SerializeProfiles();
                        }));
                    }
                    if (options.Count > 0)
                        Find.WindowStack.Add(new FloatMenu(options));
                }

                // Model button
                if (Widgets.ButtonText(new Rect(buttonWidth + 4f, rowY + 2f, buttonWidth, 30f),
                    string.IsNullOrEmpty(profile.Model) ? "(select model)" : profile.Model))
                {
                    var models = LLMModelFactory.GetModel(profile.Provider).GetAvailableModels();
                    var options = new List<FloatMenuOption>();
                    var capturedIdx = i;
                    foreach (var m in models)
                    {
                        var capturedModel = m;
                        options.Add(new FloatMenuOption(m, () =>
                        {
                            Settings.profiles[capturedIdx].Model = capturedModel;
                            Settings.SerializeProfiles();
                        }));
                    }
                    if (options.Count > 0)
                        Find.WindowStack.Add(new FloatMenu(options));
                }

                // Remove button
                if (Widgets.ButtonText(new Rect(rowWidth - 32f, rowY + 2f, 32f, 30f), "X"))
                {
                    profileToRemove = i;
                }
            }

            Widgets.EndScrollView();

            // Process deferred removal
            if (profileToRemove >= 0 && profileToRemove < Settings.profiles.Count)
            {
                Settings.profiles.RemoveAt(profileToRemove);
                Settings.SerializeProfiles();
            }

            listing.Gap(4f);
            if (listing.ButtonText("+ Add Profile"))
            {
                // Find first provider with an API key
                LLMProviderType defaultProvider = LLMProviderType.Anthropic;
                string defaultModel = "";
                foreach (LLMProviderType pType in Enum.GetValues(typeof(LLMProviderType)))
                {
                    if (!string.IsNullOrEmpty(Settings.GetApiKeyForProvider(pType)))
                    {
                        defaultProvider = pType;
                        var models = LLMModelFactory.GetModel(pType).GetAvailableModels();
                        defaultModel = models.Length > 0 ? models[0] : "";
                        break;
                    }
                }
                Settings.profiles.Add(new AgentProfile(defaultProvider, defaultModel));
                Settings.SerializeProfiles();
            }

            // Auto-assign toggle (colony-wide, only available in-game)
            if (Current.Game != null)
            {
                var comp = Current.Game.GetComponent<ColonyAssignmentComponent>();
                if (comp != null)
                {
                    listing.Gap(4f);
                    bool autoAssign = comp.autoAssignNewColonists;
                    listing.CheckboxLabeled("Auto-assign new colonists to profiles", ref autoAssign);
                    comp.autoAssignNewColonists = autoAssign;
                }
            }

            listing.GapLine();

            Settings.maxTokens = (int)listing.Slider(Settings.maxTokens, 64, 4096);
            listing.Label("Max Tokens: " + Settings.maxTokens);

            listing.Gap();

            Settings.thinkingBudget = (int)(listing.Slider(Settings.thinkingBudget, 0, 8192) / 256) * 256;
            listing.Label("Thinking Budget: " + (Settings.thinkingBudget == 0 ? "Disabled" : Settings.thinkingBudget.ToString()));

            listing.End();
        }
    }
}
