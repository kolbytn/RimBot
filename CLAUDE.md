# CLAUDE.md

This file provides guidance to Claude Code when working with code in this repository.

## Project Overview

RimBot is a C# RimWorld mod that gives colonists autonomous AI brains powered by LLMs. Each colonist observes surroundings via screenshots, reasons using tool-calling, and takes actions (building, managing work priorities, research, etc.). It targets .NET Framework 4.7.2 and uses HarmonyLib for runtime patching.

Supports three LLM providers: Anthropic (Claude), OpenAI (GPT), and Google (Gemini).

## Build

**Solution file:** `RimBot.slnx`

```bash
dotnet build RimBot.slnx
```

- Post-build copies DLLs to `C:\Program Files (x86)\Steam\steamapps\common\RimWorld\Mods\RimBot\1.6\Assemblies\` via xcopy (skipped when `CI=true`)
- Game assembly paths use the `$(RimWorldManagedPath)` MSBuild property, defaulting to the standard Windows Steam install
- `msbuild` is not on PATH in this shell; always use `dotnet build`
- .NET 4.7.2 does **not** support TLS 1.3 — use `Tls12` only

## In-Game Log Testing

Use this procedure to verify changes whenever you want to test, or when asked to. Always report back a summarized version of the logs with excerpts where appropriate.

### Step-by-step

1. **Build first:** `dotnet build RimBot.slnx` — confirm 0 errors before launching.
2. **Launch in background:**
   ```
   "C:/Program Files (x86)/Steam/steamapps/common/RimWorld/RimWorldWin64.exe" -quicktest
   ```
   Run this as a background task so you can continue working.
3. **Wait** for the appropriate duration (see timing guide below).
4. **Kill the process:** `taskkill //IM RimWorldWin64.exe //F`
   The background task will report "failed" due to the force-kill — this is expected and normal.
5. **Read the logs:** Filter `[RimBot]` lines from:
   ```
   %APPDATA%\..\LocalLow\Ludeon Studios\RimWorld by Ludeon Studios\Player.log
   ```
6. **Report back** with a summary of what happened: brains created, captures fired, LLM responses received, any errors. Include relevant log excerpts.

### Timing guide

| What you're testing | Wait time | Why |
|---|---|---|
| Mod loads without errors | 60s | Game takes ~30-40s to load into quicktest map |
| Harmony patches apply | 60s | Patches apply during mod init, before map load |
| Brain creation / colonist detection | 75s | Brains are created on first tick after map load |
| Screenshot capture (1 cycle) | 90s | ~40s load + 20s first capture interval + buffer |
| LLM responses (1 cycle) | 120s | Capture + network round-trip to LLM provider |
| Multiple capture cycles | 150s | 2+ full 20s cycles with LLM responses |
| General "does everything work" | 150s | Covers load + 2-3 capture/response cycles |

### What to look for in logs

- `[RimBot] Initialization started.` / `Harmony patches applied.` — mod loaded
- `[RimBot] Created brain for <name>` — colonist detection working
- `[RimBot] [AGENT] [<name>] Starting agent loop...` — agent cycle began
- `[RimBot] [AGENT] [<name>] Completed in N iterations` — agent cycle finished
- `[RimBot] [AGENT] [<name>] Failed:` — agent error (check for rate limits)
- `[RimBot] [<name>] Screenshot captured, sending to LLM...` — vision mode capture
- `[RimBot] [<name>] Vision: <text>` — vision mode full round-trip success
- `[RimBot] Area capture failed:` — rendering pipeline errors

### Adding debug logging

Feel free to add temporary `Log.Message("[RimBot] ...")` statements anywhere in the code to help diagnose issues during testing. Just make sure to remove all temporary logging statements after you're done — only permanent, intentional log lines should be committed.

### Overriding AI config for testing

Mod settings persist across game launches in:
```
%APPDATA%\..\LocalLow\Ludeon Studios\RimWorld by Ludeon Studios\Config\Mod_RimBot_RimBotMod.xml
```

