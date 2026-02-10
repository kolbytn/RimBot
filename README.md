# RimBot

A RimWorld mod that gives colonists autonomous AI brains powered by large language models. Each colonist observes their surroundings via screenshots, reasons about what to do, and takes actions using in-game tools — building structures, managing work priorities, conducting research, and more.

Supports **Anthropic (Claude)**, **OpenAI (GPT)**, and **Google (Gemini)** as LLM providers.

## Installation

### From GitHub Releases (recommended)

1. Go to the [Releases](../../releases) page
2. Download `RimBot.zip` from the latest release
3. Extract the zip into your RimWorld mods folder:
   - **Windows:** `C:\Program Files (x86)\Steam\steamapps\common\RimWorld\Mods\`
   - **Linux:** `~/.steam/steam/steamapps/common/RimWorld/Mods/`
   - **macOS:** `~/Library/Application Support/Steam/steamapps/common/RimWorld/Mods/`
4. The result should be a `RimBot` folder containing `About/` and `1.6/` subdirectories
5. Enable **RimBot** in the RimWorld mod manager

### From Source

See [Development Setup](#development-setup) below.

## In-Game Usage

### Initial Setup

1. In RimWorld, go to **Options > Mod Settings > RimBot**
2. Enter at least one API key (Anthropic, OpenAI, or Google)
3. **Agent Profiles** will auto-populate with a default profile per configured provider. Each profile specifies a provider + model combination. You can add, remove, or customize profiles.
4. Start a game — colonists are automatically assigned to profiles in round-robin order. The mod spawns one colonist per profile.

### Settings Panel

Open the settings panel from **Options > Mod Settings > RimBot**. Available settings:

| Setting | Default | Description |
|---------|---------|-------------|
| API Keys | — | Enter keys for Anthropic, OpenAI, and/or Google. Only providers with keys will be available for profiles. |
| Agent Profiles | Auto-created | Each profile is a provider + model pair. Click the provider button to switch providers, and the model button to pick a model. Use "X" to remove a profile and "+ Add Profile" to create one. |
| Auto-assign new colonists | On | When enabled, newly arrived colonists are automatically assigned to profiles in round-robin order. Only visible during an active game. |
| Max Tokens | 1024 | Maximum response tokens per LLM call (64–4096). Higher values let the LLM give longer responses but cost more. |
| Thinking Budget | 2048 | Extended thinking token budget (0–8192, increments of 256). Set to 0 to disable extended thinking. Higher values give the LLM more reasoning capacity. |

### Colonist Inspector Tab

Select any colonist and open the **RimBot** tab in the bottom-left inspector panel. It has two sub-tabs:

**Settings sub-tab:**
- **Profile Assignment** — Dropdown to assign this colonist to a specific agent profile, or set to "Unassigned" to disable their brain
- **Brain Status** — Shows current state: None (no profile), Running (LLM call in progress), Idle (waiting for next cycle), or Paused (cooling down after error)
- **Clear Conversation** — Resets the colonist's conversation history and starts fresh

**History sub-tab:**
- Shows a scrollable list of all LLM interactions for this colonist, newest first
- Each entry displays: game time (Day/Hour), mode (Agent), iteration number, provider/model, and success/failure status
- **Click any entry to expand it** and see full details:
  - Token usage (input, output, cache read, reasoning)
  - Screenshot thumbnail (if the turn included a `get_screenshot` call)
  - System prompt (first iteration only)
  - Extended thinking text (toggle visibility with the "Show Thinking" / "Hide Thinking" button)
  - Response text (green = success, red = failure)
  - Tool calls with argument JSON
  - Tool results with success/failure and content
- History updates live as each agent iteration completes — you can watch the colonist think and act in real time

### Viewing Logs

All mod activity is logged with the `[RimBot]` prefix. To view logs:

1. Open RimWorld's dev console (toggle with `` ` `` key if dev mode is enabled), or
2. Read the log file directly at:
   - **Windows:** `%APPDATA%\..\LocalLow\Ludeon Studios\RimWorld by Ludeon Studios\Player.log`
   - **Linux:** `~/.config/unity3d/Ludeon Studios/RimWorld by Ludeon Studios/Player.log`
   - **macOS:** `~/Library/Logs/Unity/Player.log`

Key log patterns:
- `[RimBot] Created brain for <name>` — Colonist detected and brain initialized
- `[RimBot] [AGENT] [<name>] Starting agent loop...` — Agent cycle beginning
- `[RimBot] [AGENT] [<name>] Completed in N iterations` — Agent cycle finished
- `[RimBot] [AGENT] [<name>] Failed:` — Error occurred (rate limit, API issue, etc.)
- `[RimBot] [AGENT] [<name>] Pausing for 30s` — Auto-pause after rate limit error

