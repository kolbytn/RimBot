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

        public override void ExposeData()
        {
            Scribe_Values.Look(ref anthropicApiKey, "anthropicApiKey", "");
            Scribe_Values.Look(ref openAIApiKey, "openAIApiKey", "");
            Scribe_Values.Look(ref googleApiKey, "googleApiKey", "");
            Scribe_Values.Look(ref maxTokens, "maxTokens", 1024);
            Scribe_Values.Look(ref thinkingBudget, "thinkingBudget", 2048);
            base.ExposeData();
        }
    }
}
