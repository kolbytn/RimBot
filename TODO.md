# Known Issues & TODO

Observations from automated test runs (Gemini 3 Flash, single bot, 5-minute sessions).

## Bugs to Fix

- [x] **Nickname mismatch** — Fixed: `Brain.PawnLabel` refreshed from live pawn each cycle.
- [x] **Conversation amnesia after trim** — Fixed: `BuildContext()` injects existing infrastructure. Verified: 0 duplicate buildings.
- [x] **"Interaction spot blocked" loop** — Fixed: clearance warning in production tool description + skip reasons in logs. Verified: stove/butcher on 1st try.
- [ ] **Game pauses from colonist requests/letters** — RimWorld pauses the game when colonists make requests (e.g. join colony, trade caravans) or when letters arrive (raids, events). This interrupts agent cycles since game ticks stop. Need a Harmony patch to auto-dismiss or auto-accept these, or force-unpause after they appear.
- [ ] **ITab history message open/close tied to position not message** — In the RimBot ITab (in-game agent history UI), expanding/collapsing a message entry is tied to the entry's position in the list rather than to the message itself. When new messages arrive and shift the list, the wrong entry appears expanded. Fix: track open/close state by message ID or reference, not by list index.

## Behavioral Improvements

- [x] **Research never progresses** — Fixed: BuildContext now warns if research work priority is disabled or too low when a bench exists. Also warns if no bench exists.
- [x] **PackagedSurvivalMeal defName unknown** — Fixed: forward-only fuzzy matching in `FindOnMapTool`. Still triggers occasionally ("MealPackagedSurvival").
- [ ] **Battery always chosen as research** — Every run picks "battery". Low priority — may be fine.
- [x] **Incapable colonists can't execute plans** — Fixed: BuildContext warns if pawn is incapable of construction or cooking.
- [x] **Wildlife name mismatch** — Fixed: `set_wildlife_operation` now strips trailing numeric IDs and matches by species name. "Gazelle 58284" → matches "gazelle".
- [x] **"Medical" work type not matched** — Fixed: synonym map in SetWorkPriorityTool maps "medical" → "doctor", "farming" → "grow", etc.
- [ ] **"HorseshoePin" vs "HorseshoesPin"** — Minor LLM defName guessing issue. Low priority.

## Spatial / Placement Issues

- [x] **Blueprints not tracked in metrics** — Fixed: MetricsTracker tracks blueprint placements. SpatialEvaluator checks blueprints/frames.
- [x] **Research bench blocks door** — Addressed: blueprints now visible in scan_area/inspect_cell, bot scans before building.
- [x] **Duplicate outdoor production blueprints** — Fixed: SpatialEvaluator detects and penalizes outdoor workbench blueprints. Colony score penalizes -5 each.
- [x] **Colony score misses blueprint problems** — Fixed: score includes outdoor bp penalty, pending blueprint count, blocked bench penalty.
- [x] **Blueprints invisible to scan_area/inspect_cell** — Fixed: both tools now show "wall (blueprint)" and "fueled stove (frame, 45% built)" entries. Verified: scan_area calls jumped from 5 to 17, colony score from 3 to 45.
- [x] **Coordinate grid overlay** — Added grid lines + labels + crosshair to screenshots for spatial debugging.
- [x] **Room gap detection** — BuildContext reports exact gap positions in room perimeter so bot can close incomplete rooms.

## Metrics Baselines (Gemini 3 Flash, 1 bot, 5 min)

| Metric | Before optimizations | After optimizations |
|---|---|---|
| Colony score (Day 2) | 3 | 45 |
| Enclosed rooms | 0 | 1 |
| Spatial warnings | 2-3 | 0 |
| Blueprint completion rate | ~60% | 95% |
| Tool errors per day | 3-7 | 0-1 |
| Walls completed | 15-43 | 15 (efficient) |
| Stove/butcher/bench built | Rarely | Yes |
| scan_area calls per day | 5 | 17 |
| Game days reached (5 min) | 2-5 | 2 |

## Deeper Work Needed

These issues require more than quick fixes — they need architectural or strategic changes.

