using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using RimBot.Models;
using UnityEngine;
using Verse;

namespace RimBot
{
    public static class ScreenshotLoop
    {
        private static float lastCaptureTime;
        private static bool isRequestInFlight;
        private static readonly ConcurrentQueue<Action> MainThreadQueue = new ConcurrentQueue<Action>();
        private const float IntervalSeconds = 20f;

        public static void Tick()
        {
            while (MainThreadQueue.TryDequeue(out var action))
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    Log.Error("[RimBot] Error processing screenshot queue action: " + ex.Message);
                }
            }

            if (Current.ProgramState != ProgramState.Playing)
                return;
            if (Find.CurrentMap == null)
                return;
            if (isRequestInFlight)
                return;
            if (Time.realtimeSinceStartup - lastCaptureTime < IntervalSeconds)
                return;

            isRequestInFlight = true;
            lastCaptureTime = Time.realtimeSinceStartup;

            // Request capture — the callback fires during the render phase
            // of this frame, after the camera has naturally rendered.
            ScreenshotCapture.RequestCurrentViewCapture(512, OnScreenshotCaptured);
        }

        private static void OnScreenshotCaptured(string base64)
        {
            if (base64 == null)
            {
                isRequestInFlight = false;
                return;
            }

            Log.Message("[RimBot] Screenshot captured, sending to LLM...");

            var settings = RimBotMod.Settings;
            var apiKey = settings.GetActiveApiKey();
            var model = settings.GetActiveModel();
            var provider = settings.activeProvider;
            var maxTokens = settings.maxTokens;

            if (string.IsNullOrEmpty(apiKey))
            {
                Log.Warning("[RimBot] No API key set for " + provider + ", skipping screenshot send.");
                isRequestInFlight = false;
                return;
            }

            var llmModel = LLMModelFactory.GetModel(provider);
            var messages = new List<ChatMessage>
            {
                new ChatMessage("system", "You are observing a RimWorld colony. Describe what you see briefly."),
                new ChatMessage("user", new List<ContentPart>
                {
                    ContentPart.FromText("What do you see in this screenshot?"),
                    ContentPart.FromImage(base64, "image/png")
                })
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
                            Log.Message("[RimBot] Vision response: " + response.Content);
                        }
                        else
                        {
                            Log.Error("[RimBot] Vision error: " + response.ErrorMessage);
                        }
                    });
                }
                catch (Exception ex)
                {
                    MainThreadQueue.Enqueue(() =>
                    {
                        Log.Error("[RimBot] Vision request failed: " + ex.Message);
                    });
                }
                finally
                {
                    isRequestInFlight = false;
                }
            });
        }
    }
}
