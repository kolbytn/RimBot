using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using RimBot.Models;
using Verse;

namespace RimBot
{
    public class Brain
    {
        private enum State { Idle, WaitingForLLM }

        public int PawnId { get; }
        public string PawnLabel { get; }

        private State state = State.Idle;

        public bool IsIdle => state == State.Idle;

        public Brain(int pawnId, string label)
        {
            PawnId = pawnId;
            PawnLabel = label;
        }

        public void SendToLLM(string base64)
        {
            if (base64 == null)
                return;
            if (state != State.Idle)
                return;

            state = State.WaitingForLLM;
            Log.Message("[RimBot] [" + PawnLabel + "] Screenshot captured, sending to LLM...");

            var settings = RimBotMod.Settings;
            var apiKey = settings.GetActiveApiKey();
            var model = settings.GetActiveModel();
            var provider = settings.activeProvider;
            var maxTokens = settings.maxTokens;

            if (string.IsNullOrEmpty(apiKey))
            {
                Log.Warning("[RimBot] No API key set for " + provider + ", skipping.");
                state = State.Idle;
                return;
            }

            var llmModel = LLMModelFactory.GetModel(provider);
            var label = PawnLabel;
            var messages = new List<ChatMessage>
            {
                new ChatMessage("system",
                    "You are the inner mind of a RimWorld colonist named " + label +
                    ". Describe what you see briefly from this colonist's perspective."),
                new ChatMessage("user", new List<ContentPart>
                {
                    ContentPart.FromText("What do you see?"),
                    ContentPart.FromImage(base64, "image/png")
                })
            };

            Task.Run(async () =>
            {
                try
                {
                    var response = await llmModel.SendChatRequest(messages, model, apiKey, maxTokens);
                    BrainManager.EnqueueMainThread(() =>
                    {
                        if (response.Success)
                        {
                            Log.Message("[RimBot] [" + label + "] Vision: " + response.Content);
                        }
                        else
                        {
                            Log.Error("[RimBot] [" + label + "] Vision error: " + response.ErrorMessage);
                        }
                    });
                }
                catch (Exception ex)
                {
                    BrainManager.EnqueueMainThread(() =>
                    {
                        Log.Error("[RimBot] [" + label + "] Vision request failed: " + ex.Message);
                    });
                }
                finally
                {
                    state = State.Idle;
                }
            });
        }
    }
}
