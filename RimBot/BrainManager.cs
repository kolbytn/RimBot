using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace RimBot
{
    public static class BrainManager
    {
        private static readonly Dictionary<int, Brain> brains = new Dictionary<int, Brain>();
        private static readonly ConcurrentQueue<Action> mainThreadQueue = new ConcurrentQueue<Action>();
        private static float lastCaptureTime;
        private static bool captureInProgress;
        private const float IntervalSeconds = 20f;

        public static void EnqueueMainThread(Action action)
        {
            mainThreadQueue.Enqueue(action);
        }

        public static void Tick()
        {
            while (mainThreadQueue.TryDequeue(out var action))
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    Log.Error("[RimBot] Error in main thread queue: " + ex.Message);
                }
            }

            if (Current.ProgramState != ProgramState.Playing)
                return;
            if (Find.CurrentMap == null)
                return;

            SyncBrains();

            if (captureInProgress)
                return;
            if (Time.realtimeSinceStartup - lastCaptureTime < IntervalSeconds)
                return;

            lastCaptureTime = Time.realtimeSinceStartup;
            CaptureAll();
        }

        private static void CaptureAll()
        {
            var requests = new List<ScreenshotCapture.CaptureRequest>();
            var brainOrder = new List<Brain>();

            foreach (var kvp in brains)
            {
                var pawn = FindPawnById(kvp.Key);
                if (pawn == null || !pawn.Spawned)
                    continue;
                if (!kvp.Value.IsIdle)
                    continue;

                requests.Add(new ScreenshotCapture.CaptureRequest
                {
                    CenterCell = pawn.Position,
                    CameraSize = 24f,
                    PixelSize = 512
                });
                brainOrder.Add(kvp.Value);
            }

            if (requests.Count == 0)
                return;

            captureInProgress = true;
            ScreenshotCapture.StartBatchCapture(requests, results =>
            {
                for (int i = 0; i < brainOrder.Count; i++)
                {
                    brainOrder[i].SendToLLM(results[i]);
                }
                captureInProgress = false;
            });
        }

        private static void SyncBrains()
        {
            var colonists = Find.CurrentMap.mapPawns.FreeColonistsSpawned;
            var currentIds = new HashSet<int>();

            foreach (var pawn in colonists)
                currentIds.Add(pawn.thingIDNumber);

            // Remove brains for colonists no longer present
            var toRemove = new List<int>();
            foreach (var id in brains.Keys)
            {
                if (!currentIds.Contains(id))
                    toRemove.Add(id);
            }
            foreach (var id in toRemove)
            {
                Log.Message("[RimBot] Removed brain for pawn " + id);
                brains.Remove(id);
            }

            // Add brains for new colonists
            foreach (var pawn in colonists)
            {
                if (!brains.ContainsKey(pawn.thingIDNumber))
                {
                    var brain = new Brain(pawn.thingIDNumber, pawn.LabelShort);
                    brains[pawn.thingIDNumber] = brain;
                    Log.Message("[RimBot] Created brain for " + pawn.LabelShort);
                }
            }
        }

        private static Pawn FindPawnById(int pawnId)
        {
            var colonists = Find.CurrentMap.mapPawns.FreeColonistsSpawned;
            foreach (var pawn in colonists)
            {
                if (pawn.thingIDNumber == pawnId)
                    return pawn;
            }
            return null;
        }
    }
}