- [ ] **Wood starvation stalls colony** — Bot runs out of wood and all construction stops permanently. No wood = no buildings = score frozen. Need: low-wood warning in context, auto-designate tree cutting, or prioritize wood harvesting when resources are low. (Observed in Vivi 10-min run: 0 construction progress from Day 1-3.)
- [ ] **Work priority thrashing** — Bot spends 3-4 iterations per cycle re-setting work priorities that haven't changed (30 calls in 11 cycles). Need: either include current priorities in context so bot sees they're already set, or rate-limit priority changes.
- [ ] **Room completion follow-through** — Bot starts room walls but gets distracted and never finishes. Gap positions are reported in context but bot ignores them in later cycles. Need: stronger prompting or a "finish what you started" mechanism.
- [ ] **Table blocking door / double-thick walls** — Bot places furniture blueprints that block its own door, and places redundant interior walls. These are spatial reasoning errors not caught by current heuristics. Detection ideas: check door adjacency for impassable blueprints; detect walls with walls on both sides in the same axis. Better long-term fix: improve the bot's building planning rather than just detecting errors.
- [x] **Duplicate production blueprints despite context** — "You already have" only checked completed buildings, not blueprints. Fixed: now checks blueprints/frames too. Further improved: owned asset system now shows all owned structures by name.
- [ ] **Enclosed rooms without doors** — Should be surfaced as an error in the bot's context so it can add a door. Currently detected by SpatialEvaluator in the blocked_doors metric but not presented to the bot as an actionable issue for doorless rooms.
- [ ] **Trapped pawn detection** — Need a reliable way to detect when a bot has walled itself in. Map center reachability check is too fragile (center may be blocked for unrelated reasons). Better approach: check if pawn can reach any other colonist, or use flood-fill from pawn position to see if reachable area is suspiciously small.

## Long-Duration Test Issues (25-min, 3x Gemini Pro, 2026-04-18)

Colony: Jupiter, Lover, Kena. Score peaked at 52 (Day 3), settled at 50 (Day 9), Kena died Day 12.

### Issue 1: Scan/Inspect Loop — Kena starved to death without building anything useful

**Observed behavior:** Kena completed 17 agent cycles across 25 minutes but placed only 3 walls, 2 doors, 1 stove, and 0 growing zones. She never established food production and died of malnutrition on Day 12. Most cycles hit the 15-iteration limit while accomplishing almost nothing.

**Failure pattern — three interlocking problems:**

1. **Ownership blocks on harvest/cut_plant orders.** Every attempt to designate plant harvesting or cutting was rejected with `designated=0 skipped=N`. Kena tried at least 8 times across multiple cycles (3, 24, 14, 21, 49 cells — all skipped). The plants were in areas belonging to other colonists. She never adapted by designating trees/plants in unclaimed territory.

