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

**Post-build:** The build automatically copies output DLLs to `C:\Program Files (x86)\Steam\steamapps\common\RimWorld\Mods\RimBot\1.5\Assemblies\` via xcopy.

**Debug launch:** Starts `RimWorldWin64.exe` with the `-quicktest` argument for fast iteration.

**No tests or linting** are configured. Testing is manual in-game.

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

- Namespace in code is `RimWorldLLM` (differs from project name `RimBot`)
- Use `Log.Message()` from the `Verse` namespace for logging
- Harmony patches should use `[HarmonyPatch]` attributes and will be auto-discovered by `PatchAll()`
- RimWorld APIs come from the `Verse` and `RimWorld` namespaces
