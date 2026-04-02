# Known Issues & TODO

Observations from automated test runs (Gemini 3 Flash, single bot, 5-minute sessions).

## Bugs to Fix

- [x] **Nickname mismatch** — Fixed: `Brain.PawnLabel` refreshed from live pawn each cycle.
- [x] **Conversation amnesia after trim** — Fixed: `BuildContext()` injects existing infrastructure. Verified: 0 duplicate buildings.
- [x] **"Interaction spot blocked" loop** — Fixed: clearance warning in production tool description + skip reasons in logs. Verified: stove/butcher on 1st try.

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
- [x] **Duplicate production blueprints despite context** — "You already have" only checked completed buildings, not blueprints. Fixed: now checks blueprints/frames too.
