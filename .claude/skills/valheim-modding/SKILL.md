---
name: valheim-modding
description: Verified knowledge and tools for modding Valheim (build 1.0.15, the game folder is found automatically) - the game's real API and accessibility, how vanilla systems behave inside (map pins, input, cursor, HUD, console, player lifecycle, multiplayer map sharing), BepInEx and Harmony specifics, scanning the installed mods, the build toolchain, and hard-won pitfalls. Includes a decompiler and Mono.Cecil scripts for reading game code. Use this whenever working on any Valheim mod or BepInEx plugin, reading or patching Valheim game code, choosing a Harmony patch target or a hotkey, debugging a mod from LogOutput.log, or answering any question about how Valheim works internally - even when the user just names a game class like Minimap, Player or ZNet.
---

# Valheim modding

Everything here was verified against the game build recorded in `references/environment.md` — by
decompiling the shipped assemblies, by compiling against them, or by reading the game's own log after a
live session. Training-data knowledge of Valheim is years out of date for this build (Unity 6, the new
Input System, changed signatures); prefer this skill and the tools below over memory.

## Start of any Valheim session

1. Run `scripts/check-game-version.ps1`. Exit 0 means the game code is the build this knowledge was
   verified against. Exit 2 means Valheim updated: treat game-code facts as suspect until re-verified,
   and run each mod's preflight before building anything.
2. Read `references/pitfalls.md` if you have not this session — it is short, and every entry cost real
   time once.
3. For world generation, seeds, biomes or locations, use **valheim-worldgen** — and if the question is
   "what is actually in seed X", the **seedlab** skill answers it offline and exactly (SeedLab, the
   repository these skills ship in, and its `vseed` CLI), which is faster and more precise than
   measuring in game.

## Finding things out — the tools

All scripts are PowerShell 5.1 (run them with the PowerShell tool, or
`powershell -ExecutionPolicy Bypass -File <script>`), locate the game themselves (`-ValheimDir`, then
`$env:SEEDLAB_VALHEIM_DIR`, then the folders above the script, then the Steam libraries), and read the
shipped DLLs directly. Nothing needs installing except ILSpy for the decompiler; the Cecil-based
scripts use BepInEx's own `BepInEx\core\Mono.Cecil.dll`.

| Question | Tool |
| --- | --- |
| What does vanilla actually do in X? | `scripts/decompile.ps1 -Type Minimap -Member AddPin` (whole type: omit -Member; other DLL: -Assembly assembly_utils / BepInEx / a path) |
| Does member X exist, and is it public or PRIVATE? | `scripts/api-surface.ps1 -Type 'Minimap,Minimap/*' -Filter Pin` |
| Who calls / reads / writes X? Where is string S used? | `scripts/find-usages.ps1 -Needle "PinData::m_save"` (add `-Plugins` to include installed mods) |
| Is this hotkey free? | `scripts/find-key-usage.ps1 -Keys "F4,Insert" -Plugins` — vanilla code AND mod configs |
| Who else patches this method? | `scripts/scan-mod-patches.ps1 -Target "TakeInput"` |
| Has the game updated? | `scripts/check-game-version.ps1` |
| Is the knowledge base itself intact? | `python scripts/validate-kb.py` — frontmatter (strict YAML), sizes, paths, encoding, stamp |

Work from decompiled code, not guesses: read the member, then the members it calls, then its callers
(`find-usages.ps1`). The facts that mattered most in this project — pin hit tests skipping `save:false`
pins, the cursor being re-locked every LateUpdate, death destroying the player but not the map — were
all invisible from signatures and obvious from decompiled bodies.

For a larger investigation, delegate to the **valheim-api-investigator** agent.

## Reference files — read the one you need

| File | Read it when |
| --- | --- |
| `references/environment.md` | you need versions, paths, the boot chain, the toolchain, or the KB-STAMP |
| `references/pitfalls.md` | always, once per session; before building, patching, or touching input/UI/pins |
| `references/game-api.md` | you are about to call or patch a game type — signatures and accessibility |
| `references/vanilla-behaviour.md` | you need to know what vanilla does inside: pins, map gestures, map sharing, input gating, cursor, HUD, console, lifecycle, heights, vanilla keys, BepInEx loader |
| `references/game-operations.md` | startup/loading order, death, logout, saving, local files, when instances are null |
| `references/multiplayer.md` | anything networked: ZNet roles, ZDOs, RPCs, map sharing, pings, cheat gating, client vs server |

Which other mods are installed differs per machine: before choosing a patch target or a key, scan your
own install with `scripts/scan-mod-patches.ps1` and `scripts/find-key-usage.ps1 -Plugins`.

## Starting a new mod

Copy `assets/plugin-template/` to a folder of its own (the author uses `<Valheim>\_ModSource\<ModName>\`),
rename `ExampleMod` (csproj, GUID, namespace, `ModAuthor`) and build with `dotnet build` (it finds the
game through `-p:ValheimDir=<Valheim>` or `SEEDLAB_VALHEIM_DIR`). It already has: SDK project
against the game DLLs with deploy-on-build, per-class Harmony patching, fail-safe `Update` with
throttled logging, text-input gating for hotkeys, F4 as the default key (unused by vanilla, but held by
SeedLab's dumper while it is armed - check with `scripts/find-key-usage.ps1 -Plugins` before keeping
it). For a mod with several editions from one source, copy the layout of the public TomTom/Wayfinder
repository instead (https://github.com/DoomMachine/Valheim-TomTom-and-Wayfinder).

Every mod should have: a `preflight.ps1` that checks its Harmony targets and reflected members against
the shipped game (the TomTom repository has one), a hash check of the deployed DLL, and `[BepInProcess("valheim.exe")]`
if it is client-only (a process-name filter, not an OS filter — see environment.md). Before publishing a
build, scan every packaged byte for absolute paths and personal names (pitfalls.md, section 1).

## Keeping this knowledge base true

This skill is meant to grow with the project, and it is only useful while it stays accurate.

- **Record what you verify.** When a session establishes a new fact about the game — from decompiled
  code, a compile, or a live log — add it to the right reference file with its evidence
  (`Type.Member`, "decompiled", "live log 2026-...").
- **Record what went wrong.** A mistake that cost time, or a wrong assumption caught by review, goes in
  `references/pitfalls.md`: what happened, why, what to do instead.
- **Correct, don't append contradictions.** If a fact turns out wrong, fix it in place and say so where
  you fix it — never leave two versions standing.
- **Never record a guess as a fact.** Mark anything unconfirmed as **Unverified:** with the reason.
- **After a game update** (check-game-version exit 2): re-verify facts as you touch them, note what
  changed in `environment.md`, and only then run `check-game-version.ps1 -UpdateStamp`.
- Project-specific history belongs in the project's own skill.
- **After editing any of it, run `python scripts/validate-kb.py`** — a malformed frontmatter or a
  mangled encoding breaks a skill silently.
