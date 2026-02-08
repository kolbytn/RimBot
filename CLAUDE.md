# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

RimBot is a C# RimWorld mod that integrates LLM capabilities into the game. It targets .NET Framework 4.7.2 (required for RimWorld compatibility) and uses HarmonyLib for runtime patching of game behavior.

## Build and Run

**Solution file:** `RimBot.slnx`

**Build (command line):**
```
msbuild RimBot.slnx
```

**Post-build:** The build automatically copies output DLLs to `C:\Program Files (x86)\Steam\steamapps\common\RimWorld\Mods\RimBot\1.6\Assemblies\` via xcopy.

**Debug launch:** Starts `RimWorldWin64.exe` with the `-quicktest` argument for fast iteration.

**No tests or linting** are configured. Testing is done via automated in-game log capture (see below).

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
   C:/Users/kolby/AppData/LocalLow/Ludeon Studios/RimWorld by Ludeon Studios/Player.log
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
- `[RimBot] [<name>] Screenshot captured, sending to LLM...` — capture pipeline working
- `[RimBot] [<name>] Vision: <text>` — full round-trip success
- `[RimBot] [<name>] Vision error:` / `Vision request failed:` — LLM errors
- `[RimBot] Area capture failed:` — rendering pipeline errors

## Architecture

- **Entry point:** `RimBot/Main.cs` — `ModEntryPoint` is a static class with `[StaticConstructorOnStartup]`, which RimWorld invokes on game load. It initializes HarmonyLib and applies all `[HarmonyPatch]` patches in the assembly.
- **Harmony ID:** `com.yourname.rimworldllm`
- **Output:** Compiled as a DLL library loaded by RimWorld's mod system.

## Dependencies

- **Assembly-CSharp.dll** — RimWorld's main assembly (referenced from Steam install)
- **UnityEngine.dll / UnityEngine.CoreModule.dll** — Unity engine (referenced from Steam install)
- **0Harmony.dll** — HarmonyLib for runtime patching (bundled in `RimBot/Libs/`)
- All game/engine references are `<Private>False</Private>` (not copied to output)

## Key Conventions

- Namespace in code is `RimBot`
- Use `Log.Message()` from the `Verse` namespace for logging
- Harmony patches should use `[HarmonyPatch]` attributes and will be auto-discovered by `PatchAll()`
- RimWorld APIs come from the `Verse` and `RimWorld` namespaces
