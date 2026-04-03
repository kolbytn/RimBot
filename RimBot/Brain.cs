using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using HarmonyLib;
using RimBot.Models;
using RimBot.Tools;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimBot
{
    public class Brain
    {
        private enum State { Idle, WaitingForLLM }

        public int PawnId { get; }
        public string PawnLabel { get; private set; }
        public LLMProviderType Provider { get; }
        public string Model { get; }
        public string ApiKey { get; }
        public string ProfileId { get; }

        private State state = State.Idle;
        private readonly List<HistoryEntry> history = new List<HistoryEntry>();
        private const int MaxHistoryEntries = 50;
        private List<ChatMessage> agentConversation;
        private const int ConversationTrimThreshold = 40;
        private const int ConversationTrimTarget = 24;
        private float lastRunStartedAt = float.MinValue;
        private float pauseUntil;

        public bool IsIdle => state == State.Idle;
        public float LastRunStartedAt => lastRunStartedAt;
        public bool IsPaused => Time.realtimeSinceStartup < pauseUntil;
        private const float ErrorPauseSeconds = 30f;
        public IReadOnlyList<HistoryEntry> History => history;

        // Screenshots saved alongside Player.log for spatial debugging
        private static readonly string ScreenshotSaveDir =
            System.IO.Path.Combine(Application.persistentDataPath, "RimBot_Screenshots");

        private int lastLetterTick; // Track which letters the bot has already seen

        private static string cachedSystemPrompt;
        private static string LoadSystemPrompt()
        {
            if (cachedSystemPrompt != null) return cachedSystemPrompt;
            try
            {
                var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                using (var stream = assembly.GetManifestResourceStream("RimBot.SystemPrompt.txt"))
                using (var reader = new System.IO.StreamReader(stream))
                    cachedSystemPrompt = reader.ReadToEnd().Trim();
            }
            catch (Exception ex)
            {
                Log.Warning("[RimBot] Failed to load SystemPrompt.txt: " + ex.Message);
                cachedSystemPrompt = "You are a RimWorld colonist. Use tools to manage your colony.";
            }
            return cachedSystemPrompt;
        }

        public Brain(int pawnId, string label, LLMProviderType provider, string model, string apiKey, string profileId = null)
        {
            PawnId = pawnId;
            PawnLabel = label;
            Provider = provider;
            Model = model;
            ApiKey = apiKey;
            ProfileId = profileId;
        }

        public void ClearConversation()
        {
            agentConversation = null;
            history.Clear();
        }

        public void PauseFor(float seconds)
        {
            pauseUntil = Time.realtimeSinceStartup + seconds;
            Log.Warning("[RimBot] [AGENT] [" + PawnLabel + "] Pausing for " + (int)seconds + "s");
        }

        public void RunAgentLoop()
        {
            if (state != State.Idle)
                return;

            state = State.WaitingForLLM;

            float elapsed = lastRunStartedAt > 0 ? Time.realtimeSinceStartup - lastRunStartedAt : 0;
            lastRunStartedAt = Time.realtimeSinceStartup;

            var label = PawnLabel;
            var pawnId = PawnId;

            // Get pawn position on main thread
            var pawn = BrainManager.FindPawnById(pawnId);
            if (pawn == null || !pawn.Spawned)
            {
                state = State.Idle;
                return;
            }

            // Refresh label in case pawn's nickname changed
            if (pawn.LabelShort != PawnLabel)
            {
                Log.Message("[RimBot] [AGENT] Pawn label changed: " + PawnLabel + " -> " + pawn.LabelShort);
                PawnLabel = pawn.LabelShort;
            }

            var pawnPos = pawn.Position;
            var map = Find.CurrentMap;

            // Get pawn's current job for context
            string currentActivity = "idle";
            if (pawn.CurJob != null)
            {
                currentActivity = pawn.CurJob.def.reportString;
                if (string.IsNullOrEmpty(currentActivity))
                    currentActivity = pawn.CurJob.def.label;
            }
            if (pawn.MentalState != null)
                currentActivity = "mental break: " + pawn.MentalState.def.label;

            bool isFirstCycle = agentConversation == null;
            var context = BuildContext(pawn, map);

            // Capture screenshot before launching agent — bot gets it for free in the first message
            var screenshotRequests = new List<ScreenshotCapture.CaptureRequest>
            {
                new ScreenshotCapture.CaptureRequest
                {
                    CenterCell = pawnPos,
                    CameraSize = 24,
                    PixelSize = 512,
                    PawnId = pawnId
                }
            };

            ScreenshotCapture.StartBatchCapture(screenshotRequests, screenshotResults =>
            {
                string screenshotBase64 = screenshotResults != null && screenshotResults.Length > 0 ? screenshotResults[0] : null;

                BuildConversationAndLaunch(isFirstCycle, label, currentActivity, context,
                    screenshotBase64, elapsed, pawnId, pawnPos, map);
            });
        }

        private void BuildConversationAndLaunch(bool isFirstCycle, string label, string currentActivity,
            string context, string screenshotBase64, float elapsed, int pawnId, IntVec3 pawnPos, Map map)
        {
            var maxTokens = RimBotMod.Settings.maxTokens;
            var llmModel = LLMModelFactory.GetModel(Provider);
            var apiKey = ApiKey;
            var model = Model;

            var profile = RimBotMod.Settings.GetProfileById(ProfileId);
            var thinkingLevel = profile?.ThinkingLevel ?? Models.ThinkingLevel.Medium;

            // Build user message with screenshot + context as content parts
            var userParts = new List<ContentPart>();
            if (screenshotBase64 != null)
            {
                userParts.Add(ContentPart.FromImage(screenshotBase64, "image/png"));
                // Save pre-loaded screenshot to disk for debugging
                try
                {
                    string dir = System.IO.Path.Combine(ScreenshotSaveDir, PawnLabel);
                    System.IO.Directory.CreateDirectory(dir);
                    float day = Find.TickManager.TicksGame / 60000f;
                    string filename = string.Format("day{0:F1}_iter0.png", day);
                    byte[] pngBytes = Convert.FromBase64String(screenshotBase64);
                    System.IO.File.WriteAllBytes(System.IO.Path.Combine(dir, filename), pngBytes);
                }
                catch { }
            }

            if (isFirstCycle)
            {
                Log.Message("[RimBot] [AGENT] [" + label + "] Starting agent loop...");

                var sysPrompt = LoadSystemPrompt();

                userParts.Add(ContentPart.FromText(
                    "Screenshot attached — you are at center (0,0). " +
                    "You are currently " + currentActivity + ".\n\n" + context));

                agentConversation = new List<ChatMessage>
                {
                    new ChatMessage("system", sysPrompt),
                    new ChatMessage("user", userParts)
                };
            }
            else
            {
                int elapsedSeconds = (int)elapsed;
                Log.Message("[RimBot] [AGENT] [" + label + "] Continuing conversation (" +
                    agentConversation.Count + " messages, " + elapsedSeconds + "s elapsed)...");

                // Trim conversation if too long — keep system + first user + last N messages
                TrimConversation();

                // After max iterations the conversation ends with user(tool_results).
                // Insert a synthetic assistant message to prevent consecutive user messages
                // which violates Google's alternating role requirement and causes hallucinated tool calls.
                if (agentConversation.Count > 0 && agentConversation[agentConversation.Count - 1].Role == "user")
                {
                    agentConversation.Add(new ChatMessage("assistant",
                        "I've used all my actions for this cycle. I'll reassess the situation next cycle."));
                }

                userParts.Add(ContentPart.FromText(
                    elapsedSeconds + "s elapsed. Screenshot attached. " +
                    "You are currently " + currentActivity + ".\n\n" + context));

                agentConversation.Add(new ChatMessage("user", userParts));
            }

            var messages = new List<ChatMessage>(agentConversation);

            ToolRegistry.EnsureInitialized();
            var tools = ToolRegistry.GetAllDefinitions();

            var toolContext = new ToolContext
            {
                PawnId = pawnId,
                PawnLabel = label,
                PawnPosition = pawnPos,
                Map = map,
                Brain = this
            };

            var sysPromptForHistory = isFirstCycle ? agentConversation[0].Content : null;

            Action<AgentTurn, int> onTurnComplete = (turn, index) =>
            {
                BrainManager.EnqueueMainThread(() => RecordSingleTurn(turn, index, sysPromptForHistory));
            };

            Task.Run(async () =>
            {
                try
                {
                    var result = await AgentRunner.RunAgent(
                        this, messages, tools, llmModel, model, apiKey, maxTokens, thinkingLevel, toolContext, onTurnComplete);

                    float cycleDuration = Time.realtimeSinceStartup - lastRunStartedAt;

                    BrainManager.EnqueueMainThread(() =>
                    {
                        if (result.Success)
                        {
                            // Aggregate token counts across all turns
                            int cycleInput = 0, cycleOutput = 0, cycleCache = 0, cycleReasoning = 0;
                            foreach (var turn in result.Turns)
                            {
                                cycleInput += turn.InputTokens;
                                cycleOutput += turn.OutputTokens;
                                cycleCache += turn.CacheReadTokens;
                                cycleReasoning += turn.ReasoningTokens;
                            }

                            Log.Message("[RimBot] [AGENT] [" + label + "] Completed in " +
                                result.Turns.Count + " iterations");

                            MetricsTracker.RecordAgentCycleComplete(label, result.Turns.Count,
                                cycleInput, cycleOutput, cycleCache, cycleReasoning, cycleDuration);
                        }
                        else
                        {
                            Log.Warning("[RimBot] [AGENT] [" + label + "] Failed: " +
                                result.ErrorMessage);

                            MetricsTracker.RecordAgentCycleError(label, result.ErrorMessage);

                            // Pause on rate limit or persistent API errors
                            if (IsRateLimitError(result.ErrorMessage))
                                PauseFor(ErrorPauseSeconds);
                        }

                        // Persist the conversation for next cycle
                        if (result.FinalConversation != null)
                            agentConversation = result.FinalConversation;
                    });
                }
                catch (Exception ex)
                {
                    BrainManager.EnqueueMainThread(() =>
                    {
                        Log.Warning("[RimBot] [AGENT] [" + label + "] Exception: " + ex.Message);
                    });
                }
                finally
                {
                    state = State.Idle;
                }
            });
        }

        private static bool IsRateLimitError(string errorMessage)
        {
            if (string.IsNullOrEmpty(errorMessage))
                return false;
            // Anthropic: "rate_limit_error" / "TooManyRequests"
            // OpenAI: "Rate limit" / HTTP 429
            // Google: "RESOURCE_EXHAUSTED" / HTTP 429
            var lower = errorMessage.ToLower();
            return lower.Contains("rate_limit") || lower.Contains("rate limit") ||
                   lower.Contains("toomanyrequests") || lower.Contains("429") ||
                   lower.Contains("resource_exhausted") || lower.Contains("quota");
        }

        private void TrimConversation()
        {
            if (agentConversation == null || agentConversation.Count <= ConversationTrimThreshold)
                return;

            // Trim down to target to maximize cache hits between trims
            int keepFromEnd = ConversationTrimTarget - 2;
            if (keepFromEnd < 2) keepFromEnd = 2;

            int startIdx = agentConversation.Count - keepFromEnd;
            if (startIdx < 2) startIdx = 2;

            // Find a clean boundary: must be a plain assistant message (no tool_use parts)
            // to ensure proper role alternation after system(0) + first user(1).
            // Skipping user messages here prevents consecutive user messages in the trimmed result.
            while (startIdx < agentConversation.Count - 2)
            {
                var msg = agentConversation[startIdx];
                if (msg.HasToolResult || msg.HasToolUse || msg.Role != "assistant")
                {
                    startIdx++;
                    continue;
                }
                break;
            }

            var trimmed = new List<ChatMessage>();
            trimmed.Add(agentConversation[0]); // system
            trimmed.Add(agentConversation[1]); // first user

            for (int i = startIdx; i < agentConversation.Count; i++)
                trimmed.Add(agentConversation[i]);

            agentConversation = trimmed;
            Log.Message("[RimBot] [AGENT] [" + PawnLabel + "] Trimmed conversation to " + agentConversation.Count + " messages");
        }

        /// <summary>
        /// Builds a context string with pawn status, nearby objects, resources, and research.
        /// Called on main thread before launching the agent loop.
        /// </summary>
        private string BuildContext(Pawn pawn, Map map)
        {
            var sb = new StringBuilder();

            // === PRIORITY SECTION (most important, top of context) ===

            // --- Survival status ---
            int meals = map.resourceCounter.GetCount(ThingDefOf.MealSimple)
                      + map.resourceCounter.GetCount(ThingDefOf.MealFine);
            int survivalMeals = 0;
            var survivalMealDef = DefDatabase<ThingDef>.GetNamed("MealSurvivalPack", false);
            if (survivalMealDef != null)
            {
                foreach (var t in map.listerThings.ThingsOfDef(survivalMealDef))
                    survivalMeals += t.stackCount;
            }
            bool hasStove = HasBuilding(map, "FueledStove") || HasBuilding(map, "ElectricStove");
            bool hasButcher = HasBuilding(map, "TableButcher");
            bool hasGrowingZone = false;
            bool hasStockpile = false;
            foreach (var zone in map.zoneManager.AllZones)
            {
                if (zone is Zone_Growing) hasGrowingZone = true;
                if (zone is Zone_Stockpile) hasStockpile = true;
            }
            bool hasBed = HasBuilding(map, "Bed") || HasBuilding(map, "DoubleBed") || HasBuilding(map, "RoyalBed");
            bool hasBedBP = HasBlueprintOrFrame(map, "Bed") || HasBlueprintOrFrame(map, "DoubleBed");
            bool hasEnclosedRoom = false;
            foreach (var building in map.listerBuildings.allBuildingsColonist)
            {
                if (building == null || !building.Spawned) continue;
                var room = building.GetRoom();
                if (room != null && !room.TouchesMapEdge && !room.IsDoorway)
                { hasEnclosedRoom = true; break; }
            }

            string survivalPriority = null;
            if (!hasStockpile)
                survivalPriority = "No stockpile zone. Items cannot be hauled or organized.";
            else if (meals == 0 && survivalMeals == 0)
                survivalPriority = "No food. No meals available anywhere on the map.";
            else if (meals == 0 && survivalMeals > 0)
                survivalPriority = "No meals in stockpile. " + survivalMeals + " packaged survival meals on the ground need hauling.";
            else if (!hasEnclosedRoom)
                survivalPriority = "No enclosed shelter. Need a room (walls + door) with a bed.";
            else if (!hasBed && !hasBedBP)
                survivalPriority = "No bed. Need a bed inside the enclosed room.";
            else if (!hasStove && !HasBlueprintOrFrame(map, "FueledStove") && !HasBlueprintOrFrame(map, "ElectricStove"))
                survivalPriority = "No long-term food production. Need a cook stove and butcher table.";
            else if (!hasGrowingZone)
                survivalPriority = "No growing zone for farming.";

            if (survivalPriority != null)
                sb.AppendLine("SURVIVAL PRIORITY: " + survivalPriority);
            else
                sb.AppendLine("Survival needs are met.");

            // --- Your owned assets ---
            var tracker = OwnershipTracker.Get(map);
            if (tracker != null)
            {
                var assets = tracker.GetOwnedAssets(pawn.thingIDNumber);
                if (assets.Count > 0)
                {
                    sb.AppendLine("YOUR ASSETS:");
                    foreach (var asset in assets)
                    {
                        int relCX = asset.Center.x - pawn.Position.x;
                        int relCZ = asset.Center.z - pawn.Position.z;
                        string location = "near (" + relCX + "," + relCZ + ")";

                        string line = "  " + asset.Name;
                        if (!string.IsNullOrEmpty(asset.Building))
                            line += " in " + asset.Building;
                        if (!string.IsNullOrEmpty(asset.Status))
                            line += " [" + asset.Status + "]";
                        line += " " + location;
                        if (!string.IsNullOrEmpty(asset.Description))
                            line += ": " + asset.Description;
                        sb.AppendLine(line);
                    }
                }
            }

            // --- Issues ---
            var issues = new List<string>();
            foreach (var thing in map.listerThings.AllThings)
            {
                if (thing.Faction != Faction.OfPlayer) continue;
                bool isDoor = thing.def.IsDoor;
                if (!isDoor && thing is Blueprint dbp && dbp.def.entityDefToBuild is ThingDef dd && dd.IsDoor) isDoor = true;
                if (!isDoor) continue;

                float dist = (thing.Position - pawn.Position).LengthHorizontalSquared;
                if (dist > 25 * 25) continue;

                // A door normally has walls on 2 sides (the wall line) and open passthrough
                // on the other 2. Only flag if ALL 4 cardinal sides are blocked (truly sealed)
                // or if a non-wall thing (furniture/blueprint) is blocking a passthrough side.
                int blockedSides = 0;
                int wallSides = 0;
                string lastBlocker = null;
                IntVec3 lastBlockerCell = IntVec3.Zero;
                bool lastIsBP = false;

                foreach (var adj in GenAdj.CardinalDirections)
                {
                    var adjCell = thing.Position + adj;
                    if (!adjCell.InBounds(map)) { blockedSides++; continue; }

                    bool sideBlocked = false;
                    bool sideIsWall = false;
                    foreach (var adjThing in adjCell.GetThingList(map))
                    {
                        if (adjThing == thing) continue;
                        if (adjThing is Blueprint abp2)
                        {
                            var adef = abp2.def.entityDefToBuild as ThingDef;
                            if (adef != null && !adef.IsDoor)
                            {
                                if (adef.defName == "Wall") { sideBlocked = true; sideIsWall = true; }
                                else if (adef.passability == Traversability.Impassable || adef.fillPercent > 0.3f)
                                { sideBlocked = true; lastBlocker = adef.label + " blueprint"; lastBlockerCell = adjCell; lastIsBP = true; }
                            }
                        }
                        else if (adjThing.def.passability == Traversability.Impassable && !adjThing.def.IsDoor)
                        {
                            sideBlocked = true;
                            if (adjThing.def.defName == "Wall") sideIsWall = true;
                            else { lastBlocker = adjThing.def.label; lastBlockerCell = adjCell; lastIsBP = false; }
                        }
                    }
                    if (sideBlocked) blockedSides++;
                    if (sideIsWall) wallSides++;
                }

                // Only report if door is fully sealed (4 blocked) or has a non-wall blocker
                if (blockedSides >= 4 || (lastBlocker != null && blockedSides > wallSides))
                {
                    int rx = thing.Position.x - pawn.Position.x;
                    int rz = thing.Position.z - pawn.Position.z;
                    if (lastBlocker != null)
                    {
                        int bx = lastBlockerCell.x - pawn.Position.x;
                        int bz = lastBlockerCell.z - pawn.Position.z;
                        string fix = lastIsBP
                            ? "Use architect_orders cancel at (" + bx + "," + bz + ")."
                            : "Use architect_orders deconstruct at (" + bx + "," + bz + ").";
                        issues.Add("Door at (" + rx + "," + rz + ") blocked by " + lastBlocker +
                            " at (" + bx + "," + bz + "). " + fix);
                    }
                    else
                    {
                        issues.Add("Door at (" + rx + "," + rz + ") is fully walled in with no passthrough.");
                    }
                }
            }
            var constructionDef = DefDatabase<WorkTypeDef>.GetNamed("Construction", false);
            if (constructionDef != null && pawn.WorkTypeIsDisabled(constructionDef))
                issues.Add("You are incapable of construction — other colonists must build your blueprints.");
            if (issues.Count > 0)
            {
                sb.AppendLine("ISSUES:");
                foreach (var w in issues)
                    sb.AppendLine("  - " + w);
            }

            // === CURRENT WORK STATE ===

            // --- Current job + queue ---
            if (pawn.jobs != null)
            {
                if (pawn.CurJob != null)
                {
                    string jobDesc = pawn.CurJob.def.reportString;
                    if (string.IsNullOrEmpty(jobDesc)) jobDesc = pawn.CurJob.def.label;
                    if (pawn.CurJob.targetA.HasThing)
                        jobDesc += " (" + pawn.CurJob.targetA.Thing.LabelShort + ")";
                    sb.AppendLine("Current job: " + jobDesc + ".");
                }
                if (pawn.jobs.jobQueue != null && pawn.jobs.jobQueue.Count > 0)
                {
                    var queueItems = new List<string>();
                    foreach (var qj in pawn.jobs.jobQueue)
                        queueItems.Add(qj.job.def.label);
                    sb.AppendLine("Job queue: " + string.Join(", ", queueItems) + ".");
                }
            }

            // --- Work priorities ---
            if (pawn.workSettings != null)
            {
                var highWork = new List<string>();
                foreach (var wt in DefDatabase<WorkTypeDef>.AllDefs)
                {
                    if (!pawn.WorkTypeIsDisabled(wt) && pawn.workSettings.GetPriority(wt) <= 2)
                        highWork.Add(wt.labelShort);
                }
                if (highWork.Count > 0)
                    sb.AppendLine("HIGH priority work: " + string.Join(", ", highWork) + ".");
                else
                    sb.AppendLine("All work at default priority.");
            }

            // === BACKGROUND DATA ===

            // --- Pawn needs ---
            sb.Append("Needs: ");
            if (pawn.needs != null)
            {
                var parts = new List<string>();
                if (pawn.needs.food != null)
                    parts.Add("food " + (pawn.needs.food.CurLevelPercentage * 100).ToString("F0") + "%");
                if (pawn.needs.rest != null)
                    parts.Add("rest " + (pawn.needs.rest.CurLevelPercentage * 100).ToString("F0") + "%");
                if (pawn.needs.mood != null)
                    parts.Add("mood " + (pawn.needs.mood.CurLevelPercentage * 100).ToString("F0") + "%");
                if (pawn.needs.joy != null)
                    parts.Add("joy " + (pawn.needs.joy.CurLevelPercentage * 100).ToString("F0") + "%");
                sb.Append(string.Join(", ", parts));
            }
            float healthPct = pawn.health.summaryHealth.SummaryHealthPercent;
            if (healthPct < 1f)
                sb.Append(", health " + (healthPct * 100).ToString("F0") + "%");
            sb.AppendLine(".");

            // --- Resources ---
            var resParts = new List<string>();
            AppendResource(resParts, map, ThingDefOf.WoodLog, "wood");
            AppendResource(resParts, map, ThingDefOf.Steel, "steel");
            AppendResource(resParts, map, ThingDefOf.ComponentIndustrial, "components");
            AppendResource(resParts, map, ThingDefOf.Silver, "silver");
            AppendResource(resParts, map, ThingDefOf.MealSimple, "simple meals");
            AppendResource(resParts, map, ThingDefOf.MealFine, "fine meals");
            sb.AppendLine("Resources: " + (resParts.Count > 0 ? string.Join(", ", resParts) : "none stockpiled") + ".");

            // --- Fellow colonists ---
            var colonists = map.mapPawns.FreeColonistsSpawned;
            if (colonists.Count > 1)
            {
                var names = new List<string>();
                foreach (var c in colonists)
                {
                    if (c.thingIDNumber != pawn.thingIDNumber)
                        names.Add(c.LabelShort);
                }
                sb.AppendLine("Fellow colonists: " + string.Join(", ", names) + ".");
            }

            // --- Research ---
            var currentResearch = Find.ResearchManager.GetProject();
            if (currentResearch != null)
                sb.AppendLine("Research: " + currentResearch.label +
                    " (" + (currentResearch.ProgressPercent * 100).ToString("F0") + "%).");

            // --- Alerts and events ---
            try
            {
                var uiRoot = Find.UIRoot as UIRoot_Play;
                if (uiRoot != null)
                {
                    var alertsReadout = uiRoot.alerts;
                    if (alertsReadout != null)
                    {
                        var activeAlerts = Traverse.Create(alertsReadout).Field("activeAlerts").GetValue<List<Alert>>();
                        if (activeAlerts != null && activeAlerts.Count > 0)
                        {
                            var alertTexts = new List<string>();
                            foreach (var alert in activeAlerts)
                            {
                                string label2 = alert.GetLabel();
                                if (!string.IsNullOrEmpty(label2))
                                    alertTexts.Add(label2);
                            }
                            if (alertTexts.Count > 0)
                                sb.AppendLine("Alerts: " + string.Join("; ", alertTexts) + ".");
                        }
                    }
                }
            }
            catch { }

            try
            {
                var letterStack = Find.LetterStack;
                if (letterStack != null)
                {
                    var letters = Traverse.Create(letterStack).Field("letters").GetValue<List<Letter>>();
                    if (letters != null)
                    {
                        int currentTick = Find.TickManager.TicksGame;
                        var newLetters = new List<string>();
                        foreach (var letter in letters)
                        {
                            int receivedTick = Traverse.Create(letter).Field("arrivalTick").GetValue<int>();
                            if (receivedTick > lastLetterTick)
                                newLetters.Add(letter.Label);
                        }
                        lastLetterTick = currentTick;
                        if (newLetters.Count > 0)
                            sb.AppendLine("New events: " + string.Join("; ", newLetters) + ".");
                    }
                }
            }
            catch { }

            return sb.ToString().TrimEnd();
        }

        private static void AppendResource(List<string> parts, Map map, ThingDef def, string label)
        {
            if (def == null) return;
            int count = map.resourceCounter.GetCount(def);
            if (count > 0)
                parts.Add(count + " " + label);
        }

        private static bool HasBuilding(Map map, string defName)
        {
            var def = DefDatabase<ThingDef>.GetNamed(defName, false);
            if (def == null) return false;
            return map.listerBuildings.ColonistsHaveBuilding(def);
        }

        private static bool HasBlueprintOrFrame(Map map, string defName)
        {
            foreach (var thing in map.listerThings.AllThings)
            {
                if (thing.Faction != Faction.OfPlayer) continue;
                BuildableDef target = null;
                if (thing is Blueprint bp) target = bp.def.entityDefToBuild;
                else if (thing is Frame fr) target = fr.def.entityDefToBuild;
                if (target != null && target.defName == defName)
                    return true;
            }
            return false;
        }


        private void RecordSingleTurn(AgentTurn turn, int iterIndex, string systemPrompt)
        {
            // Extract text and thinking from assistant parts
            string responseText = "";
            string thinkingText = "";
            foreach (var part in turn.AssistantParts ?? new List<ContentPart>())
            {
                if (part.Type == "text" && !string.IsNullOrEmpty(part.Text))
                    responseText += part.Text + "\n";
                else if (part.Type == "thinking" && !part.IsRedacted && !string.IsNullOrEmpty(part.Text))
                    thinkingText += part.Text + "\n";
            }
            responseText = responseText.TrimEnd();
            thinkingText = thinkingText.TrimEnd();

            // Find screenshot from tool results if any
            string screenshotBase64 = null;
            if (turn.ToolResults != null)
            {
                foreach (var tr in turn.ToolResults)
                {
                    if (!string.IsNullOrEmpty(tr.ImageBase64))
                    {
                        screenshotBase64 = tr.ImageBase64;
                        break;
                    }
                }
            }

            // Build tool call/result records
            var toolCallRecords = new List<ToolCallRecord>();
            var toolResultRecords = new List<ToolResultRecord>();

            if (turn.ToolCalls != null)
            {
                foreach (var tc in turn.ToolCalls)
                {
                    toolCallRecords.Add(new ToolCallRecord
                    {
                        Id = tc.Id,
                        Name = tc.Name,
                        ArgumentsJson = tc.Arguments?.ToString(Newtonsoft.Json.Formatting.None) ?? "{}"
                    });
                }
            }

            if (turn.ToolResults != null)
            {
                foreach (var tr in turn.ToolResults)
                {
                    toolResultRecords.Add(new ToolResultRecord
                    {
                        ToolCallId = tr.ToolCallId,
                        Success = tr.Success,
                        Content = tr.Content,
                        HasImage = !string.IsNullOrEmpty(tr.ImageBase64)
                    });
                }
            }

            var entry = new HistoryEntry
            {
                GameTick = Find.TickManager != null ? Find.TickManager.TicksGame : 0,
                Mode = "Agent",
                SystemPrompt = iterIndex == 0 ? systemPrompt : null,
                UserQuery = null,
                ResponseText = string.IsNullOrEmpty(responseText) ? turn.ErrorMessage : responseText,
                Success = turn.ErrorMessage == null,
                Provider = Provider,
                ModelName = Model,
                ScreenshotBase64 = screenshotBase64,
                ToolCalls = toolCallRecords,
                ToolResults = toolResultRecords,
                AgentIteration = iterIndex + 1,
                ThinkingText = string.IsNullOrEmpty(thinkingText) ? null : thinkingText,
                InputTokens = turn.InputTokens,
                OutputTokens = turn.OutputTokens,
                CacheReadTokens = turn.CacheReadTokens,
                ReasoningTokens = turn.ReasoningTokens
            };

            // Save screenshot to tmp/screenshots/ for spatial debugging
            if (!string.IsNullOrEmpty(screenshotBase64))
            {
                try
                {
                    string dir = System.IO.Path.Combine(ScreenshotSaveDir, PawnLabel);
                    System.IO.Directory.CreateDirectory(dir);
                    float day = Find.TickManager.TicksGame / 60000f;
                    string filename = string.Format("day{0:F1}_iter{1}.png", day, iterIndex + 1);
                    byte[] pngBytes = Convert.FromBase64String(screenshotBase64);
                    System.IO.File.WriteAllBytes(System.IO.Path.Combine(dir, filename), pngBytes);
                }
                catch (Exception ex)
                {
                    Log.Warning("[RimBot] Failed to save screenshot: " + ex.Message);
                }
            }

            history.Insert(0, entry);

            while (history.Count > MaxHistoryEntries)
            {
                history[history.Count - 1].DisposeTexture();
                history.RemoveAt(history.Count - 1);
            }
        }

    }
}
