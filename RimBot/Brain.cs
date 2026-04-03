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

                var sysPrompt = "You are the brain of a RimWorld colonist named " + label + ". Play RimWorld. " +
                    "Each cycle, you receive a screenshot and full status report — no need to request them. " +
                    "Use tools to ACT, not just observe. " +
                    "Use architect_* tools to build (structure, production, furniture, power, security, " +
                    "misc, floors, ship, temperature, joy) — call list_buildables first to see available items. " +
                    "Use architect_orders for mining, harvesting, hauling, hunting, deconstructing, and more. " +
                    "Use architect_zone for stockpiles, growing zones, and area management. " +
                    "Coordinates are relative to you at (0,0). +X=east, +Z=north. " +
                    "Areas highlighted in red in screenshots belong to other colonists — do not build there. " +
                    "You can only interact with your own colonists — visitors and NPCs cannot be controlled. " +
                    "BUILDING RULES: " +
                    "Finish one room before starting another. " +
                    "A room needs 4 walls forming a rectangle, exactly 1 door, then furniture inside. " +
                    "If the status says 'Room is INCOMPLETE', fix gaps before building anything else. " +
                    "If a door is BLOCKED, remove the obstruction: use cancel for blueprints, deconstruct for built walls. " +
                    "Place workbenches INSIDE rooms with clearance in front — never outdoors. " +
                    "Before placing buildings, use scan_area to check for existing blueprints. " +
                    "Be patient — construction and research take time. Don't change work priorities unless something is wrong.";

                userParts.Add(ContentPart.FromText(
                    "Begin. Screenshot attached — you are at center (0,0). " +
                    "+X=east (right), +Z=north (up). You are currently " + currentActivity + ".\n\n" +
                    context + "\n\nReview the CRITICAL ISSUES first if any, then act."));

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
                    "Continue. " + elapsedSeconds + "s elapsed. Screenshot attached. " +
                    "You are currently " + currentActivity + ".\n\n" + context +
                    "\n\nReview the CRITICAL ISSUES first if any, then act."));

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

            // --- Pawn needs ---
            sb.Append("Your status: ");
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

            // --- Equipment ---
            if (pawn.equipment?.Primary != null)
                sb.AppendLine("Weapon: " + pawn.equipment.Primary.def.label);

            // --- Key resources ---
            sb.Append("Colony resources: ");
            var resParts = new List<string>();
            AppendResource(resParts, map, ThingDefOf.WoodLog, "wood");
            AppendResource(resParts, map, ThingDefOf.Steel, "steel");
            AppendResource(resParts, map, ThingDefOf.ComponentIndustrial, "components");
            AppendResource(resParts, map, ThingDefOf.Silver, "silver");
            AppendResource(resParts, map, ThingDefOf.MealSimple, "simple meals");
            AppendResource(resParts, map, ThingDefOf.MealFine, "fine meals");
            if (resParts.Count > 0)
                sb.AppendLine(string.Join(", ", resParts) + ".");
            else
                sb.AppendLine("none stockpiled.");

            // --- Research ---
            var currentResearch = Find.ResearchManager.GetProject();
            if (currentResearch != null)
            {
                sb.AppendLine("Research: " + currentResearch.label +
                    " (" + (currentResearch.ProgressPercent * 100).ToString("F0") + "% done).");
            }
            else
            {
                sb.AppendLine("Research: NONE SELECTED — use list_research and set_research to advance technology.");
            }

            // --- Colonist count ---
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

            // --- Threats ---
            int fires = map.listerThings.ThingsOfDef(ThingDefOf.Fire).Count;
            if (fires > 0)
                sb.AppendLine("WARNING: " + fires + " active fires!");

            // --- Critical infrastructure warnings ---
            var warnings = new List<string>();

            // Food: check for meals and cooking infrastructure
            int meals = map.resourceCounter.GetCount(ThingDefOf.MealSimple)
                      + map.resourceCounter.GetCount(ThingDefOf.MealFine);
            bool hasStove = HasBuilding(map, "FueledStove") || HasBuilding(map, "ElectricStove");
            bool hasButcher = HasBuilding(map, "TableButcher");
            bool hasGrowingZone = false;
            foreach (var zone in map.zoneManager.AllZones)
            {
                if (zone is Zone_Growing) { hasGrowingZone = true; break; }
            }

            if (meals == 0 && !hasStove)
                warnings.Add("NO MEALS and no cook stove — you will starve. Build a fueled stove (production category) and a butcher table urgently.");
            else if (meals == 0)
                warnings.Add("NO MEALS — cook food at your stove immediately.");
            else if (meals < 5)
                warnings.Add("Low meals (" + meals + ") — prioritize cooking.");

            if (!hasGrowingZone)
                warnings.Add("No growing zones — create a growing zone with architect_zone to farm food.");

            // Research: check for bench and work priority
            if (currentResearch != null && currentResearch.ProgressPercent < 0.01f)
            {
                bool hasResearchBench = HasBuilding(map, "SimpleResearchBench") || HasBuilding(map, "HiTechResearchBench");
                if (!hasResearchBench)
                    warnings.Add("Research is set but you have NO research bench — build a simple research bench (production category) so research can progress.");
                else if (pawn.workSettings != null)
                {
                    var researchWork = DefDatabase<WorkTypeDef>.GetNamed("Research", false);
                    if (researchWork != null && pawn.workSettings.GetPriority(researchWork) == 0)
                        warnings.Add("Research bench exists but research work is DISABLED. Use set_work_priority to enable research.");
                    else if (researchWork != null && pawn.workSettings.GetPriority(researchWork) > 3)
                        warnings.Add("Research work priority is low (" + pawn.workSettings.GetPriority(researchWork) + "). Set it to 1 or 2 to make progress.");
                }
            }

            // Incapable colonist warnings
            if (pawn.WorkTypeIsDisabled(DefDatabase<WorkTypeDef>.GetNamed("Construction", false)))
                warnings.Add("You are INCAPABLE of construction — blueprints you place must be built by other colonists. Focus on other tasks.");
            if (pawn.WorkTypeIsDisabled(DefDatabase<WorkTypeDef>.GetNamed("Cooking", false)) && !hasStove)
                warnings.Add("You are incapable of cooking — another colonist must cook for you.");

            // Shelter: check for bed
            bool hasBed = HasBuilding(map, "Bed") || HasBuilding(map, "DoubleBed") || HasBuilding(map, "RoyalBed");
            if (!hasBed)
                warnings.Add("No bed — build a bed inside a roofed room to avoid mood penalties.");

            // Stockpile
            bool hasStockpile = false;
            foreach (var zone in map.zoneManager.AllZones)
            {
                if (zone is Zone_Stockpile) { hasStockpile = true; break; }
            }
            if (!hasStockpile)
                warnings.Add("No stockpile zone — create one with architect_zone so items can be hauled and organized.");

            // Blocked doors — check for impassable things/blueprints adjacent to doors
            foreach (var thing in map.listerThings.AllThings)
            {
                if (thing.Faction != Faction.OfPlayer) continue;
                bool isDoor = thing.def.IsDoor;
                if (!isDoor && thing is Blueprint dbp && dbp.def.entityDefToBuild is ThingDef dd && dd.IsDoor) isDoor = true;
                if (!isDoor) continue;

                float dist = (thing.Position - pawn.Position).LengthHorizontalSquared;
                if (dist > 25 * 25) continue; // only check nearby doors

                foreach (var adj in GenAdj.CardinalDirections)
                {
                    var adjCell = thing.Position + adj;
                    if (!adjCell.InBounds(map)) continue;
                    foreach (var adjThing in adjCell.GetThingList(map))
                    {
                        if (adjThing == thing) continue;
                        string blocker = null;
                        bool isBlueprint = false;
                        if (adjThing is Blueprint abp)
                        {
                            var adef = abp.def.entityDefToBuild as ThingDef;
                            if (adef != null && !adef.IsDoor && (adef.passability == Traversability.Impassable || adef.fillPercent > 0.3f))
                            { blocker = adef.label + " blueprint"; isBlueprint = true; }
                        }
                        else if (adjThing.def.passability == Traversability.Impassable && !adjThing.def.IsDoor)
                            blocker = adjThing.def.label;

                        if (blocker != null)
                        {
                            int rx = thing.Position.x - pawn.Position.x;
                            int rz = thing.Position.z - pawn.Position.z;
                            int bx = adjCell.x - pawn.Position.x;
                            int bz = adjCell.z - pawn.Position.z;
                            string fix = isBlueprint
                                ? "Use architect_orders cancel at (" + bx + "," + bz + ")."
                                : "Use architect_orders deconstruct at (" + bx + "," + bz + ").";
                            warnings.Add("Door at (" + rx + "," + rz + ") is BLOCKED by " + blocker +
                                " at (" + bx + "," + bz + "). " + fix);
                            break;
                        }
                    }
                }
            }

            if (warnings.Count > 0)
            {
                sb.AppendLine("CRITICAL ISSUES:");
                foreach (var w in warnings)
                    sb.AppendLine("  - " + w);
            }

            // --- Existing infrastructure (prevents re-placing after conversation trim) ---
            // Check both completed buildings AND blueprints/frames
            var infra = new List<string>();
            if (hasStove || HasBlueprintOrFrame(map, "FueledStove") || HasBlueprintOrFrame(map, "ElectricStove"))
                infra.Add("cook stove");
            if (hasButcher || HasBlueprintOrFrame(map, "TableButcher"))
                infra.Add("butcher table");
            if (HasBuilding(map, "SimpleResearchBench") || HasBuilding(map, "HiTechResearchBench") ||
                HasBlueprintOrFrame(map, "SimpleResearchBench") || HasBlueprintOrFrame(map, "HiTechResearchBench"))
                infra.Add("research bench");
            if (hasStockpile) infra.Add("stockpile");
            if (hasGrowingZone) infra.Add("growing zone");
            if (infra.Count > 0)
                sb.AppendLine("You already have (built or blueprinted): " + string.Join(", ", infra) + ". Do not rebuild these.");

            // --- Nearby structures (helps bot complete rooms across cycles) ---
            AppendNearbyStructures(sb, pawn, map);

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

            // --- Current work priorities (set by you) ---
            if (pawn.workSettings != null)
            {
                var highWork = new List<string>();
                foreach (var wt in DefDatabase<WorkTypeDef>.AllDefs)
                {
                    if (!pawn.WorkTypeIsDisabled(wt) && pawn.workSettings.GetPriority(wt) <= 2)
                        highWork.Add(wt.labelShort);
                }
                if (highWork.Count > 0)
                    sb.AppendLine("Work priorities you set to HIGH: " + string.Join(", ", highWork) +
                        ". All other work is active at low priority.");
                else
                    sb.AppendLine("All work is at low (default) priority. No high-priority work set by you.");
            }

            // --- Active alerts from the game ---
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
                                sb.AppendLine("GAME ALERTS: " + string.Join("; ", alertTexts) + ".");
                        }
                    }
                }
            }
            catch { }

            // --- New letters since last cycle ---
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
                            sb.AppendLine("NEW EVENTS: " + string.Join("; ", newLetters) + ".");
                    }
                }
            }
            catch { }

            // --- Day ---
            float daysPassed = Find.TickManager.TicksGame / 60000f;
            sb.AppendLine("Colony day: " + daysPassed.ToString("F1") + ".");

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

        /// <summary>
        /// Scans nearby walls, doors, and blueprints to help the bot understand where its
        /// existing structures are relative to its current position. Reports bounding box
        /// and gap information so the bot can complete rooms across cycles.
        /// </summary>
        private static void AppendNearbyStructures(StringBuilder sb, Pawn pawn, Map map)
        {
            var pawnPos = pawn.Position;
            int scanRadius = 20;

            // Collect wall/door positions (completed + blueprints + frames)
            var wallPositions = new List<IntVec3>();
            var doorPositions = new List<IntVec3>();

            for (int dx = -scanRadius; dx <= scanRadius; dx++)
            {
                for (int dz = -scanRadius; dz <= scanRadius; dz++)
                {
                    var cell = new IntVec3(pawnPos.x + dx, 0, pawnPos.z + dz);
                    if (!cell.InBounds(map)) continue;

                    foreach (var thing in cell.GetThingList(map))
                    {
                        if (thing.Faction != Faction.OfPlayer) continue;

                        string defName = null;
                        if (thing is Blueprint bp)
                            defName = bp.def.entityDefToBuild?.defName;
                        else if (thing is Frame fr)
                            defName = fr.def.entityDefToBuild?.defName;
                        else if (thing.def.building != null)
                            defName = thing.def.defName;

                        if (defName == null) continue;

                        if (defName == "Wall")
                            wallPositions.Add(thing.Position);
                        else if (defName == "Door")
                            doorPositions.Add(thing.Position);
                    }
                }
            }

            if (wallPositions.Count == 0) return;

            // Compute bounding box in relative coordinates
            int minX = int.MaxValue, maxX = int.MinValue;
            int minZ = int.MaxValue, maxZ = int.MinValue;
            foreach (var pos in wallPositions)
            {
                int rx = pos.x - pawnPos.x;
                int rz = pos.z - pawnPos.z;
                if (rx < minX) minX = rx;
                if (rx > maxX) maxX = rx;
                if (rz < minZ) minZ = rz;
                if (rz > maxZ) maxZ = rz;
            }
            foreach (var pos in doorPositions)
            {
                int rx = pos.x - pawnPos.x;
                int rz = pos.z - pawnPos.z;
                if (rx < minX) minX = rx;
                if (rx > maxX) maxX = rx;
                if (rz < minZ) minZ = rz;
                if (rz > maxZ) maxZ = rz;
            }

            sb.AppendLine("Nearby structures: " + wallPositions.Count + " walls, " +
                doorPositions.Count + " doors in range. Bounding box: (" +
                minX + "," + minZ + ") to (" + maxX + "," + maxZ + ") relative to you.");

            // Check for gaps in the bounding box perimeter — these are where the room is incomplete
            var wallSet = new HashSet<long>();
            foreach (var pos in wallPositions)
                wallSet.Add((long)pos.x << 32 | (long)(uint)pos.z);
            foreach (var pos in doorPositions)
                wallSet.Add((long)pos.x << 32 | (long)(uint)pos.z);

            // Check all 4 edges of the bounding box for gaps
            int absMinX = pawnPos.x + minX, absMaxX = pawnPos.x + maxX;
            int absMinZ = pawnPos.z + minZ, absMaxZ = pawnPos.z + maxZ;
            var gapPositions = new List<string>();

            for (int x = absMinX; x <= absMaxX; x++)
            {
                if (!wallSet.Contains((long)x << 32 | (long)(uint)absMinZ))
                    gapPositions.Add("(" + (x - pawnPos.x) + "," + (absMinZ - pawnPos.z) + ")");
                if (!wallSet.Contains((long)x << 32 | (long)(uint)absMaxZ))
                    gapPositions.Add("(" + (x - pawnPos.x) + "," + (absMaxZ - pawnPos.z) + ")");
            }
            for (int z = absMinZ + 1; z < absMaxZ; z++)
            {
                if (!wallSet.Contains((long)absMinX << 32 | (long)(uint)z))
                    gapPositions.Add("(" + (absMinX - pawnPos.x) + "," + (z - pawnPos.z) + ")");
                if (!wallSet.Contains((long)absMaxX << 32 | (long)(uint)z))
                    gapPositions.Add("(" + (absMaxX - pawnPos.x) + "," + (z - pawnPos.z) + ")");
            }

            if (gapPositions.Count > 0 && gapPositions.Count <= 15)
            {
                sb.AppendLine("Room is INCOMPLETE: " + gapPositions.Count +
                    " gaps in perimeter. Place walls at: " + string.Join(", ", gapPositions));
            }
            else if (gapPositions.Count > 15)
            {
                sb.AppendLine("Room is INCOMPLETE: " + gapPositions.Count + " gaps — too many walls missing. Consider starting a smaller room.");
            }
            else if (gapPositions.Count == 0 && wallPositions.Count >= 8)
                sb.AppendLine("Room perimeter is complete.");
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
