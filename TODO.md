# Known Issues & TODO

Observations from automated test runs (Gemini 3 Flash, single bot, 5-minute sessions). These are the consistent patterns where the bot gets stuck.

## Bugs to Fix

- [x] **Nickname mismatch** — Brain stores the pawn's `LabelShort` at creation time, but RimWorld pawns have nicknames that can differ from their initial label. Fixed: `Brain.PawnLabel` is now refreshed from live pawn at the start of each agent cycle. Not yet verified in a run where the name actually changes (no test run triggered it).
- [x] **Conversation amnesia after trim** — When conversation exceeds 40 messages it's trimmed to 24. Fixed: `BuildContext()` now injects "Existing infrastructure" listing what the colony already has. Verified: 0 duplicate buildings in test despite multiple trims.
- [x] **"Interaction spot blocked" loop** — Production buildings need clearance on their interaction side. Fixed: added clearance warning to production tool description + skip reasons now shown in logs. Verified: research bench placed on 4th try (down from 12+), stove/butcher on 1st try.

## Behavioral Improvements

- [ ] **Research never progresses** — All test runs set research to "battery" but it stayed at 0%. The research bench may not be completed, or the colonist never gets assigned to research work. Investigate whether the bench is actually built and whether work priorities are set correctly.
- [x] **PackagedSurvivalMeal defName unknown** — Bots consistently fail to find survival meals because they guess longer names. Fixed: forward-only fuzzy matching in `FindOnMapTool`. Not yet verified in a run where a bot searches for survival meals (no test run triggered it).
- [ ] **Battery always chosen as research** — Every run picks "battery" as the first research project. Could be fine, but might indicate the bot isn't evaluating research options strategically. Consider whether the context should suggest high-impact early-game research.
- [ ] **Incapable colonists can't execute plans** — If a colonist is incapable of Construction, they blueprint buildings that never get built. The bot doesn't detect this. Fix: warn in context if the colonist is incapable of key work types, or check before issuing build orders.
- [ ] **Wildlife name mismatch** — `set_wildlife_operation` fails when bot uses names like "Doe 14041" or "Gazelle 58284" from scan results. The tool expects species names, not individual labels. Either the tool should accept individual labels or scan_area should show the format the tool expects.
- [ ] **"Medical" work type not matched** — Bot says "Medical" but work type is "doctor". Gerund stripping doesn't help here since it's a synonym, not a suffix variant. May need a synonym map.
- [ ] **"HorseshoePin" vs "HorseshoesPin"** — Bot guesses wrong defName for horseshoes. Minor but shows LLM defName guessing is still unreliable for less common items.

## Spatial / Placement Issues

- [ ] **Blueprints not tracked in metrics** — `BuildingCompletePatch` only fires on `Frame.CompleteConstruction`. Blueprint placement is logged by ArchitectBuildTool but not tracked by MetricsTracker or SpatialEvaluator. Duplicate blueprints, outdoor blueprints, and blocked-interaction-spot blueprints go undetected.
- [ ] **Research bench blocks door** — Observed in Sherry run: bot placed research bench in a doorway of its own room, blocking access. The spatial evaluator didn't catch this because it only checks completed buildings, and the bench was never built. Need to evaluate blueprints too.
- [ ] **Duplicate outdoor production blueprints** — Bot places 3+ research bench blueprints outdoors after failing to place inside the room. These are never built and clutter the map. Need to detect and warn about production building blueprints placed outside enclosed rooms.
- [ ] **Colony score misses blueprint problems** — Score of 15 ("no issues") was reported for a colony with blocked doorways and outdoor duplicate blueprints. Score needs to include penalties for problematic blueprints, not just completed buildings.

## Metrics Baselines (Gemini 3 Flash, 1 bot, 5 min)

| Metric | Typical Range |
|---|---|
| Game days reached | 2.5 – 5.0 |
| Agent cycles | 6 – 14 |
| Tool calls | 80 – 370 |
| Tool errors | 0 – 7 |
| Walls completed | 15 – 43 |
| Colony score (Day 1) | 15 |
| Research set | Yes (always battery) |
| Research progress | 0% (always) |
| Stove placed | Yes |
| Butcher table placed | Yes |
| Growing zone | Yes (15-36 cells) |
