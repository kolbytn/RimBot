using System;
using System.Collections.Generic;
using RimBot.Models;
using Verse;

namespace RimBot
{
    public class RimBotSettings : ModSettings
    {
        public string anthropicApiKey = "";
        public string openAIApiKey = "";
        public string googleApiKey = "";
        public int maxTokens = 1024;
        public int thinkingBudget = 2048;
        public string serializedProfiles = "";

        [Unsaved]
        public List<AgentProfile> profiles = new List<AgentProfile>();

        [Unsaved]
        private bool profilesLoaded;

        public override void ExposeData()
        {
            Scribe_Values.Look(ref anthropicApiKey, "anthropicApiKey", "");
            Scribe_Values.Look(ref openAIApiKey, "openAIApiKey", "");
            Scribe_Values.Look(ref googleApiKey, "googleApiKey", "");
            Scribe_Values.Look(ref maxTokens, "maxTokens", 1024);
            Scribe_Values.Look(ref thinkingBudget, "thinkingBudget", 2048);
            Scribe_Values.Look(ref serializedProfiles, "serializedProfiles", "");
            base.ExposeData();

            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                DeserializeProfiles();
                profilesLoaded = true;
            }
        }

        public void EnsureProfilesLoaded()
        {
            if (!profilesLoaded)
            {
                DeserializeProfiles();
                profilesLoaded = true;
            }
        }

        public void SerializeProfiles()
        {
            var parts = new List<string>();
            foreach (var p in profiles)
            {
                parts.Add(p.Id + "|" + (int)p.Provider + "|" + p.Model);
            }
            serializedProfiles = string.Join(";;", parts.ToArray());
        }

        public void DeserializeProfiles()
        {
            profiles.Clear();
            if (string.IsNullOrEmpty(serializedProfiles))
                return;

            var parts = serializedProfiles.Split(new[] { ";;" }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts)
            {
                var fields = part.Split('|');
                if (fields.Length < 3)
                    continue;

                int providerInt;
                if (!int.TryParse(fields[1], out providerInt))
                    continue;

                profiles.Add(new AgentProfile
                {
                    Id = fields[0],
                    Provider = (LLMProviderType)providerInt,
                    Model = fields[2]
                });
            }
        }

        public AgentProfile GetProfileById(string id)
        {
            if (string.IsNullOrEmpty(id))
                return null;
            foreach (var p in profiles)
            {
                if (p.Id == id)
                    return p;
            }
            return null;
        }

        public string GetApiKeyForProvider(LLMProviderType provider)
        {
            switch (provider)
            {
                case LLMProviderType.Anthropic: return anthropicApiKey;
                case LLMProviderType.OpenAI: return openAIApiKey;
                case LLMProviderType.Google: return googleApiKey;
                default: return "";
            }
        }

        public void AddDefaultProfilesIfEmpty()
        {
            if (profiles.Count > 0)
                return;

            if (!string.IsNullOrEmpty(anthropicApiKey))
                profiles.Add(new AgentProfile(LLMProviderType.Anthropic, "claude-haiku-4-5-20251001"));

            if (!string.IsNullOrEmpty(googleApiKey))
                profiles.Add(new AgentProfile(LLMProviderType.Google, "gemini-3-flash-preview"));

            if (!string.IsNullOrEmpty(openAIApiKey))
                profiles.Add(new AgentProfile(LLMProviderType.OpenAI, "gpt-5-mini"));

            if (profiles.Count > 0)
            {
                SerializeProfiles();
                Log.Message("[RimBot] Auto-created " + profiles.Count + " default profiles from existing API keys.");
            }
        }
    }
}
