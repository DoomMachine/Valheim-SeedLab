# Claude Code skills and agent for Valheim modding and SeedLab

Credits: see the root README - created and tested by DoomMachine; code, tests and docs written by Claude (Anthropic) in Claude Code.

This folder holds the [Claude Code](https://claude.com/claude-code) skills and the research agent that
SeedLab was built with. They are a **snapshot of the author's working knowledge base**, taken on
2026-09-24 and scrubbed for publication. Everything in them was verified against one game build:

```
Valheim 1.0.15, network version 40, Steam build 25390630,
assembly_valheim.dll SHA-256 59f53fb55d99d22a33e8ed094eec8d21e9f133543bce92bc3d80dce44033adb1
```

After a game update, treat every game-code fact here as a lead to re-check, not an answer.

## What is here

| Path | What it is |
| --- | --- |
| `skills/seedlab/` | SeedLab itself: what it proves and which gate proves it, how to run `vseed`, its invariants, the author's decisions about search and output, and the build history with evidence |
| `skills/valheim-worldgen/` | how Valheim turns a seed into a world: the seed hash, biomes, heights, rivers, zones, location placement, vegetation and dungeon seeds, and the byte layout of world and character saves. `scripts/valheim_saves.py` is a read-only seed hasher and save parser |
| `skills/valheim-modding/` | the game's real API and accessibility, vanilla behaviour (pins, input, lifecycle, multiplayer), BepInEx and Harmony specifics, the toolchain, a long list of pitfalls, a BepInEx plugin template, and PowerShell scripts that read the shipped game code |
| `agents/valheim-api-investigator.md` | a read-only research agent that answers "how does the game really do X" from decompiled code and Mono.Cecil scans, with citations |

## How to use them

- **With Claude Code:** open this repository in Claude Code. Project skills in `.claude/skills/` and
  agents in `.claude/agents/` are picked up automatically; a skill loads when a task matches its
  description (seeds, biomes, save files, a game class, a Harmony patch, `vseed` ...).
- **Without it:** they are plain Markdown. Start with a skill's `SKILL.md`; its `references/` hold
  the detail.

The scripts in `skills/valheim-modding/scripts/` are Windows PowerShell 5.1. They find the game
themselves - `-ValheimDir`, then the `SEEDLAB_VALHEIM_DIR` environment variable, then the folders above
the script, then the Steam libraries (including every library listed in `libraryfolders.vdf`) - so they
work from a clone anywhere. What each needs:

| Script | Needs |
| --- | --- |
| `check-game-version.ps1` | nothing beyond the game; compares the installed build with the KB-STAMP in `references/environment.md` (exit 0 same build, 2 changed) |
| `api-surface.ps1`, `find-usages.ps1`, `find-key-usage.ps1`, `scan-mod-patches.ps1` | BepInEx installed in the game (they use its `BepInEx\core\Mono.Cecil.dll`) |
| `decompile.ps1` | the .NET SDK and an installed ILSpy (default `%LOCALAPPDATA%\Programs\ILSpy`, or `-ILSpyDir`); builds a small decompiler into `%LOCALAPPDATA%\valheim-modding-tools` on first use |
| `validate-kb.py` | Python 3; PyYAML for the strict frontmatter check (optional) |

If you only want to use SeedLab, you do not need any of this: the repository's own
`tools\check-game-version.ps1` and `tools\decompile.ps1` are standalone.

## Reading notes

- **"The user"** in these files is the author, DoomMachine, for whom the knowledge base was written.
  **"This machine"** is the author's machine. **"Sessions"**, **"agents"** and **"the reviewer"** are
  Claude Code sessions and subagents doing the work (see the root README's Credits).
- **`scratchpad\...`** paths name a session's temporary working folder. Most of those files were not
  kept; where a study was, it is published under `docs\studies\` and cited there.
- **Placeholders:** `<Valheim>` is the game folder (`<Steam library>\steamapps\common\Valheim`),
  `<Steam>` the Steam install, `<accountId>` a Steam account folder, `<character>` a character file.
  Paths such as `<Valheim>\_ModSource\...` describe the author's layout, where this repository lives
  inside the game folder; a clone can live anywhere.
- **Test worlds.** `asdasdasd`, `testworldclaude` and `test1` are the author's throwaway test
  worlds. Their names are stored inside the game's own save files, and SeedLab's
  gates key on the first two, so they are kept as they are.
- Some SeedLab state recorded here (the dumper being installed, its hashes) describes the author's
  install on the day of the snapshot, not yours.

## What was left out, and why

| Left out | Why |
| --- | --- |
| the `tomtom-wayfinder` skill | it belongs to the TomTom and Wayfinder mods (https://github.com/DoomMachine/Valheim-TomTom-and-Wayfinder), not to SeedLab |
| `valheim-modding/references/installed-mods.md` | a snapshot of the author's personal mod install. Scan your own with `scan-mod-patches.ps1` and `find-key-usage.ps1 -Plugins` |
| `valheim-modding/references/kb-changelog.md` | a session-by-session log of edits to the knowledge base; the verified facts it indexes are in the reference files |
| `valheim-modding/references/archive/` | raw review outputs from the TomTom work: session logs that quote decompiled game code at length |
| the `valheim-knowledge-curator` and `valheim-mod-reviewer` agents | tied to the author's workspace (its private notes and standing instructions) and to the TomTom mods |
| the workspace `CLAUDE.md` | the author's standing instructions for their own game folder |
| the Python bytecode cache (`.pyc`) | build output |

In the copies, personal install details were replaced by the placeholders above or removed: the Steam
account folder and install location, character names, world UIDs, the time zone, the names of other
running programs, and the author's local paths. Hardware and the OS version are kept where they label
a measurement.
