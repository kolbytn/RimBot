using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using RimWorld;
using Verse;

namespace RimBot
{
    /// <summary>
    /// Tracks colony metrics and bot actions for debugging and optimization.
    /// Periodically logs game state snapshots and cumulative achievement stats.
    /// </summary>
    public static class MetricsTracker
    {
        // Cumulative counters (reset per game session)
        private static int totalAgentCycles;
        private static int totalAgentErrors;
        private static int totalToolCalls;
        private static int totalToolErrors;
        private static long totalInputTokens;
        private static long totalOutputTokens;
        private static long totalCacheReadTokens;
        private static long totalReasoningTokens;

        private static readonly Dictionary<string, int> toolCallCounts = new Dictionary<string, int>();
        private static readonly Dictionary<string, int> toolErrorCounts = new Dictionary<string, int>();
        private static readonly List<string> completedResearch = new List<string>();
        private static readonly Dictionary<string, int> buildingsCompleted = new Dictionary<string, int>();
        private static readonly Dictionary<string, int> itemsCrafted = new Dictionary<string, int>();
        private static int colonistDeaths;

        // Periodic logging state
        private static int lastSnapshotTick = -1;
        private const int SnapshotIntervalTicks = 30000; // ~8.3 in-game hours (~500 real seconds at 1x)
        private static int lastSummaryTick = -1;
        private const int SummaryIntervalTicks = 60000; // ~1 in-game day

        private static bool initialized;

        public static void Reset()
        {
            totalAgentCycles = 0;
            totalAgentErrors = 0;
            totalToolCalls = 0;
            totalToolErrors = 0;
            totalInputTokens = 0;
            totalOutputTokens = 0;
            totalCacheReadTokens = 0;
            totalReasoningTokens = 0;
            toolCallCounts.Clear();
            toolErrorCounts.Clear();
            completedResearch.Clear();
            buildingsCompleted.Clear();
            itemsCrafted.Clear();
            colonistDeaths = 0;
            lastSnapshotTick = -1;
            lastSummaryTick = -1;
            initialized = false;
            SpatialEvaluator.Reset();
        }

        // --- Recording methods (called from various places) ---

        public static void RecordAgentCycleComplete(string pawnLabel, int iterations, int inputTokens, int outputTokens, int cacheTokens, int reasoningTokens, float durationSeconds)
        {
            totalAgentCycles++;
            totalInputTokens += inputTokens;
            totalOutputTokens += outputTokens;
            totalCacheReadTokens += cacheTokens;
            totalReasoningTokens += reasoningTokens;

            Log.Message("[RimBot] [METRICS] [" + pawnLabel + "] Agent cycle complete: " +
                iterations + " iterations, " +
                string.Format("{0:F1}s", durationSeconds) + ", " +
                "tokens=" + inputTokens + "in/" + outputTokens + "out/" + cacheTokens + "cache/" + reasoningTokens + "reasoning");
        }

        public static void RecordAgentCycleError(string pawnLabel, string error)
        {
            totalAgentErrors++;
            // The error itself is already logged by Brain.cs; we just count it here
        }

        public static void RecordToolCall(string pawnLabel, string toolName, bool success, string resultSummary)
        {
            totalToolCalls++;
            if (toolCallCounts.ContainsKey(toolName))
                toolCallCounts[toolName]++;
            else
                toolCallCounts[toolName] = 1;

            if (!success)
            {
                totalToolErrors++;
                if (toolErrorCounts.ContainsKey(toolName))
                    toolErrorCounts[toolName]++;
                else
                    toolErrorCounts[toolName] = 1;

                Log.Message("[RimBot] [METRICS] [" + pawnLabel + "] Tool FAILED: " + toolName + " -> " + Truncate(resultSummary, 200));
            }
        }

        public static void RecordResearchComplete(string projectLabel)
        {
            if (!completedResearch.Contains(projectLabel))
            {
                completedResearch.Add(projectLabel);
                Log.Message("[RimBot] [METRICS] [ACHIEVEMENT] Research completed: " + projectLabel +
                    " (total: " + completedResearch.Count + ")");
            }
        }

        public static void RecordBuildingComplete(string defName, string builderLabel)
        {
            if (buildingsCompleted.ContainsKey(defName))
                buildingsCompleted[defName]++;
            else
                buildingsCompleted[defName] = 1;

            Log.Message("[RimBot] [METRICS] [" + builderLabel + "] Building completed: " + defName +
                " (total " + defName + ": " + buildingsCompleted[defName] + ")");
        }

        public static void RecordItemCrafted(string defName, int count, string crafterLabel)
        {
            if (itemsCrafted.ContainsKey(defName))
                itemsCrafted[defName] += count;
            else
                itemsCrafted[defName] = count;

            Log.Message("[RimBot] [METRICS] [" + crafterLabel + "] Crafted: " + count + "x " + defName +
                " (total " + defName + ": " + itemsCrafted[defName] + ")");
        }