## How It Works

Each colonist with an assigned profile gets a **Brain** that runs an autonomous agent loop:

1. **Observe** — The brain captures a top-down screenshot of the area around the colonist
2. **Think** — The screenshot and game context are sent to the LLM along with available tools
3. **Act** — The LLM calls tools to inspect cells, place buildings, set work priorities, manage animals, etc.
4. **Repeat** — The agent runs up to 10 tool-use iterations per cycle, then waits ~30 seconds before the next cycle

### Available Tools

The LLM has access to 21 tools across 6 categories:

**Vision & Information**
- `get_screenshot` — Capture a top-down view (configurable radius)
- `inspect_cell` — Examine terrain, buildings, items, and pawns at a cell
- `scan_area` — List all objects in a radius, grouped by category
- `find_on_map` — Search the entire map for a thing by name
- `get_pawn_status` — Check colonist health, needs, skills, and equipment

**Building & Orders**
- `architect_structure/production/furniture/power/security/misc/floors/ship/temperature/joy` — Place blueprints from any architect category
- `architect_orders` — Issue orders: mine, harvest, haul, hunt, tame, deconstruct, etc.
- `architect_zone` — Create stockpiles, growing zones, manage home/roof areas
- `list_buildables` — List available items in a build category with research status

**Work & Schedules**
- `list_work_priorities` / `set_work_priority` — View and set colonist work priorities
- `list_schedule` / `set_schedule` — View and set 24-hour schedules

**Animals**
- `list_animals` — List tamed animals with training and assignment info
- `set_animal_training` / `set_animal_operation` / `set_animal_master` — Manage animal training, operations, and masters

**Wildlife**
- `list_wildlife` — List nearby wild animals
- `set_wildlife_operation` — Designate wildlife for hunting or taming

**Research**
- `list_research` / `set_research` — View available research and set the active project

## Architecture

```
RimBot/
  Main.cs                  Entry point, Harmony patches, tick loop
  Brain.cs                 Per-colonist LLM conversation & agent loop
  BrainManager.cs          Brain lifecycle, colonist sync, capture scheduling
  AgentRunner.cs           Agentic tool-use loop (up to 10 iterations)
  ScreenshotCapture.cs     Off-screen rendering pipeline for map screenshots
  RimBotMod.cs             Mod settings UI (API keys, profiles, config)
  RimBotSettings.cs        Persistent settings storage (Scribe serialization)
  AgentProfile.cs          Provider + model configuration data
  ColonyAssignmentComponent.cs  Pawn-to-profile assignment tracking
  HistoryEntry.cs          Data model for LLM interaction records
  ITab_RimBotHistory.cs    Inspector tab UI for viewing agent history
  Models/
    ILanguageModel.cs      Provider interface (chat, image, tool requests)
    AnthropicModel.cs      Claude API implementation
    OpenAIModel.cs         GPT API implementation
    GoogleModel.cs         Gemini API implementation
    LLMModelFactory.cs     Singleton provider cache
    ChatMessage.cs         Multi-modal message wrapper
    ContentPart.cs         Text, image, tool_use, tool_result, thinking parts
    ModelResponse.cs       Unified response envelope
    LLMProviderType.cs     Provider enum

  Tools/
    ToolTypes.cs           ToolDefinition, ToolCall, ToolResult, ITool interface
    ToolRegistry.cs        Static registry of all tool instances
    GetScreenshotTool.cs   Screenshot capture tool
    InspectCellTool.cs     Cell inspection tool
    ScanAreaTool.cs        Area scanning tool
    FindOnMapTool.cs       Map-wide search tool
    GetPawnStatusTool.cs   Colonist status tool
    ArchitectBuildTool.cs  Category-based building (10 instances)
    ArchitectOrdersTool.cs Work order tool
    ArchitectZoneTool.cs   Zone management tool
    ListBuildablesTool.cs  Buildable item listing
    ListWorkPrioritiesTool.cs / SetWorkPriorityTool.cs
    ListScheduleTool.cs / SetScheduleTool.cs
    ListAnimalsTool.cs / SetAnimalTrainingTool.cs / SetAnimalOperationTool.cs / SetAnimalMasterTool.cs
    ListWildlifeTool.cs / SetWildlifeOperationTool.cs
    ListResearchTool.cs / SetResearchTool.cs

  Libs/
    0Harmony.dll           HarmonyLib for runtime patching
    Newtonsoft.Json.dll    JSON serialization

About/
  About.xml                Mod metadata for RimWorld mod manager
```

### Key Flows

