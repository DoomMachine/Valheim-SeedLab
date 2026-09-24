# The specs

Fifty-two source files in this project cite these documents by name and section —
`07-features.md section 2.2`, `01-worldgen-core.md`, `05-validation.md section 1.5`. They were
written into an agent session's scratchpad, which does not survive the session, so they were
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

`scratchpad\…` and `<work>\…` name the working folder these investigations ran in; its files are not
part of this repository, except the studies later copied to `docs\studies\`. "The knowledge base" and
the skills the specs cite are the author's Claude Code skills; a scrubbed snapshot of them is published
in `.claude\` at the repository root.

The game build these specs describe is Valheim 1.0.15, `assembly_valheim.dll`
sha256 `59f53fb5…33adb1` — the same stamp `data\1.0.15-59f53fb5\` and `vseed selftest` check against.

Credits: see the root README - created and tested by DoomMachine; code, tests and docs written by
Claude (Anthropic) in Claude Code.
