# The specs

Fifty-two source files in this project cite these documents by name and section —
`07-features.md section 2.2`, `01-worldgen-core.md`, `05-validation.md section 1.5`. They were
written in a temporary working folder that was later cleared, so they were
**copied here on 2026-09-23** to stop those citations dangling. This is a copy, not the working
original: if a spec and the code disagree, the code and the goldens are the evidence.

| file | what it settles |
|---|---|
| `01-worldgen-core.md` | `WorldGenerator` method by method, with the IL evidence and the numerics rules |
| `02-locations.md` | `ZoneSystem` location placement, the filters, the RNG streams, alt biomes |
| `03-unity-natives.md` | `Mathf.PerlinNoise`, `UnityEngine.Random`, `Mathf.FloatToHalf`, libm |
| `04-gamedata-dumper.md` | what the dumper must capture and why nothing else can |
| `05-validation.md` | the oracles, the sweeps, and what counts as passing |
| `06-seed-space.md` | `GetStableHashCode`, its inverse, and the size and shape of the seed space |
| `07-features.md` | every user-facing feature, and the definitions behind every number printed |
| `08-architecture.md` | project layout, the seams, and the rules each project keeps |

## What is *not* here

The specs quote excerpts of the decompiled Valheim source where the port depends on them; the full
decompiled files (`decomp\*.cs`) are not included in this repository. Regenerate one when you need it:

```
tools\decompile.ps1 -Type WorldGenerator
```

`<work>\…` names the working folder these investigations ran in; its files are not part of this
repository, except the studies later copied to `docs\studies\`. "The published skills" ("the KB" in older passages) are
the skills the specs cite; a snapshot of them is published in `.claude\` at the repository root.

The game build these specs describe is Valheim 1.0.15, `assembly_valheim.dll`
sha256 `59f53fb5…33adb1` — the stamp of `data\1.0.15-59f53fb5\`. SeedLab is now verified against
Valheim 1.0.16 (`96cfc004…`, `data\1.0.16-96cfc004\`), which changed none of the game code these specs
describe; where a spec was corrected since, it says so in place.

Credits: see the root README - created and tested by DoomMachine; code, tests and docs written by
Claude (Anthropic) in Claude Code.
