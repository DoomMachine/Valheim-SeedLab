# Claude Code skills and agent for Valheim modding and SeedLab

Credits: see the root README - created and tested by DoomMachine; code, tests and docs written by Claude (Anthropic) in Claude Code.

This folder holds the [Claude Code](https://claude.com/claude-code) skills and the research agent that
SeedLab was built with. They are a **snapshot of the author's working knowledge base**, taken on
2026-09-24, refreshed on 2026-09-25, 2026-09-26 and 2026-09-27, and scrubbed for publication. They were verified
against Valheim 1.0.15 (Steam build 25390630) and re-stamped on 2026-09-26 to:

```
Valheim 1.0.16, network version 40, Steam build 25527674,
assembly_valheim.dll SHA-256 96cfc004f7f4a6f30d070bef39eafd79c466a137121c4665a2f19fb9c15c6127
```

On 1.0.16, everything SeedLab reproduces was re-checked (the code it ports is unchanged, and SeedLab matches
four worlds 1.0.16 created), and so was every line that says "1.0.16". Any other game-code fact was verified
on 1.0.15 only: treat it as a lead to re-check (`skills/valheim-modding/references/environment.md` says what
the stamp covers). After a game update, treat every game-code fact here as a lead to re-check, not an answer.

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

The scripts in `skills/valheim-modding/scripts/` are Windows PowerShell 5.1, apart from two in Python 3. They
find the game themselves - `-ValheimDir` (PowerShell), then the `SEEDLAB_VALHEIM_DIR` environment variable, then the folders above
the script, then the Steam libraries (including every library listed in `libraryfolders.vdf`) - so they
work from a clone anywhere. What each needs:

| Script | Needs |
| --- | --- |
| `check-game-version.ps1` | nothing beyond the game; compares the installed build with the KB-STAMP in `references/environment.md` (exit 0 same build, 2 changed) |
| `api-surface.ps1`, `find-usages.ps1`, `find-key-usage.ps1`, `scan-mod-patches.ps1` | BepInEx installed in the game (they use its `BepInEx\core\Mono.Cecil.dll`) |
| `decompile.ps1` | the .NET SDK and an installed ILSpy (default `%LOCALAPPDATA%\Programs\ILSpy`, or `-ILSpyDir`); builds a small decompiler into `%LOCALAPPDATA%\valheim-modding-tools` on first use |
| `decompile-module.ps1` | the same as `decompile.ps1`; decompiles a whole assembly (usually a plugin) to one `.cs` plus its IL, and builds its own small tool from `decompiler-module\` into the same folder on first use |
| `asmdiff.ps1` | BepInEx installed in the game (Mono.Cecil). By default it compares the client's `assembly_valheim.dll` with the dedicated server's (Steam app 896660, installed beside the game); `-A` / `-B` take any two `Managed` folders |
| `scene-scripts.py` | Python 3 only. By default it compares the client's main scene with the dedicated server's; `--a` / `--b` take any two `_Data` folders |
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
- **Test worlds.** `asdasdasd`, `testworldclaude`, `ClaudeTestWold2` (seed text `ClaudeTest`),
  `Throwaway` (seed text `VRbvYNainE`, the fresh world of dumper run 7), `throwaway` and `test1` are the
  author's throwaway test worlds. Their names are stored inside the game's own save files, and SeedLab's
  gates key on the first four, so they are kept as they are.
- **"The author's working notes"** are reports, raw measurement runs and evidence copies kept on the author's
  machine, outside this repository.
- Some SeedLab state recorded here (whether the dumper is installed, its hashes) describes the
  author's install on the day of the snapshot, not yours.

## What was left out, and why

| Left out | Why |
| --- | --- |
| the `tomtom-wayfinder` skill | it belongs to the TomTom and Wayfinder mods (https://github.com/DoomMachine/Valheim-TomTom-and-Wayfinder), not to SeedLab |
| `valheim-modding/references/installed-mods.md` | a snapshot of the author's personal mod install. Scan your own with `scan-mod-patches.ps1` and `find-key-usage.ps1 -Plugins` |
| `valheim-modding/references/kb-changelog.md` | a session-by-session log of edits to the knowledge base; the verified facts it indexes are in the reference files |
| `valheim-modding/references/publishing.md` | the author's own procedure for publishing to their GitHub account: credential handling, release bookkeeping for their repositories, and pointers to local working files |
| `valheim-modding/references/archive/` | raw review outputs from the TomTom work: session logs that quote decompiled game code at length |
| the `valheim-knowledge-curator`, `valheim-mod-reviewer`, `valheim-release-auditor`, `valheim-claims-skeptic`, `valheim-tripwire-prover`, `seedlab-perf-engineer` and `seedlab-verifier` agents | tied to the author's workspace (its private notes, local folders and standing instructions), to the TomTom mods and to their releases |
| the `workspace-operations` skill and the `workflows/` folder | how the author's sessions share one machine, and multi-agent workflows prepared for the author's own folders |
| `valheim-modding/references/server-side-mods.md` and the scripts `il.ps1`, `pe-info.py`, `server-test.ps1` and `anchored_edit.py` | newer parts of the author's knowledge base, written for the author's mods and their releases and not reviewed for publication; SeedLab does not need them |
| the workspace `CLAUDE.md` | the author's standing instructions for their own game folder |
| `valheim-modding/scripts/github-release.sh` | the author's release tooling for their own GitHub account: it implements the left-out publishing procedure and reads the local credential store |
| the Python bytecode cache (`.pyc`) | build output |

In the copies, personal install details were replaced by the placeholders above or removed: the Steam
account folder and install location, character names, world UIDs, the names of the author's other
programs and play worlds, details of unreleased versions of the author's mods, and the author's local paths. Hardware and the OS version are kept where they label
a measurement.