        public static void RecordColonistDeath(string pawnLabel, string cause)
        {
            colonistDeaths++;
            Log.Message("[RimBot] [METRICS] [ACHIEVEMENT] Colonist DIED: " + pawnLabel +
                " (cause: " + cause + ", total deaths: " + colonistDeaths + ")");
        }

        // --- Periodic tick (called from BrainManager.Tick) ---

        public static void Tick()
        {
            if (Current.ProgramState != ProgramState.Playing || Find.CurrentMap == null)
                return;

            int tick = Find.TickManager.TicksGame;

            if (!initialized)
            {
                initialized = true;
                lastSnapshotTick = tick;
                lastSummaryTick = tick;
                LogGameStateSnapshot(tick);
            }

            if (tick - lastSnapshotTick >= SnapshotIntervalTicks)
            {
                lastSnapshotTick = tick;
                LogGameStateSnapshot(tick);
            }

            if (tick - lastSummaryTick >= SummaryIntervalTicks)
            {
                lastSummaryTick = tick;
                LogMetricsSummary(tick);
            }

            SpatialEvaluator.Tick();
        }

        // --- Snapshot: current game state ---

        private static void LogGameStateSnapshot(int tick)
        {
            var map = Find.CurrentMap;
            if (map == null) return;

            float daysPassed = tick / 60000f;
            var sb = new StringBuilder();
            sb.AppendLine("[RimBot] [GAMESTATE] Day " + string.Format("{0:F1}", daysPassed) + " | Tick " + tick);

            // Colony wealth
            try
            {
                float totalWealth = map.wealthWatcher.WealthTotal;
                float itemWealth = map.wealthWatcher.WealthItems;
                float buildingWealth = map.wealthWatcher.WealthBuildings;
                sb.AppendLine("  Wealth: " + string.Format("{0:F0}", totalWealth) +
                    " (items=" + string.Format("{0:F0}", itemWealth) +
                    " buildings=" + string.Format("{0:F0}", buildingWealth) + ")");
            }
            catch { }

            // Colonists
            var colonists = map.mapPawns.FreeColonistsSpawned;
            sb.Append("  Colonists: " + colonists.Count);
            if (colonistDeaths > 0)
                sb.Append(" (deaths: " + colonistDeaths + ")");
            sb.AppendLine();

            // Colonist status summary
            int downed = 0, mentalBreak = 0, injured = 0;
            foreach (var pawn in colonists)
            {
                if (pawn.Downed) downed++;
                if (pawn.InMentalState) mentalBreak++;
                else if (pawn.health?.hediffSet?.HasNaturallyHealingInjury() == true) injured++;
            }
            if (downed > 0 || mentalBreak > 0 || injured > 0)
                sb.AppendLine("  Health: " + downed + " downed, " + mentalBreak + " mental break, " + injured + " injured");

            // Key resources
            LogResourceCount(sb, map, ThingDefOf.WoodLog, "Wood");
            LogResourceCount(sb, map, ThingDefOf.Steel, "Steel");
            LogResourceCount(sb, map, ThingDefOf.ComponentIndustrial, "Components");
            LogResourceCount(sb, map, ThingDefOf.Silver, "Silver");
            LogResourceCount(sb, map, ThingDefOf.MealSimple, "Simple meals");
            LogResourceCount(sb, map, ThingDefOf.MealFine, "Fine meals");
            LogFoodCount(sb, map);

            // Current research
            var currentResearch = Find.ResearchManager.GetProject();
            if (currentResearch != null)
            {
                float progress = currentResearch.ProgressPercent;
                sb.AppendLine("  Research: " + currentResearch.label +
                    " (" + string.Format("{0:P0}", progress) + ")");
            }
            else
            {
                sb.AppendLine("  Research: none selected");
            }

            // Threats / alerts
            LogThreats(sb, map);

            // Power
            LogPowerStatus(sb, map);

            Log.Message(sb.ToString().TrimEnd());
        }

        private static void LogResourceCount(StringBuilder sb, Map map, ThingDef def, string label)
        {
            if (def == null) return;
            int count = map.resourceCounter.GetCount(def);
            if (count > 0)
                sb.AppendLine("  " + label + ": " + count);
        }

        private static void LogFoodCount(StringBuilder sb, Map map)
        {
            // Raw food / nutrition available
            float totalNutrition = 0;
            foreach (var thing in map.listerThings.ThingsInGroup(ThingRequestGroup.FoodSourceNotPlantOrTree))
            {
                if (thing.def.IsNutritionGivingIngestible && !thing.def.IsDrug)
                    totalNutrition += thing.GetStatValue(StatDefOf.Nutrition) * thing.stackCount;
            }
            if (totalNutrition > 0)
                sb.AppendLine("  Total nutrition: " + string.Format("{0:F1}", totalNutrition));
        }

