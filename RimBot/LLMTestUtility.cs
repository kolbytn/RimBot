using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using RimBot.Models;
using Verse;

namespace RimBot
{
    public static class LLMTestUtility
    {
        private static readonly ConcurrentQueue<Action> MainThreadQueue = new ConcurrentQueue<Action>();

        public static void SendTestMessage()
        {
            var settings = RimBotMod.Settings;
            var apiKey = settings.GetActiveApiKey();
            var model = settings.GetActiveModel();
            var provider = settings.activeProvider;
            var maxTokens = settings.maxTokens;

            if (string.IsNullOrEmpty(apiKey))
            {
                Log.Warning("[RimBot] No API key set for " + provider);
                return;
            }

            Log.Message("[RimBot] Sending test message via " + provider + " using model " + model + "...");

            var llmModel = LLMModelFactory.GetModel(provider);
            var messages = new List<ChatMessage>
            {
                new ChatMessage("system", "You are a helpful assistant in the game RimWorld. Keep responses brief."),
                new ChatMessage("user", "Hello! Please respond with a short greeting to confirm you're working.")
            };

            Task.Run(async () =>
            {
                try
                {
                    var response = await llmModel.SendChatRequest(messages, model, apiKey, maxTokens);
                    MainThreadQueue.Enqueue(() =>
                    {
                        if (response.Success)
                        {
                            Log.Message("[RimBot] Response: " + response.Content);
                            Log.Message("[RimBot] Tokens used: " + response.TokensUsed);
                        }
                        else
                        {
                            Log.Error("[RimBot] Error: " + response.ErrorMessage);
                        }
                    });
                }
                catch (Exception ex)
                {
                    MainThreadQueue.Enqueue(() =>
                    {
                        Log.Error("[RimBot] Test message failed: " + ex.Message);
                    });
                }
            });
        }

        public static void ProcessMainThreadQueue()
        {
            while (MainThreadQueue.TryDequeue(out var action))
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    Log.Error("[RimBot] Error processing queued action: " + ex.Message);
                }
            }
        }
    }
}