2. **Stove placement blocked wall construction.** In cycles 3-4, Kena placed a FueledStove but then couldn't wall around it: `architect_structure(Wall): placed=0 skipped=15 (Wall would block fueled stove's interaction spot)`. She tried twice (15 then 16 cells), found the stove with `find_on_map(FueledStove)`, searched for the blueprint, attempted to cancel it (`skipped=3` — couldn't cancel others' things or already-built items), but never successfully relocated the stove. Multiple cycles burned on this.

3. **Pure exploration loops with no action.** One full cycle (cycle ~7) was 15 consecutive `scan_area` calls at expanding coordinates: (0,0), (-15,-15), (25,-5), (25,10), (25,25), (25,35), (0,25), (0,35), (0,-20), (0,15), (15,15), (25,-20) — scanning the entire map with zero building actions. Input tokens climbed from 33K to 51K in a single cycle. Another cycle featured 4 consecutive identical `architect_furniture(Bed)` calls all returning "Space already occupied."

4. **Too-late pivot to food.** By cycle ~9, Kena finally searched for food (`find_on_map(MealSimple)`, `find_on_map(RawPotatoes)`, `find_on_map(RawBerries)`) and tried hunting (`set_wildlife_operation(cassowary, hunt)`), but was already severely malnourished. Downed in cycle ~12, died in cycle ~15.

**Why reasoning tokens don't help diagnose:** Gemini's thinking content isn't exposed in the API response, only token counts. We can see Kena spent 1,765 reasoning tokens at the start of cycle 4 (her highest) and 1,908 tokens checking pawn status in cycle 5, but can't read what she was actually thinking.

**Potential fixes to investigate:**
- [ ] **Better error messages for ownership-blocked designations.** When harvest/cut_plant/haul returns `skipped=N`, include *why* — "These plants are in Lover's territory. Designate plants in unclaimed areas instead." Currently the skip count gives no ownership attribution.
- [ ] **Stove interaction spot blocking should suggest relocating.** When walls are blocked by a stove's interaction spot, the error should say "Consider canceling/deconstructing the stove and placing it in a location with more clearance" rather than just stating the block.
- [ ] **Scan-only cycle detection.** If an agent cycle has >10 iterations with 0 successful placements/designations, inject a mid-cycle prompt: "You've spent many iterations scanning without taking action. Place a building or designate a task." Or hard-cap consecutive scan_area calls at 5.
- [ ] **Growing zone as Tier 0 requirement.** The tiered survival system should make placing a growing zone a hard Tier 0 requirement alongside stockpile, since food is the #1 cause of death in long runs.
- [ ] **Repeated identical tool call detection.** Kena called `architect_furniture(Bed)` 4 times in a row with identical arguments, all failing. After 2 identical failures, inject "This placement keeps failing. Try different coordinates or a different rotation."

### Issue 2: Inaccessible Room Loop — Jupiter spent 5 days fixing door placement

**Observed behavior:** Jupiter built a 19-wall room in cycle 1, then spent Days 5-9 (cycles 4-8, roughly 5 full agent cycles) trying to fix a room that was "sealed and inaccessible." The colony score dropped from 50 to 20 during this period due to exposed beds and 7 double-thick wall pairs accumulating from the churn.

**What happened step by step:**
- Jupiter built walls + an interior door but **no exterior door**, sealing the room entirely.
- Day 5: "I realized there was an issue with the door placement... leaving the room inaccessible." Deconstructed wall sections, placed door blueprints.
- Day 6: "I've canceled my previous attempts and am instead deconstructing two wall sections to replace them with doors." More deconstruct/cancel/door cycles.
- Day 7: Score collapsed to 20. "I am replacing the remaining blueprints with new blueprints for the exterior doors." 2 beds now exposed, 7 double-thick wall pairs.
- Day 8: "the interior door separated the space into two rooms, while the exterior lacked any doors at all." Attempted mass cancel (66 cells, only 1 designated). Finally began to untangle.
- Day 9: Room finally enclosed, score recovered to 50. Jupiter then stated: "I have completed the physical infrastructure for the kitchen by enclosing the room, but I lack the ability to set up cooking bills directly via my tools, and I am personally incapable of the Cook work type."

**Compounding factor:** Jupiter was incapable of cooking, so even after 5 days of building the kitchen, it was useless without another colonist to cook. Meanwhile the colony was starving.

**Potential fixes to investigate:**
- [ ] **Inaccessible room warning in BuildContext.** When a room is detected as sealed with no exterior-connected door, inject this as a critical warning with specific coordinates: "Room at (X,Y) is sealed — no door connects to outside. Deconstruct the wall at (A,B) and place a door there." The existing `BuildContext` already has room gap detection — extend it to detect rooms with walls on all sides but no door reachable from outside.
- [ ] **Door placement validation.** Before placing a door, check if it actually connects two distinct reachable areas (inside room ↔ outside). Warn if a door is interior-only and the room has no other exit.
- [ ] **Limit deconstruct/rebuild churn.** Track how many deconstruct+rebuild cycles have happened on the same structure across agent cycles. If >3, suggest tearing down the entire room and rebuilding with a clearer plan.
- [ ] **Incapable work types in planning context.** BuildContext should prominently list work types the pawn is incapable of, so it doesn't spend 5 days building a kitchen it can't use. Currently only warns about construction/cooking incapability — should influence building strategy: "You are incapable of Cook. Do not prioritize building cooking infrastructure — focus on structures other colonists can use, or on work you can do."

### Issue 3: Work Priority Reset — Lover's priorities reverted every cycle, causing starvation

**Observed behavior:** Lover set Cook/Construct/Haul/Plant Cut to high priority at least 10 separate times across the test. Every cycle, the priorities had reverted to non-high, and Lover had to re-set them. Lover explicitly noted this: "I noticed that my work priorities had reset to default, which was causing me to idle while the colony was starving." This directly caused the starvation crisis — food existed (22 potatoes, 41 later) but nobody cooked it because Cook priority kept reverting.

**Evidence from logs:**
```
set_work_priority(Cook → high): 1 changed
set_work_priority(Cook, Construct, Plant Cut, Grow → high): 4 changed
set_work_priority(Cook, Plant Cut, Grow → high): 3 changed   [already reset!]
set_work_priority(Cook, Haul → high): 2 changed
set_work_priority(Doctor, Plant Cut, Cook, Haul → high): 4 changed
set_work_priority(Doctor, Cook, Haul → high): 3 changed
set_work_priority(Cook, Construct, Plant Cut, Haul → high): 4 changed  ["reset to default"]
set_work_priority(Cook, Construct, Plant Cut, Haul → high): 4 changed  ["reset again"]
set_work_priority(Cook, Haul, Construct, Plant Cut → high): 4 changed
```
Each time "N changed" confirms the priorities were genuinely not at 1 (high) when checked.

**The code looks correct:** `SetWorkPriorityTool.Execute()` calls `pawn.workSettings.SetPriority(workDef, targetPriority)` which should persist. `EnableAllWork` in `CorePatches.cs` only runs once per map (guarded by `lastInitMapId`). There's no obvious periodic reset in the mod code.

**Potential causes to investigate:**
- [ ] **RimWorld auto-priority system.** Check if the game's `Pawn_WorkSettings` has an auto-priority mode that overrides manual settings. If `workSettings.Notify_UseWorkPrioritiesChanged()` or `workSettings.EnableAndInitializeIfNotAlreadyInitialized()` is being called somewhere, it could reset to defaults.
- [ ] **Config colonist respawn.** `BrainManager` manages "config colonists" — check if the pawn is being despawned and respawned between cycles (e.g., on brain sync), which would reinitialize workSettings. The log shows `Config colonist 671 died, marked as dead` — verify this is Kena, not Lover.
- [ ] **Save/load cycle.** If the game auto-saves between cycles, workSettings might not be persisting correctly for mod-spawned colonists. Check if the config colonists have proper `ExposeData` serialization.
- [ ] **Harmony patch interference.** The `DisableWorkRestrictionsPatch` patches `CombinedDisabledWorkTags` to return `WorkTags.None`. Verify this doesn't trigger a side effect in `Pawn_WorkSettings` that resets priorities when the disabled work tags change.
- [ ] **Quick diagnostic:** Add temporary logging in `SetWorkPriorityTool` to print current priority before and after set, and add a log line in `EnableAllWork` to confirm it only fires once. Also log whenever `workSettings.SetPriority` is called from any source (Harmony patch on `Pawn_WorkSettings.SetPriority`).

## Agent Goal Feature (2026-06-24, Gemini 3.5 Flash, 1 bot, 2.5 min)

`set_goal` tool tested — working correctly. Bot (Gizmo) used it organically:
- **Iter 7 (cycle 1):** Set goal to "Establish stockpile and haul starting resources to complete Bootstrapping."
- **Iter 10 (cycle 2):** Updated goal to "Build wooden bedroom shelter and a bed to complete Survival Tier 1."
- Goal persisted across cycles and was logged correctly at every iteration.
- Bot's behavior tracked its stated goal well — stockpiled resources, then transitioned to building walls/door/bed.
- No errors from the tool. Goal injection in user messages confirmed working (visible in context each cycle).

## Multi-Bot Issues

- [x] **Bots attempt to build in each other's areas** — Fixed: BuildContext now shows "OTHER COLONISTS' AREAS" section listing each other bot's assets with name and bounding coordinates. Error messages on ownership violations reference this section and name the conflicting owner. Verified: Eadan hit Skater's stockpile once, then successfully placed elsewhere on next attempt with no further retries.
- [x] **System prompt should emphasize independent operation** — Fixed: added INDEPENDENCE section to system prompt explicitly stating each bot manages its own area, builds its own shelter, and should not build for or coordinate with other colonists.
- [ ] **Formal resource sharing between bots** — Currently bots cannot share stockpiles or resources by design. Need a future mechanism for bots to exchange items, share zones, or coordinate building projects.
- [x] **Rapid repeated job failures** — Fixed: zero-cost items (e.g. SleepingSpot) are now spawned directly via ThingMaker instead of going through the blueprint→frame pipeline, which caused endless botch loops since there's no actual construction work to complete.
- [x] **~10% of built structures have no owner** — Fixed in 3c82c90: botched construction (frame reverting to blueprint) now preserves ownership. Verified in multi-bot test logs: all botched constructions show "ownership preserved on new blueprint".