        private static void LogThreats(StringBuilder sb, Map map)
        {
            // Hostile pawns on map
            int hostiles = 0;
            foreach (var pawn in map.mapPawns.AllPawnsSpawned)
            {
                if (pawn.HostileTo(Faction.OfPlayer))
                    hostiles++;
            }
            if (hostiles > 0)
                sb.AppendLine("  THREAT: " + hostiles + " hostile pawns on map");

            // Fire
            int fires = map.listerThings.ThingsOfDef(ThingDefOf.Fire).Count;
            if (fires > 0)
                sb.AppendLine("  THREAT: " + fires + " active fires");

            // Temperature extremes
            try
            {
                float outdoorTemp = map.mapTemperature.OutdoorTemp;
                if (outdoorTemp < -10 || outdoorTemp > 45)
                    sb.AppendLine("  THREAT: Extreme temperature: " + string.Format("{0:F0}", outdoorTemp) + "C");
            }
            catch { }
        }

        private static void LogPowerStatus(StringBuilder sb, Map map)
        {
            try
            {
                var powerNets = map.powerNetManager.AllNetsListForReading;
                if (powerNets == null || powerNets.Count == 0) return;

                float totalStored = 0;
                float totalProduction = 0;
                float totalConsumption = 0;

                foreach (var net in powerNets)
                {
                    foreach (var comp in net.batteryComps)
                        totalStored += comp.StoredEnergy;
                    foreach (var comp in net.powerComps)
                    {
                        if (comp.PowerOutput > 0)
                            totalProduction += comp.PowerOutput;
                        else
                            totalConsumption += -comp.PowerOutput;
                    }
                }

                if (totalProduction > 0 || totalConsumption > 0)
                    sb.AppendLine("  Power: " + string.Format("{0:F0}", totalProduction) + "W produced, " +
                        string.Format("{0:F0}", totalConsumption) + "W consumed, " +
                        string.Format("{0:F0}", totalStored) + "Wd stored");
            }
            catch { }
        }

        // --- Summary: cumulative metrics ---

        private static void LogMetricsSummary(int tick)
        {
            float daysPassed = tick / 60000f;
            var sb = new StringBuilder();
            sb.AppendLine("[RimBot] [SUMMARY] Day " + string.Format("{0:F1}", daysPassed) + " cumulative metrics:");

            // Agent stats
            sb.AppendLine("  Agent cycles: " + totalAgentCycles +
                " (errors: " + totalAgentErrors + ")");
            sb.AppendLine("  Tokens: " + totalInputTokens + " in, " + totalOutputTokens + " out, " +
                totalCacheReadTokens + " cache, " + totalReasoningTokens + " reasoning");

            // Tool call breakdown
            sb.AppendLine("  Tool calls: " + totalToolCalls + " total (" + totalToolErrors + " errors)");
            if (toolCallCounts.Count > 0)
            {
                var sorted = toolCallCounts.OrderByDescending(kvp => kvp.Value);
                var parts = new List<string>();
                foreach (var kvp in sorted)
                {
                    string entry = kvp.Key + "=" + kvp.Value;
                    int errors;
                    if (toolErrorCounts.TryGetValue(kvp.Key, out errors))
                        entry += "(" + errors + " err)";
                    parts.Add(entry);
                }
                sb.AppendLine("    " + string.Join(", ", parts));
            }

            // Achievements
            if (completedResearch.Count > 0)
                sb.AppendLine("  Research completed (" + completedResearch.Count + "): " + string.Join(", ", completedResearch));

            if (buildingsCompleted.Count > 0)
            {
                int totalBuildings = 0;
                foreach (var v in buildingsCompleted.Values) totalBuildings += v;
                var top5 = buildingsCompleted.OrderByDescending(kvp => kvp.Value).Take(5);
                var parts = new List<string>();
                foreach (var kvp in top5)
                    parts.Add(kvp.Key + "=" + kvp.Value);
                sb.AppendLine("  Buildings completed (" + totalBuildings + "): " + string.Join(", ", parts));
            }

            if (itemsCrafted.Count > 0)
            {
                int totalItems = 0;
                foreach (var v in itemsCrafted.Values) totalItems += v;
                var top5 = itemsCrafted.OrderByDescending(kvp => kvp.Value).Take(5);
                var parts = new List<string>();
                foreach (var kvp in top5)
                    parts.Add(kvp.Key + "=" + kvp.Value);
                sb.AppendLine("  Items crafted (" + totalItems + "): " + string.Join(", ", parts));
            }

            if (colonistDeaths > 0)
                sb.AppendLine("  Colonist deaths: " + colonistDeaths);

            // Survival milestone
            sb.AppendLine("  SURVIVAL: " + string.Format("{0:F1}", daysPassed) + " days");

            Log.Message(sb.ToString().TrimEnd());
        }

        private static string Truncate(string s, int maxLen)
        {
            if (string.IsNullOrEmpty(s) || s.Length <= maxLen) return s;
            return s.Substring(0, maxLen) + "...";
        }
    }
}
