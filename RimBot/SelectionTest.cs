using System.Collections.Generic;
using RimBot.Models;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimBot
{
    public struct TestResult
    {
        public string PawnLabel;
        public LLMProviderType Provider;
        public MapSelectionMode Mode;
        public string ObjectType;
        public int Expected;
        public int Reported;
        public int Matched;
        public double AvgError;
        public int Extra;
        public int Cycle;
    }

    public static class SelectionTest
    {
        // Set to true to auto-start the test on first capture cycle.
        // Toggle this for automated testing without the UI button.
        public static bool AutoStart = false;

        private static bool isRunning;
        private static bool autoStartChecked;
        private static int currentCycle;

        public static readonly List<TestResult> Results = new List<TestResult>();

        public static bool IsRunning => isRunning;
        public static int CurrentCycle => currentCycle;

        public static void Toggle()
        {
            isRunning = !isRunning;
            Log.Message("[RimBot] Selection test " + (isRunning ? "STARTED" : "STOPPED"));
        }

        public static void RecordResult(TestResult result)
        {
            result.Cycle = currentCycle;
            Results.Add(result);
        }

        public static void ClearResults()
        {
            Results.Clear();
            currentCycle = 0;
        }

        public static void CheckAutoStart()
        {
            if (autoStartChecked) return;
            autoStartChecked = true;
            if (AutoStart)
            {
                isRunning = true;
                Log.Message("[RimBot] Selection test auto-started.");
            }
        }

        public static void ProcessCapture(List<Brain> brains, List<Pawn> pawns, string[] results)
        {
            currentCycle++;
            for (int i = 0; i < brains.Count; i++)
            {
                var observer = pawns[i];
                var brain = brains[i];

                string objectLabel;
                List<IntVec3> positions;
                if (!FindTargetObjectType(observer, 20f, out objectLabel, out positions))
                {
                    Log.Message("[RimBot] [SELECT] [" + brain.PawnLabel
                        + "] No suitable object type found, skipping");
                    continue;
                }

                var expX = new int[positions.Count];
                var expZ = new int[positions.Count];
                for (int j = 0; j < positions.Count; j++)
                {
                    expX[j] = positions[j].x - observer.Position.x;
                    expZ[j] = positions[j].z - observer.Position.z;
                }

                var query = "Where are all the " + objectLabel + " in this image?";
                brain.GenerateMapSelection(results[i], query, brain.PreferredMode, expX, expZ, objectLabel);
            }
        }

        private static bool FindTargetObjectType(Pawn observer, float radius,
            out string objectLabel, out List<IntVec3> positions)
        {
            var groups = new Dictionary<string, List<IntVec3>>();

            foreach (var thing in Find.CurrentMap.listerThings.AllThings)
            {
                if (thing is Pawn)
                    continue;
                if (!thing.Spawned)
                    continue;
                var cat = thing.def.category;
                if (cat != ThingCategory.Plant && cat != ThingCategory.Building && cat != ThingCategory.Item)
                    continue;
                if (observer.Position.DistanceTo(thing.Position) > radius)
                    continue;

                string lbl = thing.def.label;
                if (!groups.ContainsKey(lbl))
                    groups[lbl] = new List<IntVec3>();
                groups[lbl].Add(thing.Position);
            }

            // Filter to object types with count 2-20 (avoids very rare or very common objects)
            var candidates = new List<string>();
            foreach (var kvp in groups)
            {
                if (kvp.Value.Count >= 2 && kvp.Value.Count <= 20)
                    candidates.Add(kvp.Key);
            }

            if (candidates.Count > 0)
            {
                var pick = candidates[Random.Range(0, candidates.Count)];
                objectLabel = pick;
                positions = groups[pick];
                return true;
            }

            objectLabel = null;
            positions = null;
            return false;
        }
    }
}