This file stores API keys, max tokens, and serialized profiles. Profile format is pipe-delimited: `id|providerInt|model|thinkingLevel`, separated by `;;`. Provider ints: 0=Anthropic, 1=OpenAI, 2=Google. Thinking levels: 0=None, 1=Low, 2=Medium, 3=High.

To temporarily override config for testing:
- **Edit the XML directly** while RimWorld is not running. Changes take effect on next launch. Useful for swapping models, adjusting thinking levels, or testing with different API keys without navigating the in-game UI.
- **Via in-game UI**: Esc → Options → Mod Settings → RimBot for API keys and max tokens. Per-colonist profile and thinking level are on the colonist's RimBot ITab → Settings sub-tab.
- **To test a specific provider**: remove other API keys from the XML so only the target provider has a key. `AddDefaultProfilesIfEmpty()` auto-creates a profile for each provider with a key on first load.
- **To reset config**: delete the XML file. RimWorld will recreate it with defaults on next launch.

## Architecture

### Entry Point & Tick Loop

- **`Main.cs`** — `ModEntryPoint` (`[StaticConstructorOnStartup]`) initializes Harmony (ID: `com.kolbywan.rimbot`), patches all `[HarmonyPatch]` methods, and registers the ITab inspector on all humanlike ThingDefs.
- **`TickManagerPatch`** — Harmony postfix on `TickManager.DoSingleTick`. Every tick: drains main-thread callback queue, calls `BrainManager.Tick()`. On map load: forces Superfast speed for 2s, unforbids all items, enables all work types.
- **`RunInBackgroundPatch`** — Forces `Application.runInBackground = true`.

### Brain System

- **`BrainManager.cs`** — Static manager. `Tick()` syncs brains with colonists (creates/removes based on profile assignments), triggers agent loops for idle brains. Key constant: 30s agent cooldown. Uses `ConcurrentQueue<Action>` for background-to-main-thread dispatch.
- **`Brain.cs`** — Per-colonist agent. Maintains conversation history (auto-trimmed at 40 messages to 24), up to 50 history entries. `RunAgentLoop()` builds context, spawns async Task to call `AgentRunner.RunAgent()`. Records each turn to history in real-time via callback.
- **`AgentRunner.cs`** — Stateless agentic loop. Calls LLM, executes tool calls on main thread via `TaskCompletionSource` bridge, loops up to 10 iterations. Discards max-token responses with no tool calls to prevent runaway outputs. Fires `onTurnComplete` callback after each iteration.
- **`AgentProfile.cs`** — Data model: GUID + provider + model string + thinking level.
- **`ColonyAssignmentComponent.cs`** — `GameComponent` mapping pawn IDs to profile IDs. Handles round-robin auto-assignment and config-spawned colonist tracking.

### LLM Providers

- **`Models/ILanguageModel.cs`** — Interface: `SendChatRequest`, `SendToolRequest`, `SendImageRequest`, `GetAvailableModels`, `SupportsImageOutput`.
- **`Models/AnthropicModel.cs`** — Claude API. Supports extended thinking via `interleaved-thinking` beta header. Models: `claude-haiku-4-5-20251001`, `claude-sonnet-4-5-20250929`.
- **`Models/OpenAIModel.cs`** — GPT API. Uses Responses API (`/v1/responses`) for tool calls with `reasoning.summary` support; Chat Completions for simple chat. Supports image output. Models: `gpt-5-mini`, `gpt-5.2`.
- **`Models/GoogleModel.cs`** — Gemini API. Uses `functionDeclarations`, `systemInstruction`, `thinkingConfig`. Generates synthetic tool call IDs. Models: `gemini-3-flash-preview`, `gemini-3-pro-preview`.
- **`Models/LLMModelFactory.cs`** — Singleton cache of provider instances.
- **`Models/ChatMessage.cs`** / **`ContentPart.cs`** — Multi-modal message types supporting text, images, tool_use, tool_result, and thinking parts with provider-specific fields (signatures, redacted thinking, thought markers).
- **`Models/ModelResponse.cs`** — Unified response: success/error, content, tokens, tool calls, assistant parts, optional image output.

