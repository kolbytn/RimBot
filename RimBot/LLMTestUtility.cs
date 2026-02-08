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
            var maxTokens = settings.maxTokens;

            // Test each provider that has an API key
            var providers = new[]
            {
                new { Type = LLMProviderType.Anthropic, Key = settings.anthropicApiKey },
                new { Type = LLMProviderType.OpenAI, Key = settings.openAIApiKey },
                new { Type = LLMProviderType.Google, Key = settings.googleApiKey }
            };

            bool anyTested = false;
            foreach (var p in providers)
            {
                if (string.IsNullOrEmpty(p.Key))
                    continue;

                anyTested = true;
                var llmModel = LLMModelFactory.GetModel(p.Type);
                var model = llmModel.GetAvailableModels()[0];
                var apiKey = p.Key;
                var providerType = p.Type;

                Log.Message("[RimBot] Sending test message via " + providerType + " using model " + model + "...");

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
                                Log.Message("[RimBot] [" + providerType + "] Response: " + response.Content);
                                Log.Message("[RimBot] [" + providerType + "] Tokens used: " + response.TokensUsed);
                            }
                            else
                            {
                                Log.Error("[RimBot] [" + providerType + "] Error: " + response.ErrorMessage);
                            }
                        });
                    }
                    catch (Exception ex)
                    {
                        MainThreadQueue.Enqueue(() =>
                        {
                            Log.Error("[RimBot] [" + providerType + "] Test message failed: " + ex.Message);
                        });
                    }
                });
            }

            if (!anyTested)
                Log.Warning("[RimBot] No API keys configured.");
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