**Startup:** `ModEntryPoint` (static constructor) applies Harmony patches and registers the ITab on all humanlike ThingDefs. `TickManagerPatch` fires every game tick — it drains the main-thread callback queue, syncs brains with colonists, and triggers agent loops.

**Agent Loop:** `BrainManager.Tick()` iterates all brains. Idle/wandering colonists trigger immediately; busy colonists wait 30s between cycles. `Brain.RunAgentLoop()` builds the conversation (system prompt + context), then spawns an async `Task` calling `AgentRunner.RunAgent()`. The agent loop calls the LLM, executes tool calls on the main thread via a `TaskCompletionSource` bridge, and records each turn to history in real-time via callback.

**Screenshot Pipeline:** `ScreenshotCapture.StartBatchCapture()` temporarily takes control of the Unity camera, spoofs the ViewRect so RimWorld regenerates all section meshes, renders each pawn's area to an off-screen `RenderTexture`, and returns base64 PNG strings. Labels are drawn via GL quads using a dynamically created font.

**Provider Abstraction:** `ILanguageModel` defines `SendChatRequest`, `SendToolRequest`, and `SendImageRequest`. Each provider implementation handles API-specific serialization (Anthropic's content blocks, OpenAI's function wrappers, Google's functionDeclarations). Extended thinking maps to each provider's native mechanism. `LLMModelFactory` caches singleton instances.

**Threading Model:** LLM calls run on background threads via `Task.Run`. Tool execution and UI updates must happen on the main thread — `BrainManager.EnqueueMainThread()` adds callbacks to a `ConcurrentQueue<Action>` that `TickManagerPatch` drains each tick.

## Development Setup

### Prerequisites

- [.NET SDK](https://dotnet.microsoft.com/download) (any recent version — builds target .NET Framework 4.7.2)
- [RimWorld](https://store.steampowered.com/app/294100/RimWorld/) (Steam install)
- An IDE: Visual Studio, Rider, or VS Code with C# extension

### Building

```bash
dotnet build RimBot.slnx
```

The post-build step automatically copies the output DLLs to your RimWorld mods folder at:
```
C:\Program Files (x86)\Steam\steamapps\common\RimWorld\Mods\RimBot\1.6\Assemblies\
```

For a custom RimWorld install location, override the managed DLL path:
```bash
dotnet build RimBot.slnx -p:RimWorldManagedPath="D:\Games\RimWorld\RimWorldWin64_Data\Managed"
```

### Quick Testing

Launch RimWorld with `-quicktest` to skip the main menu and load directly into a test map:
```bash
"C:\Program Files (x86)\Steam\steamapps\common\RimWorld\RimWorldWin64.exe" -quicktest
```

### Project Structure

| File | Purpose |
|------|---------|
| `RimBot.slnx` | Solution file |
| `RimBot/RimBot.csproj` | Project targeting .NET Framework 4.7.2 |
| `RimBot/Libs/` | Bundled dependencies (0Harmony.dll, Newtonsoft.Json.dll) |
| `About/About.xml` | RimWorld mod metadata |

### Dependencies

| Assembly | Source | Copied to Output |
|----------|--------|------------------|
| `Assembly-CSharp.dll` | RimWorld install (Managed/) | No |
| `UnityEngine*.dll` | RimWorld install (Managed/) | No |
| `0Harmony.dll` | `RimBot/Libs/` | No (loaded by RimWorld) |
| `Newtonsoft.Json.dll` | `RimBot/Libs/` | Yes |

Game assembly paths are configured via the `RimWorldManagedPath` MSBuild property, defaulting to the standard Windows Steam install location.

### Tips

- All logging uses `Log.Message("[RimBot] ...")` from the `Verse` namespace — filter for `[RimBot]` in `Player.log`
- The Harmony ID is `com.kolbywan.rimbot`
- Harmony patches use `[HarmonyPatch]` attributes and are auto-discovered by `PatchAll()`
- RimWorld APIs come from the `Verse` and `RimWorld` namespaces
- The namespace for all mod code is `RimBot`
- .NET 4.7.2 does **not** support TLS 1.3 — use TLS 1.2 only
- `Camera.Render()` produces black images unless map meshes are regenerated first — see `ScreenshotCapture.cs` for the ViewRect spoofing approach
- Google's function declaration schema requires all enum values to be strings; use `minimum`/`maximum` for integer ranges

## Releases

Releases are automated via GitHub Actions. To create a release:

```bash
git tag v1.0.0
git push origin v1.0.0
```

This triggers a CI build using [Krafs.Rimworld.Ref](https://github.com/krafs/RimRef) reference assemblies and creates a GitHub Release with the packaged mod zip.

## License

This project is not currently licensed for redistribution. All rights reserved.
