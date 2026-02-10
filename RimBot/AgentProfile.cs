using System;
using RimBot.Models;

namespace RimBot
{
    public class AgentProfile
    {
        public string Id { get; set; }
        public LLMProviderType Provider { get; set; }
        public string Model { get; set; }
        public ThinkingLevel ThinkingLevel { get; set; }

        public AgentProfile()
        {
            Id = Guid.NewGuid().ToString("N");
            Provider = LLMProviderType.Anthropic;
            Model = "";
            ThinkingLevel = ThinkingLevel.Medium;
        }

        public AgentProfile(LLMProviderType provider, string model)
        {
            Id = Guid.NewGuid().ToString("N");
            Provider = provider;
            Model = model;
            ThinkingLevel = ThinkingLevel.Medium;
        }
    }
}