### Tools (21 total, registered in `ToolRegistry.cs`)

All tools implement `ITool`: `GetDefinition()` returns JSON Schema, `Execute()` takes `ToolCall` + `ToolContext` + callback.

| Category | Tools |
|----------|-------|
| Vision/Info | `get_screenshot`, `inspect_cell`, `scan_area`, `find_on_map`, `get_pawn_status` |
| Building | `architect_structure/production/furniture/power/security/misc/floors/ship/temperature/joy` (10 instances of `ArchitectBuildTool`), `architect_orders`, `architect_zone`, `list_buildables` |
| Work/Schedule | `list_work_priorities`, `set_work_priority`, `list_schedule`, `set_schedule` |
| Animals | `list_animals`, `set_animal_training`, `set_animal_operation`, `set_animal_master` |
| Wildlife | `list_wildlife`, `set_wildlife_operation` |
| Research | `list_research`, `set_research` |

### Screenshot Pipeline

**`ScreenshotCapture.cs`** — Batch captures pawn surroundings as base64 PNG:
1. Disables CameraDriver, spoofs ViewRect to force mesh regeneration for off-screen areas
2. Moves camera to each pawn position, renders to off-screen `RenderTexture`
3. Draws pawn name labels via GL quads
4. Returns base64-encoded PNGs via callback

Key gotcha: `Camera.Render()` produces black images if map section meshes haven't been generated. The ViewRect spoof (`Traverse`) tricks RimWorld into regenerating all sections.

### Settings & UI

- **`RimBotSettings.cs`** — `ModSettings` subclass. Persists API keys, max tokens, and serialized profiles via `Scribe_Values`. Profiles serialized as pipe-delimited strings (`id|provider|model|thinkingLevel`).
- **`RimBotMod.cs`** — Settings UI: API key fields, profile management (add/remove, provider/model dropdowns), auto-assign toggle, max tokens slider.
- **`ITab_RimBotHistory.cs`** — Inspector tab on colonists. Two sub-tabs: History (scrollable entries with screenshots, tool calls, thinking, tokens) and Settings (profile assignment, thinking level dropdown, brain status, clear conversation).
- **`HistoryEntry.cs`** — Data model for each LLM interaction. Lazy-loads `Texture2D` from base64 on first access, with explicit `DisposeTexture()` for GPU memory cleanup.

### Threading Model

LLM network calls run on background threads via `Task.Run`. All Unity/RimWorld API access (tool execution, UI updates, history recording) must happen on the main thread. `BrainManager.EnqueueMainThread()` uses a `ConcurrentQueue<Action>` drained every tick by `TickManagerPatch`.

## Dependencies

| Assembly | Source | Private (copied) |
|----------|--------|-------------------|
| `Assembly-CSharp.dll` | `$(RimWorldManagedPath)` | No |
| `UnityEngine*.dll` (5 modules) | `$(RimWorldManagedPath)` | No |
| `0Harmony.dll` | `RimBot/Libs/` | No |
| `Newtonsoft.Json.dll` | `RimBot/Libs/` | Yes |

## Key Conventions

- Namespace: `RimBot` (tools under `RimBot.Tools`, models under `RimBot.Models`)
- Logging: `Log.Message("[RimBot] ...")`, `Log.Warning(...)`, `Log.Error(...)` from `Verse`
- Harmony patches: `[HarmonyPatch]` attributes, auto-discovered by `PatchAll()`
- RimWorld APIs: `Verse` and `RimWorld` namespaces
- New tools: implement `ITool`, register in `ToolRegistry.Initialize()`
- Provider-specific quirks:
  - Google requires all enum values in function schemas to be strings (not ints)
  - Anthropic thinking signatures must be echoed back in multi-turn conversations
  - Google thought parts must be marked `IsThought = true` to skip in subsequent turns

## CI/CD

GitHub Actions workflow (`.github/workflows/release.yml`) builds on tag push (`v*`) using `Krafs.Rimworld.Ref` NuGet package for reference assemblies and creates a GitHub Release with the packaged mod zip.
