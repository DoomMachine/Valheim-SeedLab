# 08 — Software architecture, numerics discipline, performance, risk and delivery plan

Target: a local, cross-platform reimplementation of Valheim 1.0.15 world generation, usable as
(a) an offline seed inspector (what bobmitch.com/valheim does) and (b) a seed search engine with no
20,000-seed ceiling (what valheim.gaming.tools/seeds does, without its limit).

Evidence rules used here: every factual claim cites either a decompiled member (`Type.Member`), a
knowledge-base section, or a probe that was run on this machine (probe scripts are listed in
Appendix A and live in `scratchpad\probe\`). Anything not settled is marked **Unverified:** with the
reason. A constant guessed wrong here silently corrupts every answer the tool will ever give, so a
plausible number is never written as a fact.

Machine this was measured on: AMD Ryzen 7 9800X3D, 8 cores / 16 threads, 61.6 GB RAM, Windows 10 Pro
19045, .NET SDK 10.0.401, runtime 10.0.12 (probe: `dotnet --list-sdks`, `Win32_Processor`).

---

## 0. The fact that reshapes the whole product: the seed space is 2^32, not 8.5e17

The user's arithmetic is correct. Σ(62^k) for k = 1..10 = **853,058,371,866,181,866** (probe A1;
62 = A–Z a–z 0–9). But that is the number of seed *texts*, and world generation never sees the text:

```csharp
// World..ctor  (decompiled)
m_seedName = seed;
m_seed = ((!(m_seedName == "")) ? m_seedName.GetStableHashCode() : 0);
m_worldGenVersion = 2;
```

For terrain and biomes `WorldGenerator` reads only `m_world.m_seed` (int32), `m_world.m_worldGenVersion`
and `m_world.m_menu` (`WorldGenerator..ctor`, `VersionSetup`; valheim-worldgen/world-generator.md §3
and §9). *Correction (reviewer): it also reads `m_world.m_biomeData` — `GetBiomeSector` (WorldGenerator.cs:847)
returns `BiomeSector.EmptyBlackForest` when it is null, and `GetBiomeHeight` calls it at
WorldGenerator.cs:1023 but never uses the result (dead local). `m_biomeData` is itself built from the
seed by `AltBiomeWorldData.GenerateBiomePoints`, so the conclusion is unchanged; the word "only" was
not.* Nothing else about the text reaches generation. Therefore:

- **The complete space of distinct Valheim worlds is 2^32 = 4,294,967,296.** The 8.5e17 seed-text
  space collapses by a factor of ~198.6 million: on average 198,618,130 different seed texts produce
  the identical world (re-derived by the reviewer: 853,058,371,866,181,866 / 2^32 = 198,618,129.80).
  **Unverified:** that *every* int32 is reachable as some `GetStableHashCode` — 207/207 sampled
  targets were solved and the expected preimage count per target is ~987, so a gap would be
  extraordinary, but surjectivity was not established exhaustively (the sumset check is 4.2e12 pairs).
  Consequence for the tool: it must re-hash every seed text it emits and fall back to a longer text
  rather than assume the 8-character inverse always exists.
- Searching seed *texts* is therefore ~8 orders of magnitude of wasted work. **The search engine
  enumerates int32 seeds.** Seed text is an output, not an input.

### 0.1 `GetStableHashCode` is invertible, cheaply — verified by probe

```csharp
// StringExtensionMethods.GetStableHashCode (assembly_utils / decompiled)
int num = 5381, num2 = num;
for (int i = 0; i < str.Length && str[i] != 0; i += 2) {
    num = ((num << 5) + num) ^ str[i];
    if (i == str.Length - 1 || str[i + 1] == '\0') break;
    num2 = ((num2 << 5) + num2) ^ str[i + 1];
}
return num + num2 * 1566083941;      // unchecked int32
```

The two lanes are **independent**: `num` consumes only the even-index characters, `num2` only the
odd-index ones. So a preimage is a meet-in-the-middle, not a brute force. Measured (probe A2/A3, the
alphabet A–Z a–z 0–9):

| lane length | distinct lane values reachable from 5381 | of possible strings |
|---|---|---|
| 1 | 62 | 62 |
| 2 | 2,097 | 3,844 |
| 3 | 66,014 | 238,328 |
| 4 | **2,058,466** | 14,776,336 |

With 4+4 (an 8-character seed text) the expected number of preimages per int32 target is
2,058,466² / 2^32 ≈ **987**. Probe A3 solved **207 of 207** targets (200 random int32 plus 0, ±1,
int.MinValue, int.MaxValue, and the user's own world seed) with zero failures, at 0.4 ms per target
in *Python* — in C# with a prebuilt lane table this is microseconds.

Worked example, cross-checked against real data: the user's world `asdasdasd` has seed text
`MWd8eV6svz`; our hash gives **−1772362158**, which matches the `m_seed` stored in the game's own
minimap meta file (probe A4). The inverter returns `paxd8oI1` for that same int, i.e. an 8-character
alphanumeric seed that produces a bit-identical world.

Consequences for the product:

- "Search all possible Valheim worlds" is a truthful claim, not marketing.
- A search result is reported as `int seed` + a short typeable seed text.
- **Unverified:** whether the new-world UI accepts arbitrary lengths and characters.
  `FejdStartup.OnNewWorldDone` passes `m_newWorldSeed.text` straight to `new World(name, text)` with
  no trimming, filtering or length check (decompiled), but `m_newWorldSeed` is a `GuiInputField`
  whose `characterLimit` is serialized prefab data and cannot be read from code. Settle it by
  dumping the field at runtime (see §2.1); until then the inverter is restricted to ≤10 characters
  from `[A-Za-z0-9]`, which is what the game's own `World.GenerateSeed()` produces (10 chars from a
  59-char alphabet without `o`, `O`, `1`).
- Nothing about the seed *text* is validated on join: a world's identity for generation is
  (m_seed, m_worldGenVersion). Two worlds with different names/texts and the same pair are the same
  terrain (`ZNet.RPC_PeerInfo` sends seed, seedName, uid and worldGenVersion separately).

---

## 1. Solution layout

### 1.1 Constraints that dictate the shape

1. The portable code must never reference a Unity assembly, and `dotnet build` at the repo root must
   succeed on a machine with no Valheim installed (Linux/macOS contributors, CI).
2. The dumper plugin must target `netstandard2.1` and reference the game assemblies with
   `<Private>false</Private>` (valheim-modding/pitfalls.md §3; working example in
   `.claude\skills\valheim-modding\assets\plugin-template\ExampleMod.csproj`).
3. **Two SDK projects may not share a folder** — their `obj\` collide (pitfalls.md §3). One folder
   per project, always.
4. `dotnet new sln` on SDK 10 produces a **`.slnx`**, not `.sln` (environment.md; pitfalls.md §3).
   Visual Studio 18 opens it. Do not fight this; commit the `.slnx`.
5. `MSB3277` (System.* version unification against Unity's assemblies) is expected **in the dumper
   project only**; demote it there with `<MSBuildWarningsAsMessages>MSB3277</MSBuildWarningsAsMessages>`
   and leave it an error everywhere else, so a Unity reference leaking into portable code is loud.
6. No Unity Editor is assumed — the dumper cannot use AssetBundles and must
   serialize everything it needs as plain JSON/binary at runtime.
7. Files are UTF-8 without BOM; in PowerShell 5.1 use `[IO.File]::ReadAllText/WriteAllText` with
   `UTF8Encoding($false)` (pitfalls.md §2). XML comments cannot contain `--` (MSB4025).

### 1.2 Tree

```
valheim-seeker/
  Valheim.Seeker.slnx
  Directory.Build.props            LangVersion=latest, Nullable=enable, TreatWarningsAsErrors=true,
                                   Deterministic=true, InvariantGlobalization=true, no per-project drift
  Directory.Build.targets          the reference guard (see 1.4)
  data/                            dumped asset tables + DATA-STAMP (see §2), committed
  src/
    Valheim.Contracts/             TFMs: netstandard2.0;net10.0  — POCOs shared with the plugin only
    Valheim.WorldGen/              net10.0  the port: WorldGenerator, DUtils, FastNoise, UnityRandom,
                                            Perlin, Heightmap.Biome. BCL only. No I/O. No statics.
    Valheim.WorldGen.Data/         net10.0  loads/embeds data/, exposes the DATA-STAMP + asset tables
    Valheim.Saves/                 net10.0  .fwl2 / .db2 / .chunks / .fch / minimap-cache readers.
                                            READ-ONLY by construction (see R10).
    Valheim.Seeds/                 net10.0  GetStableHashCode, the inverse-hash lane tables, alphabets
    Valheim.Locations/             net10.0  ZoneSystem.GenerateLocations port + AltBiomeWorldData grid
    Valheim.Search/                net10.0  criteria model, planner, parallel executor, checkpointing
    Valheim.Render/                net10.0  PNG/paletted map output (own PNG encoder or ImageSharp)
    Valheim.Cli/                   net10.0  exe; the primary deliverable
    Valheim.Web/                   net10.0  ASP.NET Core minimal API on 127.0.0.1 + static SPA
  tools/
    Valheim.Dumper/                netstandard2.1 BepInEx plugin — the ONLY project touching Unity
    Valheim.Fixtures/              net10.0  turns dumper output into test fixtures / golden vectors
  tests/
    Valheim.WorldGen.Tests/        fast unit + property tests
    Valheim.Acceptance.Tests/      slow, ground-truth (minimap cache, real .db2), [Trait]-gated
    Valheim.Search.Tests/
    Valheim.Arch.Tests/            the reference guard as an executable test
  bench/
    Valheim.Bench/                 BenchmarkDotNet; the perf regression floor
```

### 1.3 Dependency rules

```
Contracts  <- WorldGen? no. Contracts is referenced by Dumper, Fixtures, WorldGen.Data only.
WorldGen         -> (BCL only)
WorldGen.Data    -> WorldGen, Contracts
Saves            -> WorldGen (for Biome enum), BCL
Seeds            -> (BCL only)
Locations        -> WorldGen, WorldGen.Data
Search           -> WorldGen, WorldGen.Data, Locations, Seeds
Render           -> WorldGen, WorldGen.Data
Cli / Web        -> everything above
Dumper           -> Contracts + game assemblies + BepInEx + Harmony   (nothing else in this tree)
```

`Valheim.WorldGen` having **zero** non-BCL references is the load-bearing rule: it is what makes the
port portable, testable and reviewable, and what keeps a Unity type from creeping into a hot path.

### 1.4 How the plugin lives in the same tree without contaminating it

Three mechanisms, all cheap:

1. **Separate solution folder + conditional inclusion.** The dumper is in the `.slnx` but its csproj
   opens with

   ```xml
   <PropertyGroup>
     <ValheimDir Condition="'$(ValheimDir)'==''">E:\SteamLibrary\steamapps\common\Valheim</ValheimDir>
     <ValheimManaged>$(ValheimDir)\valheim_Data\Managed</ValheimManaged>
     <BuildDumper Condition="'$(BuildDumper)'=='' AND Exists('$(ValheimManaged)\assembly_valheim.dll')">true</BuildDumper>
     <BuildDumper Condition="'$(BuildDumper)'==''">false</BuildDumper>
     <TargetFramework Condition="'$(BuildDumper)'=='true'">netstandard2.1</TargetFramework>
   </PropertyGroup>
   ```

   and every `<Reference>`/`<Compile>` group is `Condition="'$(BuildDumper)'=='true'"`. On a machine
   without the game the project builds to nothing instead of failing. Follow the template's
   `<Private>false</Private>` on every game reference so no game DLL is ever copied out of the install.

2. **A shared contracts assembly, multi-targeted.** `Valheim.Contracts` is
   `<TargetFrameworks>netstandard2.0;net10.0</TargetFrameworks>` and contains only DTOs
   (`DumpManifest`, `LocationDef`, `VegetationDef`, `AltBiomeDef`, `NoiseSampleBlock`, `RandomTrace`)
   plus their System.Text.Json source-generated context for net10.0 and hand-rolled writers for
   netstandard2.0 (BepInEx ships Newtonsoft.Json with the game — usable inside the plugin, but keep
   it out of the shared assembly). This is the only code the plugin and the portable tool share.

3. **An executable guard.** `Valheim.Arch.Tests` reflects over
   `typeof(WorldGen).Assembly.GetReferencedAssemblies()` and fails if anything outside a whitelist
   (`System.*`, `netstandard`) appears; it does the same for `Valheim.Saves` plus a check that no
   member of `System.IO.File`/`FileStream` write API is referenced. An architecture rule that is not
   a test is a suggestion (pitfalls.md §1: "Test the tripwire, not just the code").

### 1.5 Cross-platform and publishing

- Everything is `net10.0`; no `#if WINDOWS`; no `System.Drawing`; `Path.Combine` everywhere; no
  registry, no WMI. Save-folder discovery is a per-OS probe list:
  Windows `%USERPROFILE%\AppData\LocalLow\IronGate\Valheim\` (verified on this machine,
  `Utils.GetSaveDataPath`/`app.info`, zones-locations-vegetation.md §10) plus the Steam Cloud path
  `<Steam>\userdata\<id>\892970\remote\worlds`; **Unverified:** the Linux and macOS equivalents
  (`~/.config/unity3d/IronGate/Valheim`, `~/Library/Application Support/IronGate/Valheim` are the
  usual Unity conventions but were not checked against a real install) — ship them as candidates and
  always accept an explicit `--saves-dir`.
- `dotnet publish -r win-x64|linux-x64|osx-arm64 -p:PublishAot=true` for the CLI. NativeAOT forbids
  reflection-heavy JSON, hence the source-generated contexts. **osx-arm64 is the divergence risk**
  (R9): its build only ships if it passes the same digest tests as x64.
- `Valheim.Web` is `dotnet run`-only (no AOT): binds `http://127.0.0.1:<random port>` with a
  per-launch token in the URL, `UseUrls` never `0.0.0.0`, no CORS. It is a local UI, not a service.

---

## 2. Game data: what is dumped, how it is versioned, what happens when the game changes

### 2.1 What cannot be derived from code and must be dumped

Everything below is serialized asset/prefab data. The knowledge base marks all of it **Unverified**
precisely because it is not in the IL.

| Dump item | Source member | Why it is needed |
|---|---|---|
| `ZoneSystem.m_locations` — every `ZoneLocation` field, **in list order** | `ZoneSystem.SetupLocations` result | location prediction is order-sensitive (zones-locations §2.3, §3.2) |
| `ZoneSystem.m_vegetation` | same | vegetation prediction (best-effort only, see R6) |
| `ZoneSystem.m_locationVersion`, `m_waterLevel` | ZoneSystem prefab | regeneration trigger; sea level (code default 30) |
| `AltBiomeList.m_altBiomes` + each `AltBiome` | `AltBiomeList` | whether any alt biomes are enabled at all |
| `Minimap.m_textureSize`, `m_pixelSize`, biome colour fields | Minimap prefab | map rendering + decoding the ground-truth cache |
| `Heightmap.m_width`, `m_scale` on the zone prefab | zone prefab | heightmap-corner blending (M3) |
| `Location.m_exteriorRadius/m_interiorRadius/m_hasInterior`, `DungeonGenerator.m_useCustomInteriorTransform` and generator local offsets | location prefabs | placement filters; dungeon seed offsets |
| `GuiInputField.characterLimit` on `FejdStartup.m_newWorldSeed` | FejdStartup prefab | whether an inverted seed text is typeable (§0.1) |

*Correction (reviewer): the row "Version constants (`Version.World`, network version, `Version.CachedMinimap`)"
was removed from this table — those are `const`/enum members in IL, not serialized asset data, and are
already readable from the decompiled `Version` class: `c_networkVersion = 40u`, `c_WorldVersion =
World.DeepNorth = 41`, `c_WorldGenVersion = 2`, `c_CachedMinimapVersion = CachedMinimap.Original = 1`
(Version.cs). Hard-code them from the decompilation and re-check them with the DATA-STAMP, do not
spend a dumper hook on them.*

Two of these are **now settled by a probe rather than a dump** (see Appendix B): for this build
`Minimap.m_textureSize = 2048` and `m_pixelSize = 12` — not the code defaults 256 / 64. Dump them
anyway; the point of the pipeline is that the next game update re-settles them automatically.

The dumper also emits the **numerics evidence** without which the port cannot be validated at all
(§3.4): Perlin sample blocks, `UnityEngine.Random` traces, per-seed offset traces, and dense
`GetBiome`/`GetHeight` grids as raw float32.

### 2.2 Shipping format

```
data/
  1.0.15-59f53fb5/                 <game version>-<first 8 of assembly_valheim sha256>
    manifest.json                  DATA-STAMP + schema version + per-file sha256
    locations.json                 ordered array, each entry with its GetStableHashCode
    vegetation.json
    altbiomes.json
    prefab-constants.json
    version-constants.json
  goldens/
    perlin-<sha>.bin               10^6..10^7 (in-float32, out-float32) pairs
    random-<sha>.bin               InitState(seed) -> 7 draws, for N seeds, + raw Random.State words
    worldgrid-<seed>-<sha>.bin     float32 GetHeight + byte biome on a declared grid
  selftest-vectors.bin             the compact subset embedded in the binary (§3.5)
```

`manifest.json` carries a stamp in exactly the format `check-game-version.ps1` already uses, so the
tool and the knowledge base can be compared by eye:

```
DATA-STAMP game-version=1.0.15 network=40 steam-build=25390630 \
  assembly_valheim-sha256=59f53fb55d99d22a33e8ed094eec8d21e9f133543bce92bc3d80dce44033adb1 \
  dumped=2026-09-22 dumper=1.0.0 schema=1
```

(The installed build's real values, from `.claude\skills\valheim-modding\references\environment.md`.)

The default data set is embedded as a resource so the tool works with no game installed; `--data-dir`
overrides it, so after a game update the user re-runs the dumper and points the existing binary at
the new folder without waiting for a release.

### 2.3 Staleness detection and the refusal policy

On every run, before any computation:

1. Locate the install (config, `--valheim-dir`, or platform default). If found, SHA-256
   `valheim_Data/Managed/assembly_valheim.dll` — the same artifact `check-game-version.ps1` hashes.
2. Compare with the DATA-STAMP hash.

| state | behaviour |
|---|---|
| hash equal | normal operation |
| hash differs | **terrain output degrades, asset-dependent output refuses.** Print the two stamps and `run tools/Valheim.Dumper to re-dump`. Biome/height still run (they are pure code, ported from IL) but every output is watermarked `data-stale`. Anything reading `locations.json`/`vegetation.json`/prefab constants **exits 2** — the same exit code the KB script uses for "the game changed". |
| no install found | run, but print `assumed data: <stamp>` on every invocation and stamp every artifact; `--assume-data` makes it explicit and non-interactive |
| `manifest.json` file hashes mismatch | hard error, exit 3 (tampered/partial dump) |

Further, the self-test (§3.5) must pass before any result is produced; a game update that changes the
generation *code* is caught there even if the asset tables happen to be unchanged.

**Every artifact the tool writes** (result JSONL, PNG `tEXt` chunk, CSV header, checkpoint file)
embeds `DATA-STAMP`, the tool version/commit, the self-test verdict and the vector-set hash. A seed
list found six months ago must be re-verifiable; an unstamped result is treated as unusable, not as
"probably fine".

Per-world nuance: `m_worldGenVersion` is stored per world in the `.fwl2` and changes the constants
`m_minMountainDistance` / `minDarklandNoise` / `maxMarshDistance` (`WorldGenerator.VersionSetup`:
v0 = 1500/0.5/8000, v1 = 1000/0.5/8000, v2 = 1000/0.4/6000). New worlds are v2. The tool takes it as
an explicit parameter, defaults to 2, prints it in every output, and reads it from a `.fwl2` when one
is supplied. Never silently assume 2 for an old world.

---

## 3. Numerics discipline

This is the part that decides whether the tool is right or merely plausible.

### 3.1 The transcription rules (verified against IL, world-generator.md §9)

- Arithmetic is done in **double**.
- Results are **truncated to float at the end of each statement** (`conv.r4` in the shipped IL —
  checked for `WorldAngle`, `GetBaseHeight`, `GetMeadowsHeight`, `.ctor`; this is not decompiler noise).
- Literals are **float constants widened to double** (`0.002f` = 0.0020000000949949026). Write them
  in the port as `(double)0.002f` or as the exact decimal expansion with the float literal in a
  comment — never as `0.002`.
- Perlin inputs are cast to float: `DUtils.PerlinNoise(double,double)` is
  `Mathf.PerlinNoise((float)x, (float)y)`.
- Specific float-only spots must stay float-only: `GetDeepNorthHeightPregenerate`'s `wx*0.1f`,
  **`wx*0.4f`** (WorldGenerator.cs:1351–1352 — both Perlin calls in that method take float-scaled
  arguments while every other biome uses the double `num2 * 0.4`) and `+0.1f` (:1338);
  `GetDeepNorthHeight`'s `base + 0.1f` (:1359); `GetPlainsHeight`'s `h − 0.15` (:1174); the Mistlands
  (:1140) and `GetMenuHeight` (:1192) `P*P`; `wy + 4000f` inside `CreateDeepNorthGap`/`CreateAshlandsGap`/
  `DeepNorthWaveFade`/`GetAshlandsOceanGradient`; `100000f + m_offset3` in `GetAshlandsHeight` (:1230).
- **`GetBaseHeight`'s 10,490 m edge uses `Utils.LerpStep`, not `DUtils.LerpStep`** — an all-float
  `Clamp01((v−l)/(h−l))` (Utils.cs:311) at WorldGenerator.cs:924, while the 10,000 m lerp one line
  earlier (:919) uses the double `DUtils.LerpStep`. Two identically named helpers, different precision,
  ten lines apart. *(Added by the reviewer; it was missing from this list.)*
- **`DUtils.MathfLikeSmoothStep` returns a float-rounded double**: its body is
  `return (float)(to * t + from * (1.0 - t));` (DUtils.cs:69–74; the IL ends `add; conv.r4; conv.r8; ret`,
  world-generator.md §9, verified 2026-09-22). A port must write `(double)(float)(...)` — writing the
  obvious double expression is wrong. It is used by `CreateAshlandsGap` (:1390), `CreateDeepNorthGap`
  (:1398) and at three call sites inside `GetAshlandsHeight` — :1224, :1238 (inside the 5-iteration
  cellular fBm loop, so it runs 5× per call, 2× when `cheap`) and :1248 — and
  both gap functions additionally cast their clamped input with `(float)` before calling.
  *(Added by the reviewer; it was missing from this list and it moves every Ashlands and Deep North
  coastline.)*
- Associativity is part of the spec, not a detail: Meadows and DeepNorth evaluate
  `(h−0.15) * ((1−k)*0.75)` while Plains evaluates `((h−0.15)*(1−k)) * 0.75` with a *float*
  `h − 0.15` (`GetMeadowsHeight`/`GetPlainsHeight` IL; re-checked by the reviewer against
  WorldGenerator.cs:1107, :1178 and the DeepNorth copy at :1373). A "tidy-up" refactor here is a
  silent bug — which is exactly what the regression digests in §5.4 exist to catch.
- **Unity's `Vector2` is part of the port surface and is not interchangeable with `DUtils`.**
  *(Added by the reviewer — the spec did not mention `Vector2` at all.)*
  - `Vector2.magnitude`/`Distance`/`SqrMagnitude` do `mul; mul; add` on **float32** and only then
    `conv.r8; Math.Sqrt; conv.r4` (Vector2.get_magnitude IL_000d–IL_001d, world-generator.md §9,
    verified 2026-09-22). `DUtils.Length(float,float)` squares and sums in **double** (DUtils.cs:6–9).
    The generator mixes them deliberately: `IsDeepnorth` uses `Vector2.magnitude` (:776) while
    `IsAshlands` uses `DUtils.Length` (:754); `FindLakes` uses `.magnitude` (:290); `MergePoints`,
    `FindClosest`, `IsRiverAllowed` and the river lengths use `Vector2.Distance`; `GetWeight` uses
    `Vector2.SqrMagnitude` (:676). Porting either one as the other silently moves the Deep North ring
    and the lake set.
  - **`Vector2.operator ==` is an epsilon test**, `(dx*dx + dy*dy) < 9.9999994396249292E-11f`
    (Vector2.op_Equality IL_0024–IL_0029, world-generator.md §9). It gates `FindClosest` (:329),
    `FindRandomRiverEnd` (:477) and both `HaveRiver` overloads (:493, :505) — i.e. which lakes may be
    linked. A port that compares floats exactly will occasionally build a different river graph.
  - `Vector2.normalized` returns the zero vector below a 1e-5 magnitude; `(p1 − p0).normalized` is used
    for every river direction (:516, :552).

Enforcement: the port is written as a mechanical transcription with `// IL: conv.r4` markers at each
truncation, an analyzer/Roslyn rule (or a review checklist item) forbidding implicit `float`
arithmetic in `Valheim.WorldGen`, and **no helper extraction** in the height functions. Readability
loses to fidelity in this assembly and nowhere else.

### 3.2 Is .NET 10 float/double behaviour identical to the game's Mono?

Reasoned, not measured: since .NET Core 3.0 RyuJIT is IEEE-754 conformant on x64 — SSE2 registers,
no x87 excess precision, `double`→`float` conversions are never elided, and the JIT does not fuse
`a*b+c` into an FMA on its own. Mono's x64 JIT likewise uses SSE. Both therefore evaluate a float
operation in single precision and a double operation in double precision, which is the property the
transcription rules rely on. **Unverified:** that Unity's Mono does this on every path (the KB flags
it), and it cannot be settled from managed code.

The correct posture is not to argue about it but to measure it once, against the game, and then keep
measuring it in CI. That is what §3.4 and §3.5 are for.

Hard bans in `Valheim.WorldGen`, each enforced by an Arch test:
`Math.FusedMultiplyAdd`, `MathF.FusedMultiplyAdd`, any `System.Runtime.Intrinsics.*` FMA intrinsic,
`float`-typed multi-op expressions outside the enumerated list above, and any `[MethodImpl]` that
changes float behaviour. `<AllowUnsafeBlocks>` is off in this project.

### 3.3 The four things that cannot be verified from managed code

| Unknown | Status | Blocking? |
|---|---|---|
| `Mathf.PerlinNoise` — `[FreeFunction("PerlinNoise::NoiseNormalized")] extern`. Partial evidence: Ken Perlin's reference permutation table (151,160,137,91,90,15,…, 512 int32 = the 256 table twice) exists in `UnityPlayer.dll` at file offset 0x1BE0BC0 on this build (byte search, KB §9). **Unverified:** gradient set, fade curve, output normalisation, and that this table is the one `NoiseNormalized` uses. | must be reverse-engineered and proven bit-exact | **yes — everything** |
| `UnityEngine.Random` — `InitState`, `RandomRangeInt` (behind `Range(int,int)`), `Range(float,float)` and `GetRandomUnitCircle` (behind `insideUnitCircle`) are extern; `Random.State` is four ints (consistent with xorshift128) (UnityEngine.Random, decompiled). **Unverified:** seeding recurrence and the range mapping of both overloads. Note `Range(int,int)` is max-**exclusive** and `Range(float,float)` max-**inclusive** (parameter names in UnityEngine.CoreModule), so `m_riverSeed`/`m_streamSeed` can never be `int.MaxValue`. | must be reverse-engineered and proven bit-exact | **yes — even biomes**, because the 5 offsets and the 2 river/stream seeds come from 7 `Random.Range(int,int)` draws after `InitState(m_seed)` |
| `Mathf.FloatToHalf` — `[FreeFunction(IsThreadSafe = true)] public static extern ushort FloatToHalf(float)` (UnityEngine.Mathf, decompiled). It is what writes `cacheMinimapHeight` (`Utils.FloatsToCompressedHalfBuffer`, Utils.cs:1420–1428). **Unverified:** that it rounds identically to .NET's `(Half)f` (round-to-nearest-even) rather than truncating. | dump ~10^5 (float, half) pairs in M0 | no, but the **M1 acceptance bar in §5.2 is defined in terms of it**, so until it is settled that bar is only as good as the assumption. *(Row added by the reviewer.)* |
| `Math.Sin` / `Atan2` / `Pow` last bit under Mono vs .NET | measure the delta, bound the blast radius | no, but must be quantified |

*Correction (reviewer): "whether `Range(20f,20f)` consumes a draw" was listed here as an open unknown.
It is **already resolved and immaterial** (world-generator.md §9, 2026-09-22): `RenderRivers` is the
last RNG consumer inside `PlaceRivers` and `PlaceStreams`, both of which restore `Random.state`
immediately afterwards (WorldGenerator.cs:448–449 and :371–372), so its draws can never shift a later
draw; and for streams `widthMin == widthMax == 20f`, so the value is 20 either way. `Range(float,float)`
must still be reproduced exactly, because the **river** width draws inside `PlaceRivers`' `RenderRivers`
(:559) set per-point radii and so do change terrain.*

The second row is the one most likely to be underestimated. `GetBiome` needs `m_offset0/1/2/4`, and
those come out of `UnityEngine.Random`; without an exact reproduction there is **no offline biome map
at all**. The "biome-only is easy" intuition is wrong.

Brute-force fallback if `Random` proves irreducible: have the dumper enumerate the four biome offsets
for every seed. They are whole numbers in [−10000, 9999], so 4 × int16 = 8 bytes × 2^32 = **34 GB**,
and the game would have to construct 4.29e9 `WorldGenerator`s to produce it. That is not a product;
it is a reason to treat the `Random` spike as a go/no-go gate (§7, M0).

### 3.4 How the unknowns get settled — dumper-first

The first code written is the dumper, not the port.

1. **Random.** `Random.InitState(s)` then the 7 draws in constructor order
   (`Range(-10000,10000)` ×4 → `m_offset0..3`, `Range(int.MinValue,int.MaxValue)` ×2 → `m_riverSeed`,
   `m_streamSeed`, `Range(-10000,10000)` ×1 → `m_offset4` — note `m_offset4` is drawn **last**, after
   the two river seeds; re-read from WorldGenerator.cs:223–229), plus `Random.state`'s four words after
   `InitState` and after each draw, for 512 seeds spanning 0, ±1, ±2^31, the user's seed and random
   values. Acceptance: a candidate implementation reproduces **512/512 seeds × 7 draws** exactly and
   the four state words at every step.
   Also trace `Range(float,float)` (river widths, stream angles and the ±10,000 m stream start draws:
   WorldGenerator.cs:384, :401–402, :436–437, :559) and — needed for M2, not M1 —
   **`Random.insideUnitCircle`**, which `WorldGenerator.GetTerrainDelta` consumes 10 times per
   candidate location point from the *seeded* per-location stream (WorldGenerator.cs:1427;
   zones-locations-vegetation.md §3.3 filter 6). *(The `insideUnitCircle` requirement was missing;
   added by the reviewer. Without it M2's location prediction cannot be exact, whatever the offsets do.)*
2. **Perlin.** `Mathf.PerlinNoise` over (a) the exact float coordinate pairs the generator actually
   produces for 4 seeds on a 128 m world grid, and (b) a stress grid including negative inputs,
   integer lattice points, values near 2^23, and the `+0.123f/+0.15123f/+0.321f/+0.231f` offsets used
   by the sea-channel term. 10^7 samples, raw float32 in/out. Acceptance: bit-exact on all of them.
3. **libm delta.** `Math.Sin`, `Math.Atan2`, `Math.Pow` over the exact arguments used by `WorldAngle`
   (`Sin((float)((float)Atan2(wx,wy) * 20.0))`), by the Mistlands `M^1.5` and by the Ashlands `^1.4`.
   Compare bitwise with .NET's. If any differ, quantify the blast radius by re-running the
   ground-truth comparison with the dumped values substituted, and report "N of 4,194,304 pixels
   change" rather than reasoning about it.
4. **Ground truth grids.** Raw float32 `GetHeight` and byte `GetBiome` on declared grids for several
   seeds — the exactness companion to the half-precision minimap cache (R11).

### 3.5 Startup self-test, and what happens on divergence

`Valheim.WorldGen` embeds a compact vector set (~200 KB) drawn from the dumps:

- 1,024 Perlin (in,out) pairs including the awkward cases
- the 7 constructor draws for 256 seeds — the **five** offsets `m_offset0..4` plus `m_riverSeed` and
  `m_streamSeed` (*corrected by the reviewer: the original read "the 7 offsets … + the two
  river/stream seeds", which counts nine. `WorldGenerator` has exactly five `m_offsetN` fields,
  WorldGenerator.cs:60–68, and seven ctor draws, :223–229*)
- 4,096 (seed, x, z) → (biome, height float32) tuples, deliberately sampled from biome boundaries,
  the Ashlands/DeepNorth rings, the 10,000/10,490/10,500 m edges, river centre lines, Mistlands
  terraces, and the ±600 m no-mountain zone — i.e. from the discontinuities, not from easy interiors
- 256 `GetStableHashCode` pairs and 256 inverse-hash round trips

It runs at process start in single-digit milliseconds. Comparison is on **bit patterns**
(`BitConverter.SingleToInt32Bits`), never a tolerance: a tolerance hides precisely the failure mode
the test exists for.

**Policy on mismatch: fail closed.** Print the first failing vector (seed, coordinate, expected bits,
actual bits), the vector-set hash, the DATA-STAMP and the RID; refuse to produce any result; exit 3.
A tool that returns slightly-wrong worlds is strictly worse than one that returns nothing, because
the user cannot tell the difference until they have spent an evening walking to a Mistlands that is
not there. `--selftest-only` for CI and for the user to run after a game update.

### 3.6 Runtime knobs

- **Server GC on, concurrent GC off** for the CLI/search (`ServerGarbageCollection=true`,
  `ConcurrentGarbageCollection=false`): many workers, batch workload, no latency requirement. The
  benchmark in Appendix C already ran with server GC enabled. The hot loop should nonetheless be
  allocation-free: structs, `ArrayPool<T>`, per-worker reusable buffers; a gen-0 collection per seed
  is a design failure, not a GC tuning problem.
- **Span/unsafe.** Use `Span<T>`/`ref` locals and `ArrayPool<T>` freely; use `stackalloc` for the
  small per-cell `RiverPoint` arrays. Keep `AllowUnsafeBlocks=false` in `Valheim.WorldGen`; if a
  measured hot spot needs it, it goes in a separate class with a bit-exactness test against the safe
  version.
- **SIMD — probably not, and only under proof.** Measured scalar Perlin here is **12.9 ns/call
  (77.7 M calls/s)**; `Vector<float>.Count` is 8 (AVX2) and hardware-accelerated. The Perlin inner
  loop is dominated by two dependent permutation-table lookups, so a vectorised version needs
  gathers; realistic expectation is 2–4x, not 8x. **Rule: a SIMD path may only produce user-visible
  output if a test proves it bit-identical to the scalar path over ≥10^8 random inputs.** Timebox a
  spike; ship it only at ≥2.5x on the `GetBiome` benchmark; otherwise delete it. Coarse-to-fine
  rejection (§4.3) buys two orders of magnitude and SIMD buys less than one — do the cheap thing first.

---

## 4. Performance architecture

### 4.1 Measured cost model (Appendix C; this machine, 16 threads)

These come from a standalone .NET 10 benchmark using improved Perlin as a **cost** stand-in — it is
not the verified algorithm, so treat the numbers as ±30% on the absolute scale and exact on the
*ratios*, which is what the architecture depends on.

| operation | single thread | note |
|---|---|---|
| Perlin call | 77.7 M/s (12.9 ns) | |
| `GetBaseHeight` (8 Perlin + channels + dist) | 15.6 M/s (64 ns) | |
| `GetBiome` (base + WorldAngle + ≤4 mask Perlin) | 8.8 M/s (113 ns) | |
| `GetBiome`, 16 threads | **69.6 M/s** | 7.9x scaling on 8 cores + SMT |

Derived whole-seed budgets:

| per-seed work | cost | seeds/s (16 threads) | all 2^32 |
|---|---|---|---|
| 100-sample targeted prefilter | 11 µs | 696,000 | **1.7 hours** |
| 1,000-sample prefilter | 113 µs | 69,600 | 17 hours |
| full-world biome scan @ 512 m (1,198 pts) | 0.14 ms | 58,100 | **0.9 days** |
| full-world biome scan @ 256 m (4,794 pts) | 0.54 ms | 14,500 | 3.4 days |
| full-world biome scan @ 128 m (19,175 pts) | 2.2 ms | 3,630 | 13.7 days |
| full-world biome scan @ 64 m (76,699 pts) | 8.7 ms | 907 | 54.8 days |

For scale: valheim.gaming.tools caps a search at 20,000 seeds. At 128 m that is **5.5 seconds** here.
The website's entire search budget is a rounding error; the interesting question is not whether we
beat it but how to spend days of CPU well.

### 4.2 The expensive half: pregeneration

`Pregenerate()` (lakes → rivers → streams) runs in the `WorldGenerator` constructor for every
non-menu world and is needed by **every height query** (most biome height functions call
`AddRivers`), but by **no biome query** (`GetBiome` does not depend on rivers — world-generator.md §9).

Derived estimate (not measured end-to-end; the loop structure is verified, the per-call costs are):

- `FindLakes`: the loop walks a 157×157 grid (`for wy = −10000; wy <= 10000; wy += 128`, twice), but
  `GetBaseHeight` is called only **inside** the circle — the `&&` short-circuits on
  `new Vector2(wx,wy).magnitude > 10000f` first (WorldGenerator.cs:290). Exact count, computed by the
  reviewer over the real float32 grid: **19,175 of 24,649** points (77.8%) reach `GetBaseHeight`, so
  ≈ **1.2 ms** at the measured 64 ns, not 1.6 ms. *(Appendix C's 1.6 ms benchmarked the full square;
  the 19,175 figure is, coincidentally, the same as the 128 m full-world scan in §4.1 — it is the same
  grid.)*
- `MergePoints(800)`: O(n²) over the lake list; n **Unverified** — measure in M1.
- `PlaceRivers`: per candidate link, `IsRiverAllowed` samples the straight line every 128 m.
- `PlaceStreams`: 3,000 attempts, **twice** (`Pregenerate` calls `PlaceStreams(false)` then
  `PlaceStreams(true)`, WorldGenerator.cs:263–264); each attempt does up to 100 start probes and up to
  100 end probes of `GetPregenerationHeight`, plus one centre probe. **A probe is not 120 ns**: it is
  `GetBiome` (measured 113 ns, §4.1) **plus** a biome height function that calls `GetBaseHeight` a
  second time and adds ~6 more Perlin calls — ≈ **250 ns** (WorldGenerator.GetPregenerationHeight →
  GetBiome + GetBiomeHeight, :1011–1016). Worst case 3,000 × 2 × 201 ≈ 1.2 M probes ≈ **300 ms**;
  a realistic 10-probe start gives ≈ **165 ms**. *(Corrected by the reviewer: the original 120 ns/probe
  contradicted this document's own measured 113 ns for `GetBiome` alone.)*
- **Both stream passes re-seed from the same `m_streamSeed`** (`PlaceStreams` opens with
  `InitState(m_streamSeed)` and closes by restoring the saved state, :344–345 and :372), so the second
  pass replays the identical random sequence and diverges only through `riverPreGen`, which changes
  `GetDeepNorthHeightPregenerate`'s `+0.1f`. The second pass's returned list is discarded — only
  `m_streams` from the first pass is kept, while both passes merge their points into `m_riverPoints`
  via `RenderRivers` (:262–264, :371). A port that seeds the second pass from anything else, or that
  keeps the second list, is wrong. *(Added by the reviewer.)*

So **pregeneration is ~150–350 ms per seed (derived, not measured) against 0.14 ms for a coarse biome
scan — a ~1,000–2,500x step**. Fully generating all 2^32 seeds on 16 threads is
2^32 × t / 16: **311 days (0.85 years) at 100 ms**, 155 days at 50 ms, 1.7 years at 200 ms, 2.6 years
at 300 ms. *(Corrected by the reviewer: the original said "~850 years", which is 1000× too large and
contradicted this section's own "160 seeds/s ≈ 13.8 M seeds/day" — 4.295e9 / 13.8e6 = 311 days.)*
The architecture follows directly:

> Broad search is biome-only and never constructs rivers. Heights, rivers, streams and locations are
> a **second stage** applied only to survivors. A criterion that needs heights is quoted to the user
> with its own, much smaller, budget.

At 100 ms/seed and 16 threads the second stage runs ~160 seeds/s ≈ 13.8 M seeds/day; at the corrected
~250 ms/seed it is ~64 seeds/s ≈ 5.5 M seeds/day. Either way the first stage must reject at least
~99.9% for a full sweep to be affordable.

### 4.3 Two-stage funnel and the soundness trap

Each criterion implements:

```csharp
interface ICriterion {
    Stage   Stage { get; }                    // Offsets | BiomeOnly | FullTerrain | Locations
    bool    CoarseReject(in CoarseSample s);  // MUST be sound: never rejects a seed that would pass
    bool    Evaluate(WorldGen gen);           // exact
    CoarseSoundnessSpec Soundness { get; }    // what makes the bound sound, in words + a test
}
```

The planner orders criteria by stage and by measured selectivity, evaluates the cheapest first, and
short-circuits. The danger is entirely on `CoarseReject`: an unsound bound silently drops valid
seeds, and the user never learns what they missed. Mitigations, all mandatory:

- every criterion ships a soundness test asserting `Evaluate ⊆ !CoarseReject` over 10^4 random seeds;
- `--no-prefilter` re-runs a completed search exactly, on the same seed range, and must produce an
  identical result set — run it on a sub-range as a routine audit;
- the result file records which prefilters ran, so a later doubt is answerable.

Coarse sampling must respect the geometry: the wobble term `A = WorldAngle(x,z)*100` gives ±100 m
ripples with 20 lobes (`sin(atan2(x,y)*20)`, so 20 periods around the circle), and the Swamp band is
an **annulus between 2,000 m and 6,000 m** from the origin (8,000 m for `m_worldGenVersion` ≤ 1) whose
interior is carved out by a 0.001-scale Perlin mask, so a "biome exists" criterion sampled at 512 m can
miss thin features. *(Corrected by the reviewer: "2,000–6,000 m wide" described a 4,000 m-wide annulus
as a width. Also note Swamp is the **one band with no wobble** — `GetBiome`'s swamp test is
`num > 2000f && num < maxMarshDistance` with no `A` term (WorldGenerator.cs:812), while Mistlands
(`6000+A…10000`, :816), Plains (`3000+A…8000`, :820), BlackForest (`600+A…6000`, :824) and the
`5000+A` fallback (:828) all carry it.)* State each criterion's *resolution guarantee* explicitly
("detects contiguous regions ≥ 1 km across"); do not let the UI imply exhaustiveness the sampling
does not provide.

### 4.4 State: what must be per-seed, per-worker, or shared

The game's own code leaks state across worlds. These are the exact members; copying any of them as a
static into the port produces answers from the *previous* seed, which is the worst possible bug class
because the output still looks like a valid world.

| member | decl. | what it is | port |
|---|---|---|---|
| `WorldGenerator.s_cachedBiomeAreas` | `private static readonly Dictionary<Vector2s, Heightmap.BiomeArea>` (WorldGenerator.cs:50) | memo for `GetBiomeArea(Vector2s)`; cleared **only** in the ctor (lines 208–209) | per-seed instance field, or omit (pure memo) |
| `WorldGenerator.s_cachedBiomes` | `private static readonly Dictionary<Vector2s, Heightmap.Biome>` (:52) | memo for the private `GetBiome(Vector2s)` used by location placement | per-seed instance field, or omit |
| `WorldGenerator.m_instance` | `private static WorldGenerator` (:54) | the singleton + `instance` getter | **no singleton**: `WorldGen` is an ordinary object, constructed per seed |
| `m_cachedRiverPoints` / `m_cachedRiverGrid` / `m_riverCacheLock` | instance (:82/:84/:86), `ReaderWriterLockSlim` | single-entry river-grid cache; the lock exists only because the game shares one generator between the main thread and `HeightmapBuilder` | per-seed **and** per-worker; **drop the lock**; **replicate the cache exactly, including its staleness — do not "fix" it** (see below) |
| `m_riverPoints` | `Dictionary<Vector2i, RiverPoint[]>` instance | the rendered river/stream point set; the dominant per-seed allocation | per-seed; pool the arrays; **size Unverified — measure in M1** and use it to set worker count |
| `m_noiseGen` | `private static FastNoise` (:90) | **the one static that is safe to share** | one process-wide readonly instance |

**Correction (reviewer) — the river cache must be replicated, not disabled.** The original text said
"do not cache during pregeneration (the KB records a stale-cache edge case there)". That is a
deliberate divergence from the game and would silently produce different worlds. The KB's finding is
the opposite instruction: the single-entry cache (`m_cachedRiverGrid`/`m_cachedRiverPoints`) is *not*
invalidated when `RenderRivers` replaces a cell's `RiverPoint[]` — it always allocates a **new** array
(WorldGenerator.cs:566–576) — so it can return stale weights at exactly two points, the first
`GetRiverWeight` of `PlaceStreams(true)` and the first `GetHeight` after pre-generation finishes, and
only when that query lands in the one cached 64 m cell (≈1e-5 per world). "An external port that wants
bit-exact agreement must replicate the cache, not 'fix' it" (world-generator.md §10, *Stale river
cache*, refined 2026-09-22). Disabling it during pregeneration changes `GetPregenerationHeight` at
those points, which changes stream acceptance, which changes the world. Keep the single-entry cache
with identical fill/replace semantics; the only thing to drop is the `ReaderWriterLockSlim`, because
each worker owns its generator.

Why `m_noiseGen` is safe, verified rather than assumed: `FastNoise..ctor` only stores the seed and
calls `CalculateFractalBounding()` — there is no seed-derived permutation table; seeding happens
inline in `Hash2D(seed,x,y)`; every field assignment in `FastNoise.cs` is in the ctor or a `Set*`
method (lines 730–873), and `GetCellular`/`GetSimplexFractal` assign nothing. Since
`WorldGenerator..ctor` always calls `SetSeed(0)`, the noise is seed-independent and the object is
immutable after configuration, so it can be shared read-only by all workers. The port must reproduce
the exact configuration order, because `SetFractalOctaves(2)` recomputes `m_fractalBounding`:

```
new FastNoise(anything) -> SetNoiseType(Cellular) -> SetCellularDistanceFunction(Euclidean)
 -> SetCellularReturnType(Distance) -> SetFractalOctaves(2) -> SetSeed(0)     // frequency stays 0.01
```

Also shareable read-only: the Perlin permutation table, the location/vegetation tables, and the
inverse-hash lane-4 table (2,058,466 entries ≈ 24 MB as value → packed 4-char string).

A property test enforces all of this: evaluate seed A, then seed B, then seed A again, and require
byte-identical output for A both times — the direct detector for the `s_cachedBiomes` bug class.

### 4.5 Threading and durability

- One worker per logical processor (16 here), each owning a `WorldGen` instance and its pooled
  buffers. No shared mutable state except an atomic block counter and a bounded results channel.
  No `async` in the hot loop; `Parallel.For` only at the block level.
- Seeds are handed out in contiguous blocks (65,536) by `Interlocked.Add`, which keeps the resume
  cursor to one integer and keeps each worker's memory hot.
- Memory per worker: coarse sample buffers (a 512 m scan is ~1,200 floats), the lake list, the river
  point dictionary (measure), the per-seed memo dictionaries. Target **< 64 MB/worker** so 16 workers
  stay under 1 GB — the constraint is not the 61.6 GB of RAM but L2/L3 residency of the Perlin table
  and the hot code.
- **Resumability is a requirement, not a nicety**: a full sweep runs for days. Checkpoint
  `{planId, criteriaHash, dataStamp, toolCommit, nextBlock, resultsOffset, selftestHash}` every 10 s
  and on SIGINT. Resume re-runs the self-test and **refuses** to continue across any stamp, criteria
  or tool-version change (it must not silently stitch two different algorithms' results together).
- Results stream to disk as JSONL; they are never accumulated in memory. A permissive criterion can
  match hundreds of millions of seeds — `--max-results` with an explicit "truncated at N" marker in
  the file, plus a `--count-only` mode that just tallies.
- Progress reporting quotes a completion estimate from a 10,000-seed pilot before the long run
  starts, and refuses to start a >1 h run without `--yes` or an interactive confirmation.

---

## 5. Testing strategy

### 5.1 Unit tests with recorded vectors

Per-function golden vectors from the dumper: `GetBaseHeight`, every `Get*Height`, `WorldAngle`,
`IsAshlands`, `IsDeepnorth`, `CreateAshlandsGap`, `CreateDeepNorthGap`, `AddRivers`, `GetWeight`,
`DUtils.*`, `FastNoise.GetCellular`/`GetSimplexFractal`, `UnityRandom`, `Perlin`. Comparison is on
bit patterns. Sampling is adversarial by construction: biome boundaries, the 0.02 ocean threshold,
`base > 0.4`, the 600/744/1000/2000/3000/5000/6000/8000/10000/10490/10500 m radii, the
12,000 m ± A Ashlands/DeepNorth rings, river centre lines, Mistlands terrace steps.

### 5.2 Ground-truth acceptance (spec 05's corpus, plus a better one found here)

**The single most valuable artifact on this machine is the minimap cache the real game wrote for the
user's world.** Verified by probe (Appendix B), read-only:

- `cacheMinimapMeta` = 8 bytes: `int seed` = **−1772362158** (matches `MWd8eV6svz`), `int version` = 1.
- `cacheMinimapHeight` = GZip of 2048×2048 **float16** `GetBiomeHeight` values
  (`Utils.FloatsToCompressedHalfBuffer`, `Utils.Compress` → `GZipStream`).
- `cacheMinimapBiome`, `cacheMinimapMask` = GZip of 2048×2048 `Color32`.
- The grid is `wx = (j − 1024) * 12 + 6`, `wy = (i − 1024) * 12 + 6` — i.e. `m_textureSize = 2048`,
  `m_pixelSize = 12`, established by finding the exact radius at which the height becomes the
  sentinel −400 (`GetBiomeHeight` returns −400 beyond 10,500 m): **875.000 px inside / 875.001 px
  outside → 10500/875 = 12.0000 exactly**.

That is **4,194,304 (biome, height) ground-truth samples for one real seed**, produced by the game
itself. It samples exactly the `AltBiomeWorldData` grid *coordinates*
(`MapSpaceToWorldSpace(i) = (i−1024)*12 + 6`, AltBiomeWorldData.cs:89–92), so passing it validates the
coordinates that location placement draws candidate zones from — **but not the values stored there
outside the world circle**: `AltBiomeWorldData.GenerateBiomePoints` short-circuits
`vector.sqrMagnitude > 110250000f` to `Biome.Ocean` and height `−1000f` without calling the generator
(AltBiomeWorldData.cs:113–117), while `Minimap.GenerateWorldMap` evaluates `GetBiome`/`GetBiomeHeight`
at every pixel and therefore stores the `−400` sentinel out there (WorldGenerator.cs:1032). Note also
the index order differs — the minimap writes `array[i * 2048 + j]` with `j → wx`
(Minimap.cs:1963–1973), the AltBiome grid writes `PointBiomes[j, i]`. *(Caveat added by the reviewer;
the original claimed the two grids were identical.)*

Acceptance bars:

- **Biome: 100.000%** of 4,194,304 pixels. Not 99.99% — a biome is an integer; any mismatch is a bug.
- **Height:** our float32 rounded through `FloatToHalf` must equal the stored half **bit for bit** on
  ≥ 99.99% of pixels, with every exception enumerated, located and explained. This is still only a
  half-precision check (≈0.05% relative), hence R11 and the raw-float32 dumper grids. **Unverified:**
  that .NET's `(Half)f` rounds like Unity's native `Mathf.FloatToHalf` (§3.3) — until the M0 dump
  settles it, a mismatch at this bar cannot be attributed to the port rather than to the rounding.
- **Reported per biome, never only globally.** 0.01% of the map is 100% of a small Mistlands.
- Repeat for ≥ 2 further seeds whose caches are generated on demand (the user starts a world, the
  game writes the cache, we compare) — one of them chosen for a large Mistlands and Ashlands area.
- Biome colours are prefab data: decode the biome texture through the **dumped palette**
  (`Minimap.GetPixelColor`, Minimap.cs:2092–2105 — `m_meadowsColor`, `m_ashlandsColor`,
  `m_blackforestColor`, `m_deepnorthColor`, `m_heathColor` for **Plains**, `m_swampColor`,
  `m_mountainColor`, `m_mistlandsColor`; `Ocean => Color.white` is the only one in code), never by
  guessing RGB values. Re-verified by the reviewer on the real cache: seed −1772362158 contains
  exactly **7** distinct RGBA values (white 1,651,812 px; (123,32,32) 1,361,539; (51,51,51) 402,651;
  (231,171,120) 327,479; (107,116,63) 307,927; (146,167,92) 80,277; (163,114,88) 62,619). Since the
  palette has nine entries, **this seed exercises only seven of them** — the colour→biome decode must
  come from the dump, the dump must be checked for duplicate colours (a duplicate makes the decode
  ambiguous and the 100.000% bar unverifiable for those biomes), and the acceptance corpus needs a
  seed that shows the remaining two.

Location acceptance uses the other real artifact: the user's `_main.2.db2` contains the actual
`LocationInstance` list, parseable read-only by `SeedLab.Saves` (`vseed world <name> --locations` counts
them). Predicting that world's **placed** instances and diffing against the save (the location gate does
this) is the decisive test that the location port works —
and its failures are informative, because the KB explains exactly which parts are not predictable
(R6). Read-only: never write to `worlds_local\` or the Steam Cloud `remote\worlds\` folders.

### 5.3 Property tests

- `hash(invert(h)) == h` over 10^6 random int32 targets, and for every length the inverter supports.
- `hash(seedText)` equals the `m_seed` stored in every `.fwl2` the tool can find on this machine
  (currently 1 world; grows for free).
- **Seed isolation:** generate A, then B, then A — byte-identical results for A (catches §4.4 leaks).
- **Thread isolation:** the same seed evaluated on 16 threads concurrently gives identical results.
- Range invariants: biome ∈ enum (`Heightmap.Biome` is a flags enum, values 1/2/4/8/0x10/0x20/0x40/
  0x100/0x200 — Heightmap.cs:16–30 — so "is a valid biome" is a set membership test, not a range test);
  `GetBiomeHeight` = −400 exactly beyond 10,500 m (`> 10500f`, so 10,500 m itself is *not* sentinel:
  `return -2f * GetHeightMultiplier()` with `GetHeightMultiplier() = 200f`, WorldGenerator.cs:1032,
  :1450–1453); base height monotone against the documented edge lerps; **rivers only ever lower
  terrain** — `AddRivers` only assigns when `h > target` and lerps toward it (WorldGenerator.cs:937–957).
  *Correction (reviewer): its targets are 0.12–0.14 in normalised units, which is 24–28 m only where the
  height multiplier is the full 200; inside the Ashlands/Deep North gaps `GetBiomeHeight` multiplies by
  `GetHeightMultiplier() * CreateAshlandsGap * CreateDeepNorthGap` (:1021), so assert the normalised
  invariant, not "24–28 m".*
- Determinism of the whole pipeline: a search re-run with the same plan and stamp yields an identical
  result file, byte for byte.
- Parser fuzzing on `.fwl2`/`.db2`/cache readers with truncated and corrupted inputs: a crash is
  acceptable, a wrong seed or a silent partial parse is not.

### 5.4 Regression corpus (the anti-refactor)

64 seeds — 0, −1772362158, ±1, int.MinValue, int.MaxValue, and seeds chosen so that every
biome including Mistlands, Ashlands and DeepNorth is well represented — each reduced to a canonical
digest: SHA-256 over {the 7 constructor draws, lake count, river count, stream count, and biome+height
on a fixed 512 m grid}. *(Two corrections by the reviewer: "the 7 offsets" is five offsets plus two
river/stream seeds, WorldGenerator.cs:223–229; and seed 0 is **not** the menu world — `m_menu` is an
independent `World` flag (`World.GetMenuWorld()` sets it on a world whose empty seed text happens to
give `m_seed = 0`, World.cs:73, :150–156) and it switches the generator onto entirely different code:
`menuTerrain: true`, `GetMenuHeight`, and `Pregenerate()` skipped (WorldGenerator.cs:230–233,
GetBiome :781–788). The corpus needs both an ordinary seed-0 world and a separate menu-world case.)* Digests are committed as text. Any change fails CI and must be explained in the
commit message; `--update-digests` requires a reviewer. This is what stops a "harmless" reassociation
in `GetPlainsHeight` from quietly moving every Plains boundary in every world.

The same digests must match across `win-x64`, `linux-x64` and `osx-arm64` (§3.6) — the cheapest
possible detector for an ARM/FMA divergence, run before either of those builds is published.

### 5.5 Performance regression

BenchmarkDotNet on `Perlin`, `GetBaseHeight`, `GetBiome` and a 1,000-seed prefilter sweep, with a
committed baseline and a CI failure below 70% of it. A 3x slowdown turns a 1.7-hour sweep into a
5-hour one; nobody notices that by feel.

---

## 6. Risk register

Ordered by severity × likelihood. "Early signal" is the thing to watch for *before* the damage.

### R1 — `Mathf.PerlinNoise` cannot be reproduced bit-exactly (severity: fatal · likelihood: medium)
Everything, biome and height alike, is a function of this native routine. The only evidence for its
algorithm is a permutation table found by byte search in `UnityPlayer.dll`; the gradient set, fade
curve and normalisation are unverified.
*Mitigation:* dump 10^7 samples and settle it **before** any other code is written (M0). Candidate
implementations to test in order: improved Perlin with 8-gradient 2D `grad`, classic Perlin,
Unity's known `(x*0.5+0.5)` style normalisation, and float vs double internals.
*If it fails:* the product degrades from "generator" to "viewer of grids the game dumped", which is a
different, much weaker product — decide then, not after 10k lines.
*Early signal:* the M0 Perlin acceptance fails, or matches only in the low bits for some inputs
(worse: a 99.9% match is a trap; require 100%).

### R2 — `UnityEngine.Random` cannot be reproduced bit-exactly (severity: fatal · likelihood: medium)
`InitState` + `Range(int,int)` produce the four biome offsets. Without them there is no offline map
at all, not even biomes — a point that is easy to get wrong when planning ("biomes are the easy
part"). River/stream placement needs `Range(float,float)` too, and thousands of draws where one
wrong draw shifts every later stream.
*Mitigation:* 512-seed trace in M0; the four-word `Random.State` gives strong structural evidence
(xorshift128 family). The precomputed-offset fallback is 34 GB and requires 4.29e9 in-game
constructions — treat it as a non-option and therefore as a go/no-go gate.
*Early signal:* the 7-draw trace fails on any of 512 seeds.

### R3 — The port is subtly wrong in a way that only shows deep in the Mistlands (severity: high · likelihood: medium)
Mistlands uses `M^1.5` (`Math.Pow`), a terracing step `Lerp(h + P*0.002, ceil(h*400)/400, k)` whose
`ceil` turns a last-bit difference into a visible 2.5 mm quantisation boundary, and the mask
`1 − 1.2k − (1 − LerpStep(0.1,0.3,k))`. Ashlands adds `Math.Pow(...,1.4)`, FastNoise cellular fBm and
a lava mask. A global 99.99% pass rate can be 100% failure inside one biome.
*Mitigation:* per-biome acceptance reporting from day one; acceptance seeds deliberately chosen for
large Mistlands/Ashlands coverage; `Math.Pow`/`Sin`/`Atan2` deltas measured against the game (§3.4.3)
rather than assumed.
*Early signal:* the per-biome table shows a non-zero mismatch count in exactly one biome — which is
a structural bug, not noise.

### R4 — The tool spoils worlds the user wanted to play blind (severity: high · likelihood: high if unmanaged)
Not having to look things up is a design goal here: the author's Wayfinder mod was built for exactly
that. A tool that
happily prints "your current world's trader is at (1234, −567)" damages the thing the rest of the
project exists to protect. It is also the one risk where the failure is irreversible: you cannot
un-know a map.
*Mitigation, by design and not by discipline:*
- Default output for a *search* is `seed + seed text + which criteria matched` and nothing else.
- Anything that reveals content of a **named world that exists in the user's save folders** requires
  `--spoil <world>` plus an interactive confirmation; the web UI keeps a "blind list" of world names
  (defaulting to every world found on disk) and refuses to render them.
- A `--blind` global switch, and a config default of blind-on for known worlds.
- Map renders of a *searched* seed are fine (the user has not played it); the gate is about worlds
  they are playing.
*Early signal:* the user says "I didn't want to see that" — too late, so this risk is managed by
prevention only. Review the default of every new command against it.

### R5 — Asset data goes stale after a game update (severity: high · likelihood: high)
Valheim updates; location lists, prefab constants and even biome logic change with new biomes.
*Mitigation:* DATA-STAMP + the refusal policy in §2.3; exit code 2 mirroring `check-game-version.ps1`;
the self-test also catches code changes; every artifact carries the stamp.
*Early signal:* exit 2 / the stale banner. Test the tripwire deliberately (pitfalls.md §1) by
pointing the tool at a doctored hash and confirming it refuses.

### R6 — Location prediction over-promises (severity: medium-high · likelihood: high)
Placement is seed-*deterministic* but not seed-*determined*: it also depends on the exact location
list and order (game version, mods), on `m_worldGenVersion`, on which zones were already generated,
and for `m_unique` locations on **which candidate zone a player reaches first**; rotations are drawn
from the ambient, unseeded `UnityEngine.Random` (zones-locations §3.5, §4). So "where is the trader
in seed X" has no single correct answer for an explored world and only a candidate set for a fresh
one.
*Mitigation:* the tool emits **candidate sets in generation order**, marks unique locations
"first-visited wins", never prints a single coordinate for them, and refuses location output entirely
when the data stamp is stale. Dungeon interiors and vegetation exactness are explicitly out of scope
(dungeons are saved to the generator's ZDO and never regenerated; vegetation acceptance uses physics
raycasts against whatever colliders happen to exist).
*Early signal:* the `asdasdasd` `.db2` diff shows placed instances we did not predict, or vice versa.

### R7 — Search throughput is far below what the user imagines (severity: medium · likelihood: medium-high)
Someone who has used a website that searches 20,000 seeds in a second may expect 4.29e9 in a minute.
Reality (measured, §4.1): a 512 m full-world biome sweep of the entire seed space is ~0.9 days; at
128 m it is ~13.7 days; anything needing rivers/heights for every seed is **most of a year to a few
years** — 311 days at 100 ms/seed, ~1.7 years at the corrected ~250 ms/seed, on 16 threads.
*(Corrected by the reviewer: "centuries" followed from §4.2's "~850 years", which was wrong by 1000×.
The honest number is bad enough to make the same product point without being false.)*
*Mitigation:* quote the budget **before** the run from a 10,000-seed pilot; require confirmation for
long runs; make resumability real; publish the table in the README so expectations are set by numbers
and not by hope. Offer "sample N random seeds" and "search the first N" modes that finish in seconds.
*Early signal:* the pilot estimate; user asking "why is it still going".

### R8 — A coarse prefilter is unsound and silently drops valid seeds (severity: high · likelihood: medium)
The funnel in §4.3 is where the speed comes from and where correctness can be lost invisibly: the
user gets *fewer* results, never a wrong one, so nothing looks broken.
*Mitigation:* mandatory per-criterion soundness tests; `--no-prefilter` audit mode that must
reproduce the result set exactly on a sub-range; the result file records which filters ran.
*Early signal:* the audit run returns extra seeds.

### R9 — Cross-platform / NativeAOT float divergence (severity: high · likelihood: low-medium)
osx-arm64 (and any FMA contraction) can change results. A user on a Mac would get a different world
from the same seed and have no way to know.
*Mitigation:* the digest corpus must match across RIDs before a build is published; FMA intrinsics
banned by an Arch test; the self-test runs on every start on every platform and prints the RID.
*Early signal:* a digest mismatch in CI on the ARM leg.

### R10 — The tool writes to save data (severity: high · likelihood: low)
The saves are Steam Cloud-synced; `worlds_local\<world>\` holds the minimap cache; the game deletes
files it does not expect in a world folder.
*Mitigation:* `Valheim.Saves` opens files `FileAccess.Read, FileShare.ReadWrite` only; an Arch test
fails on any write API reference in that assembly; the CLI refuses an output path under any known
worlds/characters directory.
*Early signal:* the Arch test; a review that spots a `File.WriteAll*` in a parser.

### R11 — Half-precision ground truth mistaken for exactness (severity: medium · likelihood: medium)
The minimap cache is float16 (~3 decimal digits). A port that is wrong by 0.03% passes it everywhere.
*Mitigation:* the dumper also emits raw float32 `GetHeight` grids; the acceptance suite requires both
the broad half-precision comparison and the narrow exact one. Never report "matches the game" on the
strength of the minimap alone.
*Early signal:* the half-precision test passes while the float32 test fails — expected early, and
informative about which term is wrong.

### R12 — Results without provenance (severity: medium · likelihood: medium)
An unstamped seed list found today is worthless after an update; worse, it may be quietly wrong.
*Mitigation:* stamp every artifact (§2.3); a `verify <resultfile>` command that re-checks a sample of
its rows against the current build and says whether the file is still valid.

### R13 — Scope creep into things that are not seed-determined (severity: medium · likelihood: medium)
Dungeon layouts (saved to ZDO, never regenerated), vegetation exactness (physics raycasts), unique
location winners, location rotations, `GetForestFactor` (static, no seed input — the forest pattern
is identical in every world), heightmap corner blending and terrain edits. Each looks like "one more
feature" and each is either impossible or a research project.
*Mitigation:* a written non-goals list in the README, repeated in `--help`.

### R14 — Toolchain friction (severity: low · likelihood: medium)
`.slnx` instead of `.sln`; two projects sharing a folder; MSB3277; `--` inside XML comments;
PowerShell 5.1 mangling UTF-8; C# heredocs in bash breaking on apostrophes. All recorded in
`valheim-modding/references/pitfalls.md` §2 and §3 — read it before fighting the build.

---

## 7. Delivery plan

### M0 — Go/no-go spike (before the architecture is committed to)
**Build:** `Valheim.Dumper` only, plus a throwaway comparison harness.
Dump: 512-seed `UnityEngine.Random` traces (7 draws + state words, plus `Range(float,float)` and
`insideUnitCircle` traces — reviewer); 10^7 `Mathf.PerlinNoise` samples; ~10^5 `Mathf.FloatToHalf`
(float → ushort) pairs covering the height range and the half-ULP midpoints (reviewer);
`Math.Sin/Atan2/Pow` at the exact `WorldAngle`/Mistlands/Ashlands arguments; `GetBiome`/`GetHeight`
float32 grids for 4 seeds; the asset tables of §2.1 (including `GuiInputField.characterLimit`).
**Acceptance (all must hold, no partial credit):**
1. A candidate `UnityEngine.Random` reproduces 512/512 seeds × 7 draws and every state word.
2. A candidate `Mathf.PerlinNoise` reproduces 10^7/10^7 samples bit-exactly.
3. The libm delta is quantified (count of differing results, and the resulting pixel count on the
   4.19 M-pixel grid).
**If 1 or 2 fails, stop.** Everything downstream is worthless without them, and this is two days of
work versus months.

### M1 — "Useful at all"
Offline biome and height for any (seed, worldGenVersion) at any coordinate; rivers/streams/lakes;
PNG map render; seed text ↔ int and the inverse hash; `.fwl2` reader (identify a world, its seed and
gen version); the startup self-test; a CLI with `map`, `at`, `seed`, `selftest`, `verify`.
**Acceptance:** the 4,194,304-pixel minimap comparison for seed −1772362158 — 100.000% biome match;
heights bit-identical after `FloatToHalf` on ≥99.99% with every exception enumerated; a per-biome
breakdown with no biome worse than the global rate; plus the same on one freshly generated seed with
substantial Mistlands and Ashlands; plus the float32 grid comparison from the dumper; plus the
regression digests recorded.

### M2 — "Matches the two websites"
The `AltBiomeWorldData` 12 m grid; `ZoneSystem.GenerateLocations` port with the dumped location
tables; candidate-set location output with the honesty rules of R6; a criteria model and a search
engine over an explicit seed list, a random sample, or a bounded int range; the criteria the existing
sites offer (biome present / near spawn, distances between biomes and between points of interest,
boss-altar candidates, world composition percentages); CSV/JSONL export; spoiler gating.
**Acceptance:** (a) predicted **placed** location instances for the user's world `asdasdasd` match its
real `_main.2.db2` list, with every difference attributable to a documented non-determinism (R6);
(b) map output for 20 seeds matches each one's game-written minimap cache at the M1 bars;
(c) for a criterion both sites support, our result set over a shared 20,000-seed range agrees with
the website's, and any disagreement is explained (not averaged away).

### M3 — "Exceeds them"
Exhaustive int32 search with the two-stage funnel; resumable multi-day runs; inverse-hash output so
every found seed comes with a typeable 8-character text; the criteria language; the local web UI;
`--no-prefilter` audit mode; `verify <resultfile>`; a published budget table.
**Acceptance:** (a) a full 2^32 sweep of a cheap criterion completes, and killing the process
mid-run and resuming produces a result set identical to an uninterrupted run over the same range;
(b) `--no-prefilter` reproduces the funnel's result set exactly on a 10^7-seed sub-range;
(c) **the loop is closed in the real game**: three seeds found by the search, entered by the user as
the inverter's 8-character text, produce worlds whose game-written minimap caches match our
prediction at the M1 bars. Nothing short of (c) justifies telling the user the tool is correct.

**Non-goals, stated up front:** dungeon interiors, exact vegetation, unique-location winners,
location rotations, terrain edits, heightmap-corner blending (M3+ at best), multiplayer, anything
that writes to a save.

---

## Appendix A — probes run for this document

All under `<work>\probe\`.

- **A1** seed-space arithmetic (inline python): Σ 62^k, k=1..10 = 853,058,371,866,181,866 — matches
  the user's figure exactly; ÷ 2^32 = 198,618,129.8 texts per world.
- **A2** `invhash.py` — lane-3 reachability (66,014) and 6-character preimages: 14 of 46 targets had
  no 6-char solution, i.e. 6 characters is *not* enough.
- **A3** `invhash8.py` — lane depth 1..4 reachability (62 / 2,097 / 66,014 / 2,058,466);
  8-character preimages for 207/207 targets, 0 failures, 0.4 ms each in Python; expected ~987
  solutions per target; `−1772362158 → paxd8oI1`, `0 → IaPcIa3t`.
- **A4/B** `minimapcache.py`, `pixelsize.py` — read-only decode of the game-written cache (below).
- **C** `perf\` — .NET 10 throughput benchmark (below).

## Appendix B — new facts settled here (evidence for the knowledge base)

1. **`Minimap.m_textureSize = 2048` and `m_pixelSize = 12` on build 1.0.15** — previously
   "Unverified: serialized prefab values; code defaults 256 / 64" (world-generator.md §8 and its
   Unverified list). Evidence: the game-written cache for world `asdasdasd`
   (`worlds_local\asdasdasd\`) decompresses to exactly 2048×2048 elements in all three buffers
   (biome and mask 4 B `Color32`, height 2 B half), and the −400 sentinel that `GetBiomeHeight`
   returns beyond 10,500 m begins at pixel radius 875.000 → 10500 / 875 = 12.0000 exactly
   (probe `pixelsize.py`, 2026-09-22).
2. **The minimap cache samples the same grid coordinates as `AltBiomeWorldData`**: both are 2048×2048
   at `(i − 1024) * 12 + 6` (`Minimap.GenerateWorldMap` with the values above vs
   `AltBiomeWorldData.MapSpaceToWorldSpace`). The cache is therefore a full dump of
   `GetBiome`/`GetBiomeHeight` on the exact grid location placement samples from. **They are not
   value-identical**: `AltBiomeWorldData.GenerateBiomePoints` stores `Ocean`/`−1000f` for every point
   with `sqrMagnitude > 110250000f` without calling the generator (AltBiomeWorldData.cs:113–117),
   where the cache holds the generator's own `−400` sentinel; and the two use opposite index order
   (`array[i*2048 + j]` vs `PointBiomes[j, i]`). *(Caveat added by the reviewer.)*
3. **`cacheMinimapMeta` layout** (8 bytes): `int32 m_seed`, `int32` cache version. The writer writes
   the literal `1` (`Minimap.SaveMapTextureDataToDisk`, Minimap.cs:2005–2007); the reader casts it to
   `Version.CachedMinimap` and requires `Original` = 1 (`TryLoadMinimapTextureData`, Minimap.cs:779–784),
   and also refuses the cache unless `ZNet.World.m_worldVersion == Version.World.DeepNorth` (=41,
   Minimap.cs:767). Re-verified read-only by the reviewer on the real file: 8 bytes, seed −1772362158
   (= `"MWd8eV6svz".GetStableHashCode()`, recomputed independently), version 1; the three buffers
   decompress to exactly 8,388,608 / 16,777,216 / 16,777,216 bytes = 2048² halves and 2048² `Color32`
   twice; the `−400` sentinel first appears at radius 10500.0103 m (875.00086 px) and the last
   non-sentinel pixel is at 10499.9966 m (874.99971 px), which pins `m_pixelSize` to 12.0000.
   The buffers are GZip (`Utils.Compress` → `GZipStream` at `CompressionLevel.Fastest`, Utils.cs:962–970),
   heights via `Mathf.FloatToHalf` (Utils.cs:1420–1428).
4. **`FastNoise` builds no seed-derived table**: `FastNoise..ctor` stores the seed and calls
   `CalculateFractalBounding()` only; seeding is inline in `Hash2D(seed,x,y)`; all field writes are
   in the ctor and `Set*` methods, and the `Get*` methods write nothing. Hence `SetSeed(0)` fully
   determines the noise (confirming the KB's claim with its mechanism), the object is immutable after
   configuration, and one instance is safely shareable across threads and seeds.
5. **`GetStableHashCode` is invertible in microseconds** and its two lanes are independent; one lane
   reaches 2,058,466 distinct values in 4 characters, so every int32 seed has ~987 eight-character
   alphanumeric preimages (probe A3). Consequence: the practical Valheim seed space is 2^32, not
   8.5e17.
6. **`FejdStartup.OnNewWorldDone` applies no trimming, filtering or length check** to the seed text
   before `new World(name, text)` (decompiled) — the only possible restriction is the
   `GuiInputField.characterLimit` prefab value, which remains **Unverified**.

## Appendix C — measured throughput (this machine, 2026-09-22)

`scratchpad\probe\perf\` — .NET 10.0.401, Release, server GC, `Vector<float>.Count = 8`,
hardware-accelerated. Perlin here is improved-Perlin with Ken Perlin's reference table: a **cost**
model, not the verified algorithm.

```
perlin        :  77.7 M calls/s  (12.9 ns/call)
GetBaseHeight :  15.55 M/s       (64 ns)
GetBiome      :   8.82 M/s       (113 ns)
GetBiome x16  :  69.59 M/s       (scaling 7.9x on 8 cores / 16 threads)

FindLakes pass (157x157 GetBaseHeight)   :   1.6 ms/seed, 1 thread
full-world biome scan @ 512 m (  1,198 pts):  58,065 seeds/s @16T -> all 2^32 in  0.9 days
full-world biome scan @ 256 m (  4,794 pts):  14,516 seeds/s @16T -> all 2^32 in  3.4 days
full-world biome scan @ 128 m ( 19,175 pts):   3,629 seeds/s @16T -> all 2^32 in 13.7 days
full-world biome scan @  64 m ( 76,699 pts):     907 seeds/s @16T -> all 2^32 in 54.8 days
prefilter,    100 samples/seed            : 695,866 seeds/s @16T -> all 2^32 in  1.7 hours
prefilter,  1,000 samples/seed            :  69,587 seeds/s @16T -> all 2^32 in 17.1 hours
prefilter, 10,000 samples/seed            :   6,959 seeds/s @16T -> all 2^32 in  7.1 days
```

Pregeneration (rivers/streams) is **derived, not measured end-to-end**: **~150–350 ms/seed** from the
verified loop structure (`FindLakes` **19,175** base-height samples inside the circle, not 24,649 —
the benchmark row above walked the whole square; `PlaceStreams` 3,000 attempts × two passes × up to
201 height probes at ≈250 ns each, because a `GetPregenerationHeight` probe is a `GetBiome` **plus** a
biome height function) times the measured per-call costs. *(Corrected by the reviewer; the original
read ~50–150 ms/seed at 120 ns/probe, which is less than the measured cost of `GetBiome` alone.)*
Measure it for real in M1 — the whole two-stage design rests on the ratio.

---

## Open questions (added by the reviewer)

These are material, were not settled from the evidence available here, and are not covered by the
spike list in §3.3 / §7.

1. **`Vector2s` was never decompiled.** The file `scratchpad\decomp\Vector2s.cs` contains a
   decompiler error (`type not found: Vector2s`), not source. It is the key type of
   `s_cachedBiomeAreas`/`s_cachedBiomes` and of `ZoneSystem`'s zone identity, and `GetZoneCenter(id) =
   new Vector2s(id.x * 64, id.y * 64)` (ZoneSystem.cs:3091–3094) narrows an `int` product into it.
   Its field width, narrowing behaviour, `operator ==` and `GetHashCode` are therefore **Unverified**.
   Within the legal range (zone indices ≤ 156, centres ≤ 10,048 m) no overflow occurs, so this is
   probably inert — but it must be decompiled before `Valheim.Locations` is written. Use
   `tools\decompile.ps1 -Type Vector2s -Assembly assembly_utils`: it lives in `assembly_utils.dll`,
   beside `Vector2i`, not in `assembly_valheim.dll`.
2. **Does `Mathf.FloatToHalf` round like .NET's `(Half)f`?** See §3.3. It is the definition of the
   M1 height bar and is currently assumed, not measured.
3. **`Random.insideUnitCircle` (native `GetRandomUnitCircle`)** must be reproduced for M2, and its
   relationship to the underlying state (how many draws it consumes, and its mapping) is unknown.
   Nothing in §7's M0 acceptance covers it, so M2 can fail after M0 "passed".
4. **Which two of the nine biome colours are missing from the `asdasdasd` cache**, and whether the
   dumped palette contains any duplicate colour. Until that is checked, "100.000% biome match" is a
   statement about seven biomes, not nine (§5.2).
5. **The per-seed cost of pregeneration** is still derived from per-call costs, now at ~150–350 ms
   (§4.2). The 100 ms figure that the funnel arithmetic is quoted against is the optimistic end of a
   range that was never measured. Measure it in M1 *before* quoting any second-stage budget to the user.
6. **`GetBaseHeight` is called twice per height query** — once by `GetBiome` and again inside the
   biome height function (e.g. `GetMeadowsHeight`, WorldGenerator.cs:1093). The port may memoise it
   per (wx, wy) *only* if the memo is proven to return bit-identical values; the cost model in §4.1
   assumes no such memo.

---

## Verification

Checked by an independent reviewer on 2026-09-22 against
`scratchpad\decomp\` (WorldGenerator, World, StringExtensionMethods, DUtils, FastNoise, Minimap,
AltBiomeWorldData, Heightmap, Utils, UnityEngine.Random, UnityEngine.Mathf, Version, FejdStartup,
ZoneSystem), the knowledge base (`valheim-worldgen/references/world-generator.md` §3/§8/§9/§10,
`zones-locations-vegetation.md` §3.2/§3.3/§3.5/§4/§10, `seeds-and-world-files.md`,
`valheim-modding/references/pitfalls.md` §1/§3, `environment.md`), and two probes run for this review
(`scratchpad\probe\review_check.py`, `scratchpad\probe\minimap_recheck.py` — the latter read-only
against the user's `worlds_local\asdasdasd\` cache; nothing was written to any save location).

**Re-derived and confirmed correct** (independently recomputed, not taken from the text):
`GetStableHashCode` (`"MWd8eV6svz"` → −1772362158, `"paxd8oI1"` → −1772362158, `"IaPcIa3t"` → 0);
Σ 62^k (k=1..10) = 853,058,371,866,181,866 and 198,618,129.80 texts per world; lane reachability
62 / 2,097 / 66,014 / 2,058,466 and 986.57 expected 8-character preimages; the 7 constructor draws and
their order (`m_offset4` last); `VersionSetup` v0/v1/v2 = 1500/0.5/8000, 1000/0.5/8000, 1000/0.4/6000;
every line number cited in §4.4 (:50, :52, :54, :82, :84, :86, :90, ctor clears at :208–209) and the
FastNoise configuration order, `SetFractalOctaves` → `CalculateFractalBounding`, `SetSeed` storing only
the seed, and all field writes confined to lines 730–873; the §3.1 associativity claim for
Meadows/DeepNorth vs Plains; `GetBiomeHeight`'s −400 sentinel and `GetHeightMultiplier() = 200f`;
`GetBiome` not depending on rivers, including in `Minimap.GenerateWorldMap`, which calls it with the
default `waterAlwaysOcean: false`; the minimap grid `(i−1024)*12+6` and the 2048²/half/Color32 layout;
the meta file (seed −1772362158, version 1); the 7 distinct biome colours; `m_pixelSize = 12.0000`
from the sentinel radius (874.99971 px in / 875.00086 px out); `Ocean => Color.white` as the only
in-code colour; the `World.GenerateSeed` 59-character alphabet; `FejdStartup.OnNewWorldDone` applying
no filtering; the DATA-STAMP values against `environment.md`; and every §4.1 point count
(1,198 / 4,794 / 19,175 / 76,699) and throughput derivation.

**Corrected in place** (each with its evidence, marked inline): the "~850 years" full-sweep figure
(§4.2) and "centuries" (R7) — both 1000× too large and contradicted by this document's own
13.8 M seeds/day; the `FindLakes` call count (24,649 → 19,175, `&&` short-circuit at
WorldGenerator.cs:290); the `GetPregenerationHeight` probe cost (120 ns → ≈250 ns) and the resulting
pregeneration range (§4.2, Appendix C); "the 7 offsets" (§3.5, §5.4) → five offsets plus two seeds;
"do not cache during pregeneration" (§4.4) → replicate the stale cache exactly, per
world-generator.md §10; `Range(20f,20f)` listed as open (§3.3) → resolved and immaterial;
the Swamp "band width" and its missing wobble (§4.3); seed 0 described as the menu world (§5.4);
"reads only m_seed/m_worldGenVersion/m_menu" (§0); "the minimap grid is identical to the
`AltBiomeWorldData` grid" (§5.2, Appendix B) → same coordinates, different values outside the circle;
the Version-constants row in §2.1 (they are IL consts, values given); the river-floor invariant in
§5.3 (normalised units, not 24–28 m).

**Added because they were missing and are material**: `DUtils.MathfLikeSmoothStep`'s float-rounded
double return; Unity `Vector2` semantics (`magnitude`/`Distance`/`SqrMagnitude` float chains vs
`DUtils.Length`'s double, the epsilon `operator ==` that decides the river graph, `normalized`'s
1e-5 floor); `Utils.LerpStep` (all-float) at the 10,490 m edge; `GetDeepNorthHeightPregenerate`'s
`wx*0.4f`; `Mathf.FloatToHalf` as a fourth native unknown that the M1 acceptance bar is defined in
terms of; `Random.insideUnitCircle` as a required M2 dump; the two stream passes sharing `m_streamSeed`
and the discarded second list; the biome-colour decode caveat.

**Still unverified after this review**: everything in §3.3 (Perlin, Random, libm, FloatToHalf) —
unchanged, these remain go/no-go; surjectivity of `GetStableHashCode` over int32 (§0);
`GuiInputField.characterLimit`; `m_riverPoints` size; Linux/macOS save paths; .NET-vs-Mono float
agreement; all serialized asset values; `Vector2s` (its decompilation failed — Open questions 1);
and every measured number in Appendix C, which was taken as reported and only checked for internal
consistency and for the arithmetic derived from it.

*Checked by an independent reviewer.*
