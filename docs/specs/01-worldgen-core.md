# 01 — WorldGenerator core: seed → biome and height (porting specification)

**Target build.** Valheim 1.0.15, network 40, Steam build 25390630,
`assembly_valheim.dll` SHA-256 `59f53fb55d99d22a33e8ed094eec8d21e9f133543bce92bc3d80dce44033adb1`
(`check-game-version.ps1`, run 2026-09-22, exit 0 — identical to the build the knowledge base was
verified against).

**Evidence basis.** Every statement below is taken either from the ILSpy decompilation in
`scratchpad\decomp\` (`WorldGenerator.cs`, `DUtils.cs`, `FastNoise.cs`, `Utils.cs`, `World.cs`,
`Heightmap.cs`, `HeightmapBuilder.cs`, `ZoneSystem.cs`, `Minimap.cs`) or from raw IL read with
Mono.Cecil in this session (`scratchpad\probe\WorldGenerator.il.txt`, `DUtils.il.txt`, `Utils.il.txt`,
`Vector2.il.txt`, `Mathf.il.txt`, `Random.il.txt`; produced by `scratchpad\probe\dump-il.ps1`).
Citations are of the form `(Type.Member / IL IL_xxxx)`. Anything that could not be settled is marked
**Unverified:** with the reason.

`WorldGenerator` is in `assembly_valheim.dll`; `DUtils`, `FastNoise`, `Utils`, `Vector2i`, `Vector2s`
are in `assembly_utils.dll`; `Mathf`, `Vector2`, `Vector3`, `Color`, `Random` are in
`UnityEngine.CoreModule.dll`.

**Two points that were corrected in the knowledge base earlier the same day** (both re-verified against
IL by the reviewer; `valheim-worldgen/references/world-generator.md` already carries them — lines
120–126 for (1) and the numerics bullet for (2), both stamped 2026-09-22, so they are no longer
outstanding corrections *from* this document):

1. The near-spawn mountain cap is **not** `clamp01((h-0.28)/0.1)`. The IL divisor
   is `0.099999994039535522`, i.e. the double widening of the float `0.38f - 0.28f`, **not** `(double)0.1f`
   (`0.10000000149011612`). (`GetBaseHeight` / IL_04f1 `ldc.r8 0.099999994039535522`, `div` at IL_04fa;
   re-read from the IL by the reviewer. `world-generator.md` now carries this at its lines 119–126.)
2. Unity's multi-op float chains (`Vector2.magnitude`, `Distance`,
   `SqrMagnitude`) were listed as **Unverified** in earlier KB revisions. They are settled: the IL performs `mul/mul/add` on float32
   stack values and only then widens for `Math.Sqrt` (`Vector2.get_magnitude` / IL_000d–IL_001d,
   `Vector2.Distance` / IL_001d–IL_002a). A port written with C# `float` locals on .NET reproduces them.
   `Vector2.operator ==` is **not** exact equality (see §6.8).

---

## 0. What the port must expose

```csharp
sealed class WorldGenerator {
    WorldGenerator(int seed, int worldGenVersion = 2, bool menu = false);
    Biome  GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false);
    float  GetHeight(float wx, float wy);                 // world metres, sea level 30
    float  GetHeight(float wx, float wy, out ColorRGBA mask);
    float  GetBiomeHeight(Biome b, float wx, float wy, out ColorRGBA mask,
                          bool preGeneration = false, bool riverPreDN = true);
    float  GetPregenerationHeight(float wx, float wy, bool riverPreGen);
    float  GetAshlandsHeight(float wx, float wy, out ColorRGBA mask, bool cheap = false);
    BiomeArea GetBiomeArea(Vector2s point);               // the overload vanilla uses; Vector2s = two Int16
                                                          // (a private GetBiome(Vector2s) backs it, §5.2)
    // statics: IsAshlands, IsDeepnorth, WorldAngle, CreateDeepNorthGap, DeepNorthWaveFade,
    //          GetAshlandsOceanGradient, GetForestFactor, InForest, GetHeightMultiplier
}
```

Generation reads exactly three inputs: the **int seed**, **`m_worldGenVersion`** and the **menu flag**
(`World.m_seed`, `World.m_worldGenVersion`, `World.m_menu`). The seed *text* is never used by the
generator. (`WorldGenerator..ctor` / IL_00b0–IL_00bb, IL_0090–IL_009a, IL_01ac–IL_01b6.)

Biome enum values (flags), needed because `GetBiomeHeight` switches on them
(`Heightmap.Biome` / decompiled):
`None=0, Meadows=1, Swamp=2, Mountain=4, BlackForest=8, Plains=0x10, AshLands=0x20, DeepNorth=0x40,
Ocean=0x100, Mistlands=0x200, Land=0x27F`.
`Heightmap.BiomeArea`: `Edge=1, Median=2`.

---

## 1. Construction

### 1.1 Order of operations in `WorldGenerator(World world)`

Verified instruction by instruction (`WorldGenerator..ctor` / IL_0000–IL_01c3):

```
// (a) field initialisers, emitted BEFORE the base ctor call and BEFORE VersionSetup:
m_rivers = new List<River>();          // IL_0001
m_streams = new List<River>();         // IL_000c
m_riverPoints = new Dictionary<Vector2i, RiverPoint[]>();          // IL_0017
m_cachedRiverGrid = new Vector2i(-999999, -999999);                // IL_0022
m_riverCacheLock = new ReaderWriterLockSlim();                     // IL_0037
m_biomes = new List<Heightmap.Biome>();                            // IL_0042
maxMarshDistance      = 6000f;         // IL_004d
minDarklandNoise      = 0.4f;          // IL_0058  (ldc.r4 0.40000000596046448)
m_minMountainDistance = 1000f;         // IL_0063
// (b)
m_world = world;                                                   // IL_0075
s_cachedBiomeAreas.Clear(); s_cachedBiomes.Clear();                // IL_007a, IL_0084
m_version = world.m_worldGenVersion;                               // IL_008f
VersionSetup(m_version);                                           // IL_00a6
var savedState = UnityEngine.Random.state;                         // IL_00ab
UnityEngine.Random.InitState(world.m_seed);                        // IL_00bb
if (m_noiseGen == null) {                                          // IL_00c0 static field
    m_noiseGen = new FastNoise(world.m_seed);                      // IL_00d2
    m_noiseGen.SetNoiseType(NoiseType.Cellular);                   // IL_00e2  (ldc.i4.6)
    m_noiseGen.SetCellularDistanceFunction(Euclidean);             // IL_00ed  (ldc.i4.0)
    m_noiseGen.SetCellularReturnType(Distance);                    // IL_00f8  (ldc.i4.2)
    m_noiseGen.SetFractalOctaves(2);                               // IL_0103  (ldc.i4.2)
}
m_noiseGen.SetSeed(0);                                             // IL_010e  ALWAYS, ldc.i4.0
// (c) the seven draws, in this exact order:
m_offset0    = (float)Random.Range(-10000, 10000);   // IL_011e + conv.r4 IL_0123
m_offset1    = (float)Random.Range(-10000, 10000);   // IL_0134 + conv.r4
m_offset2    = (float)Random.Range(-10000, 10000);   // IL_014a + conv.r4
m_offset3    = (float)Random.Range(-10000, 10000);   // IL_0160 + conv.r4
m_riverSeed  =        Random.Range(int.MinValue, int.MaxValue);    // IL_0176, no conv
m_streamSeed =        Random.Range(int.MinValue, int.MaxValue);    // IL_018b, no conv
m_offset4    = (float)Random.Range(-10000, 10000);   // IL_01a0 + conv.r4   <-- 7th, LAST
// (d)
if (!world.m_menu) Pregenerate();                                  // IL_01b6/IL_01b9
UnityEngine.Random.state = savedState;                             // IL_01be
```

All seven draws use the **int** overload `UnityEngine.Random::Range(Int32,Int32)`, whose parameters are
`(minInclusive, maxExclusive)` (verified from the parameter names in `UnityEngine.CoreModule`:
`Range(Int32 minInclusive, Int32 maxExclusive)`; it forwards to the extern `RandomRangeInt`). So
`m_offset0..4 ∈ {-10000 … 9999}` as whole numbers stored in a float (exact), and
`m_riverSeed`/`m_streamSeed` ∈ `{-2147483648 … 2147483646}` — `int.MaxValue` itself can never be drawn.

**Draw-order sensitivity:** any extra `UnityEngine.Random` consumption between `InitState(seed)` and the
7th draw shifts everything after it. The port must draw exactly these seven, in this order, from a
freshly seeded stream.

### 1.2 What each offset feeds

| Field | Consumers |
|---|---|
| `m_offset0` | `GetBaseHeight` X coordinate (both menu and world); Swamp mask noise (X **and** Y) |
| `m_offset1` | `GetBaseHeight` Y coordinate (both); Plains mask noise (X and Y) |
| `m_offset2` | BlackForest mask noise (X and Y) |
| `m_offset3` | every per-biome detail-noise coordinate (both axes), including AshLands and the menu |
| `m_offset4` | Mistlands mask noise (X and Y) |

(`GetBaseHeight` / IL_0026, IL_003c, IL_01eb, IL_0201; `GetBiome` / IL_008d+IL_00a3 (off0),
IL_00e8+IL_0103 (off4), IL_013f+IL_0155 (off1), IL_0192+IL_01a8 (off2); every `Get*Height` /
`ldfld m_offset3`.)

Note the asymmetry the port must keep: `GetBaseHeight` uses **off0 for X and off1 for Y**, whereas every
mask noise and every detail noise uses **one offset for both axes**.

### 1.3 `VersionSetup(int version)`

```
(WorldGenerator.VersionSetup / IL_0016–IL_003f)
if (version <= 0) m_minMountainDistance = 1500f;                 // bgt.s: skip when version > 0
if (version <= 1) { minDarklandNoise = 0.5f; maxMarshDistance = 8000f; }
```

Resulting table (field initialisers supply the v2 values):

| `m_worldGenVersion` | `m_minMountainDistance` | `minDarklandNoise` | `maxMarshDistance` |
|---|---|---|---|
| 0 | 1500 | 0.5 | 8000 |
| 1 | 1000 | 0.5 | 8000 |
| **2** (all new worlds) | **1000** | **0.4** | **6000** |

`World..ctor` sets `m_worldGenVersion = 2` (`World..ctor` / decompiled line 75). Worlds loaded from a
`.fwl2` carry the stored value; it is read only when the file's world version ≥ `Version.World.WorldGenVersion`,
otherwise it stays 0. The port must accept the version as an input, not assume 2.

### 1.4 FastNoise construction and `SetSeed(0)`

`m_noiseGen` is a **private static** field, constructed once per process from the *first* world's seed —
but `SetSeed(0)` runs on **every** `WorldGenerator` construction, before any use. Therefore the cellular
and simplex noise used by AshLands is **seed-independent and identical in every world**. Settings after
construction:

| FastNoise property | Value | Source |
|---|---|---|
| `m_seed` | **0** | `SetSeed(0)` on every ctor |
| `m_frequency` | **0.01** (field default, never set) | `FastNoise` field init |
| `m_interp` | `Quintic` (default, unused by the reachable paths) | field init |
| `m_noiseType` | `Cellular` (set; only matters for `GetNoise`, which is not reachable here) | `SetNoiseType(6)` |
| `m_octaves` | **2** | `SetFractalOctaves(2)` |
| `m_lacunarity` | **2.0** (default) | field init |
| `m_gain` | **0.5** (default) | field init |
| `m_fractalType` | `FBM` (0, default) | field init |
| `m_fractalBounding` | **1/1.5 = 0.6666666666666666** | `CalculateFractalBounding` with octaves 2, gain 0.5 |
| `m_cellularDistanceFunction` | `Euclidean` (0) | `SetCellularDistanceFunction(0)` |
| `m_cellularReturnType` | `Distance` (2) | `SetCellularReturnType(2)` |
| `m_cellularJitter` | **0.45f** (field default) → widened to `0.44999998807907104` at use | field init; `SingleCellular` casts `(double)m_cellularJitter` |

**Correction (reviewer, evidence: `FastNoise.m_cellularJitter` / decompiled line 114 `private float m_cellularJitter = 0.45f;`, and a computed check):** the widened value is
`(double)0.45f = 0.449999988079071044921875`, whose shortest round-trip form is **`0.44999998807907104`**.
The value `0.449999988079071` written in the first draft of this table parses to a *different* double,
one ulp lower (`0.449999988079070989410723768742172978818416595458984375`). Copying that literal into the
port shifts every cellular cell centre by ~1e-16 and can flip an AshLands ridge/lava threshold. Write the
constant as `(double)0.45f` in the port rather than typing digits.

A port should simply construct its FastNoise with seed 0, octaves 2, gain 0.5, lacunarity 2.0,
frequency 0.01, fractal type FBM, cellular Euclidean/Distance — the ctor seed is irrelevant. See §7.2.

### 1.5 Menu world

`World.GetMenuWorld()` = `new World("menu", "") { m_menu = true }` → `m_seed = 0` (empty seed name is
forced to 0 by the `World` ctor), `m_worldGenVersion = 2` (`World.GetMenuWorld`, `World..ctor` /
decompiled). Consequences:

- The seven draws still run, from `InitState(0)`.
- `Pregenerate()` is **skipped** → `m_lakes` is null, `m_rivers`/`m_streams` empty, `m_riverPoints` empty,
  so `AddRivers` is a no-op everywhere.
- `GetBiome` returns only `Mountain` or `BlackForest`; `GetBiomeHeight` uses `GetSnowMountainHeight(menu:true)`
  or `GetMenuHeight`.
- `m_world.m_biomeData` is null → `GetBiomeSector` returns `BiomeSector.EmptyBlackForest`.

The offline tool does not need the menu world, but `GetBaseHeight(menuTerrain:true)` must still exist
because `GetSnowMountainHeight` takes the flag through.

### 1.6 `Pregenerate()`

```
(WorldGenerator.Pregenerate / IL_0000–IL_0027)
FindLakes();
m_rivers  = PlaceRivers();
m_streams = PlaceStreams(isDN: false);
            PlaceStreams(isDN: true);      // IL_0026 pop — return value DISCARDED
```

Everything downstream of construction (heights) depends on the river point set built here. See §4.

---

## 2. Base height, biome, regions and the world edge

### 2.1 `WorldAngle(float wx, float wy)` — static

```csharp
// (WorldGenerator.WorldAngle / IL_0000–IL_001d)  exact IL: conv.r8 x2, Atan2, conv.r4, conv.r8,
//                                                *20.0, conv.r4, conv.r8, Sin, conv.r4
public static float WorldAngle(float wx, float wy) {
    float a = (float)Math.Atan2((double)wx, (double)wy);   // note the argument order: (x, y)
    float b = (float)((double)a * 20.0);
    return (float)Math.Sin((double)b);
}
```

Three float truncations: after `Atan2`, after `*20.0`, after `Sin`. A port that keeps it all in double
will differ near band boundaries. Result ∈ [-1, 1]; the wobble term used everywhere is
`A = WorldAngle(x,z) * 100` → ±100 m, 20 lobes (one every 18°).

**The wobble is truncated in some places and not in others** — this is the single most error-prone
detail in the whole file:

| Site | Wobble expression | Type |
|---|---|---|
| `GetBiome` | `num2 = (float)((double)WorldAngle(wx,wy) * 100.0)` | **float** (IL_0045 `conv.r4`) |
| `IsDeepnorth` | `num = (float)((double)WorldAngle * 100.0)` | **float** (IL_0012 `conv.r4`) |
| `IsAshlands` | `num = (double)WorldAngle * 100.0` | **double**, no `conv.r4` (IL_0011 `mul`, IL_0012 `stloc.0` into a double local) |
| `GetAshlandsHeight` | `num3 = (double)WorldAngle * 100.0` | **double** |
| `CreateAshlandsGap`, `CreateDeepNorthGap`, `DeepNorthWaveFade`, `GetAshlandsOceanGradient` | `(double)WorldAngle * 100.0` | **double** |

### 2.2 `GetBaseHeight(float wx, float wy, bool menuTerrain)` — private

`P(a,b)` = `DUtils.PerlinNoise(double,double)` = `Mathf.PerlinNoise((float)a, (float)b)` (float result).
Every line ends with a `conv.r4`. All literals below are the **exact double values** found in the IL.

```csharp
float num  = 0f;    // IL_0000  additive term, always 0
float num2 = 1f;    // IL_0006  multiplicative term, always 1

// ---------- menuTerrain == true (IL_0012 .. IL_01cd) ----------
double X = (double)wx;  X += 100000.0 + (double)m_offset0;   // double, NOT truncated
double Y = (double)wy;  Y += 100000.0 + (double)m_offset1;
float h = 0f;
h = (float)((double)h + (double)P(X*0.0020000000949949026*0.5, Y*0.0020000000949949026*0.5)
                      * (double)P(X*0.0030000000260770321*0.5, Y*0.0030000000260770321*0.5) * 1.0);
h = (float)((double)h + (double)P(X*0.0020000000949949026*1.0, Y*0.0020000000949949026*1.0)
                      * (double)P(X*0.0030000000260770321*1.0, Y*0.0030000000260770321*1.0)
                      * (double)h * 0.89999997615814209);
h = (float)((double)h + (double)P(X*0.004999999888241291*1.0, Y*0.004999999888241291*1.0)
                      * (double)P(X*0.0099999997764825821*1.0, Y*0.0099999997764825821*1.0)
                      * 0.5 * (double)h);
h = (float)((double)h - 0.070000000298023224);
return h * num2 + num;                    // float mul/add

// ---------- menuTerrain == false (IL_01ce .. IL_0542) ----------
float dist = DUtils.Length(wx, wy);                   // float result, double interior (§7.1)
double X = (double)wx;  X += 100000.0 + (double)m_offset0;
double Y = (double)wy;  Y += 100000.0 + (double)m_offset1;
float h = 0f;
   ... the same four statements as above, identical literals ...
// sea channels
float n10 = P(X*0.0020000000949949026*0.25 + 0.12300000339746475,
              Y*0.0020000000949949026*0.25 + 0.15123000741004944);
float n11 = P(X*0.0020000000949949026*0.25 + 0.32100000977516174,
              Y*0.0020000000949949026*0.25 + 0.23100000619888306);
float v = Mathf.Abs((float)((double)n10 - (double)n11));
float c = (float)(1.0 - (double)DUtils.LerpStep(0.019999999552965164f, 0.11999999731779099f, v));
      c = (float)((double)c * (double)DUtils.SmoothStep(744f, 1000f, dist));
h       = (float)((double)h * (1.0 - (double)c));
// world edge
if (dist > 10000f) {                                          // ble.un.s → NaN falls through here too
    float t = DUtils.LerpStep(10000f, 10500f, dist);
    h = DUtils.Lerp(h, -0.20000000298023224f, t);
    float e = 10490f;
    if (dist > e) {
        float t2 = Utils.LerpStep(e, 10500f, dist);           // NOTE: Utils, all-float (§7.1)
        h = DUtils.Lerp(h, -2f, t2);
    }
    return h * num2 + num;
}
// near-spawn mountain suppression
if (dist < m_minMountainDistance && h > 0.2800000011920929f) {
    float t3 = (float)DUtils.Clamp01(((double)h - 0.2800000011920929) / 0.099999994039535522);
    h = DUtils.Lerp( DUtils.Lerp(0.2800000011920929f, 0.37999999523162842f, t3),
                     h,
                     DUtils.LerpStep((float)((double)m_minMountainDistance - 400.0),
                                     m_minMountainDistance, dist) );
}
return h * num2 + num;
```

Notes the port must honour:

- `X`/`Y` are **double and never truncated to float** here — unlike the per-biome coordinates (§3.1).
  The grouping is `wx + (100000.0 + off0)`: the inner add first (IL_001c/IL_0026/IL_002c `add`, then
  IL_002d `add`). All three values are exact integers in double, so the grouping is harmless, but keep it.
- The literal `1.0` factor on the first octave and the `1.0` scale factors on octave 2/3 are real `mul`
  instructions; they are exact no-ops.
- `Mathf.Abs(float)` is `Math.Abs(float)` (`Mathf.Abs` / IL_0002).
- `dist` uses `DUtils.Length` (double interior), *not* `Vector2.magnitude`.
- The 10490 branch uses **`Utils.LerpStep`** (`GetBaseHeight` / IL_04b3
  `call System.Single Utils::LerpStep`), which is `Utils.Clamp01((v-l)/(h-l))` with **float** operands
  throughout (`Utils.LerpStep` / IL_0000–IL_000c). Every other `LerpStep` in the file is `DUtils.LerpStep`
  (double interior, float result).
- Consequences: mountains need base > 0.4, and the cap holds base ≤ 0.38 for `dist < m_minMountainDistance - 400`
  (600 m on v≥1) fading out at `m_minMountainDistance` — so **no Mountain within 600 m of (0,0)** on a v2 world.

### 2.3 `GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false)` — public

Defaults confirmed from metadata: `oceanLevel = 0.02`, `waterAlwaysOcean = false`.
Exact sequence (`WorldGenerator.GetBiome` / IL_0000–IL_01f6):

```csharp
if (m_world.m_menu)                                              // IL_000b
    return GetBaseHeight(wx, wy, true) >= 0.40000000596046448f ? Biome.Mountain : Biome.BlackForest;

float dist = DUtils.Length(wx, wy);                              // IL_0023  (float)
float base = GetBaseHeight(wx, wy, false);                       // IL_002d
float A    = (float)((double)WorldAngle(wx, wy) * 100.0);        // IL_0045 conv.r4

if (waterAlwaysOcean && GetHeight(wx, wy) <= oceanLevel) return Biome.Ocean;   // IL_0047..IL_005b
if (IsAshlands(wx, wy))                                  return Biome.AshLands;// IL_005e  (32)
if (!waterAlwaysOcean && base <= oceanLevel)             return Biome.Ocean;   // IL_0068..IL_0075 (256)
if (IsDeepnorth(wx, wy))                                 return Biome.DeepNorth;//IL_0078 (64)
if (base > 0.40000000596046448f)                         return Biome.Mountain;// IL_0083 (4)

if (P((float)((double)m_offset0 + (double)wx) * 0.0010000000474974513,
      (float)((double)m_offset0 + (double)wy) * 0.0010000000474974513) > 0.60000002384185791f
    && dist > 2000f && dist < maxMarshDistance
    && base > 0.05000000074505806f && base < 0.25f)      return Biome.Swamp;   // IL_00e5 (2)

if (P((float)(off4+wx)*0.001f…, (float)(off4+wy)*0.001f…) > minDarklandNoise
    && dist > (float)(6000.0 + (double)A) && dist < 10000f) return Biome.Mistlands;  // IL_0138 (512)

if (P((float)(off1+wx)*0.001f…, (float)(off1+wy)*0.001f…) > 0.40000000596046448f
    && dist > (float)(3000.0 + (double)A) && dist < 8000f)  return Biome.Plains;     // IL_018e (16)

if (P((float)(off2+wx)*0.001f…, (float)(off2+wy)*0.001f…) > 0.40000000596046448f
    && dist > (float)(600.0 + (double)A) && dist < 6000f)   return Biome.BlackForest;// IL_01e1 (8)

if (dist > (float)(5000.0 + (double)A))                    return Biome.BlackForest;// IL_01f3 (8)
return Biome.Meadows;                                                                // IL_01f5 (1)
```

Mask-noise coordinate shape, exactly (e.g. IL_008d–IL_00b7):
`ldfld off; conv.r8; ldarg wx; conv.r8; add; conv.r4; conv.r8; ldc.r8 0.0010000000474974513; mul`
→ **`(double)((float)((double)off + (double)wx)) * 0.0010000000474974513`**, then `DUtils.PerlinNoise`
truncates that product back to float. Two truncations per axis. The offset comes first in the add.

Band terms: `dist > (float)(6000.0 + (double)A)` — the sum is formed in double and then truncated
(`ldc.r8 6000; ldloc A; conv.r8; add; conv.r4`), and compared float-to-float. The **upper** bounds
(6000 / 8000 / 10000) and the Swamp band (2000 … `maxMarshDistance`) carry **no** wobble.

All comparisons use the unordered forms (`ble.un`, `bge.un`, `bgt.un`), so a NaN operand fails the test
and falls through. **Corrected by the reviewer:** with a NaN coordinate `dist = DUtils.Length(wx,wy)` is
NaN, so *every* distance test is false and `Swamp`, `Mistlands`, `Plains`, `BlackForest` (both the noise
branch at IL_01e1 and the `dist > 5000 + A` fallback at IL_01f3) and `DeepNorth` are all unreachable —
`BlackForest` in particular cannot be produced. What remains is `Ocean` (if `base <= oceanLevel`),
`Mountain` (if `base > 0.4`) or `Meadows`, and which of the three is reached depends on what the extern
`Mathf.PerlinNoise` returns for NaN arguments. **Unverified:** `Mathf.PerlinNoise(NaN, NaN)` — it has no
managed body, so `GetBaseHeight` of a NaN coordinate is not predictable from code. Keep coordinates
finite; do not rely on any NaN behaviour.

`GetBiome(Vector3 p)` = `GetBiome(p.x, p.z)`. `wy` is always the world **Z**.

### 2.4 `IsAshlands` / `IsDeepnorth` — static, and the gap functions

```csharp
// (WorldGenerator.IsAshlands / IL_0000–IL_002e)   ashlandsMinDistance = 12000f, ashlandsYOffset = -4000f
//   both are `public static readonly` FIELDS (ldsfld), not consts.
public static bool IsAshlands(float x, float y) {
    double A = (double)WorldAngle(x, y) * 100.0;                       // double, NOT truncated
    float  yy = (float)((double)y + (double)ashlandsYOffset);          // conv.r4 after the add
    return (double)DUtils.Length(x, yy) > (double)ashlandsMinDistance + A;   // compared in DOUBLE
}

// (WorldGenerator.IsDeepnorth / IL_0000–IL_003e)   literals 12000 / 4000, no fields
public static bool IsDeepnorth(float x, float y) {
    float A = (float)((double)WorldAngle(x, y) * 100.0);               // conv.r4
    float yy = (float)((double)y + 4000.0);
    return new Vector2(x, yy).magnitude > (float)(12000.0 + (double)A);// compared in FLOAT
}
```

The two are deliberately different: AshLands uses `DUtils.Length` (double-interior sqrt) and a double
comparison; DeepNorth uses `Vector2.magnitude` (float-interior sum) and a float comparison. Do not
unify them.

Geometry: AshLands = outside a circle of radius `12000 + A` centred at **(0, +4000)** → the far south
(at x = 0 it starts near z ≈ −8000). DeepNorth = outside a circle of radius `12000 + A` centred at
**(0, −4000)** → the far north (at x = 0, z ≈ +8000).

```csharp
// (WorldGenerator.CreateAshlandsGap / IL_0000–IL_005d)  — private INSTANCE method
private double CreateAshlandsGap(float wx, float wy) {
    double A = (double)WorldAngle(wx, wy) * 100.0;
    double v = (double)DUtils.Length(wx, wy + ashlandsYOffset)      // float Length, float add of y
             - ((double)ashlandsMinDistance + A);
    v = DUtils.Clamp01(Math.Abs(v) / 400.0);
    return DUtils.MathfLikeSmoothStep(0.0, 1.0, (double)(float)v);  // IL_0054 conv.r4, IL_0055 conv.r8
}

// (WorldGenerator.CreateDeepNorthGap / IL_0000–IL_0060) — public STATIC, literals instead of fields
public static double CreateDeepNorthGap(float wx, float wy) {
    double A = (double)WorldAngle(wx, wy) * 100.0;
    double v = (double)DUtils.Length(wx, wy + 4000f) - (12000.0 + A);
    v = DUtils.Clamp01(Math.Abs(v) / 400.0);
    return DUtils.MathfLikeSmoothStep(0.0, 1.0, (double)(float)v);
}

// (WorldGenerator.DeepNorthWaveFade / IL_0000–IL_003c) — public static, no smoothstep, /200
public static double DeepNorthWaveFade(float wx, float wy) {
    double A = (double)WorldAngle(wx, wy) * 100.0;
    return DUtils.Clamp01(((double)DUtils.Length(wx, wy + 4000f) - (12000.0 + A)) / 200.0);
}

// (WorldGenerator.GetAshlandsOceanGradient / decompiled) — public static; NOTE the wobble is evaluated
// at (x, y + ashlandsYOffset), out of phase with IsAshlands. Not used by height or biome.
public static float GetAshlandsOceanGradient(float x, float y) {
    double A = (double)WorldAngle(x, y + ashlandsYOffset) * 100.0;
    return (float)(((double)DUtils.Length(x, y + ashlandsYOffset) - ((double)ashlandsMinDistance + A)) / 300.0);
}
```

Both gap functions return 0 exactly on the ring and rise to 1 at 400 m from it; they multiply the height
multiplier (§3.2), which is what carves the ocean moat in front of AshLands and Deep North.
The `(float)` cast before `MathfLikeSmoothStep` is real and must be reproduced, and
`MathfLikeSmoothStep` itself returns a **float-rounded** double (§7.1).

### 2.5 World edge

- `worldSize = 10000f`, `waterEdge = 10500f`, `waterEdgeSqr = 110250000f` (consts, inlined).
- `GetBaseHeight` lerps the base towards −0.2 between 10000 and 10500 m, and towards −2 between 10490
  and 10500 m (§2.2).
- `GetBiomeHeight` short-circuits: `if (DUtils.Length(wx, wy) > 10500f) return -2f * GetHeightMultiplier();`
  → **−400 f exactly**, evaluated *after* the menu branch and *before* the biome switch
  (`GetBiomeHeight` / IL_0075–IL_008e).
- `GetEdgeHeight` exists but is **dead code** (no callers).
- High ground therefore remains land a few hundred metres past 10000 m and turns Ocean progressively as
  the base falls to ≤ 0.02 — except inside the AshLands region, which test 2 of `GetBiome` catches first.

---

## 3. Height

### 3.1 Shared per-biome coordinate preparation

Every biome function except `GetMarshHeight` (no offset at all), `GetOceanHeight` (no detail noise) and
`GetAshlandsHeight` (double coordinates) does:

```csharp
float wx2 = wx, wy2 = wy;                       // the ORIGINAL coordinates, kept for AddRivers
float base = GetBaseHeight(wx, wy, false);      // (menuTerrain flag for the mountain function)
wx = (float)((double)wx + 100000.0 + (double)m_offset3);   // ((wx + 100000.0) + off3), then conv.r4
wy = (float)((double)wy + 100000.0 + (double)m_offset3);
double U = (double)wx, V = (double)wy;
```

(`GetPlainsHeight` / IL_000d–IL_003c is the reference; `GetMeadowsHeight`, `GetForestHeight`,
`GetMistlandsHeight`, `GetMenuHeight`, `GetSnowMountainHeight`, `GetDeepNorthHeight`,
`GetDeepNorthHeightPregenerate`, `GetAshlandsHeightPregenerate` are identical in shape.)

The grouping here is `(wx + 100000.0) + off3`, which differs from `GetBaseHeight`'s
`wx + (100000.0 + off0)`; both are exact, but copy them as written.

**The `conv.r4` matters.** `U` lies in 79500 … 120499 for every in-world coordinate
(|wx| ≤ 10500, off3 ∈ [−10000, 9999]), i.e. always in the binade [65536, 131072), where the float ulp is
exactly **1/128 = 0.0078125**. Detail noise is therefore quantised to a 1/128 m lattice everywhere in the
world. A port that keeps `U` in double loses this and produces visibly different micro-terrain.

Two shared sub-expressions (the literals are the widened float constants):

```
D  = (float)((double)P(U*0.0099999997764825821, V*0.0099999997764825821)
           * (double)P(U*0.019999999552965164,  V*0.019999999552965164));
D  = (float)((double)D + (double)P(U*0.05000000074505806, V*0.05000000074505806)
                       * (double)P(U*0.10000000149011612, V*0.10000000149011612)
                       * (double)D * 0.5);
F(h): h = (float)((double)h + (double)P(U*0.10000000149011612, V*0.10000000149011612) * 0.0099999997764825821);
      h = (float)((double)h + (double)P(U*0.40000000596046448, V*0.40000000596046448) * 0.0030000000260770321);
```

Exception: in **`GetMistlandsHeight`** and **`GetMenuHeight`** the first line of `D` is a plain **float**
multiply, not a double one — `call P; call P; mul; stloc` with no `conv.r8`
(`GetMistlandsHeight` / IL_006e–IL_00a4, `GetMenuHeight` / IL_0053–IL_0074).

### 3.2 `GetBiomeHeight` — public

```csharp
// (WorldGenerator.GetBiomeHeight / IL_0000–IL_01d1+)
float num = 0f;                                                  // IL_0000
float mult = preGeneration
    ? GetHeightMultiplier()                                      // 200f exactly
    : (float)((double)GetHeightMultiplier()
              * CreateAshlandsGap(wx, wy) * CreateDeepNorthGap(wx, wy));   // conv.r4 at IL_0029
mask = Color.black;                                              // (0,0,0,1)
BiomeSector _ = GetBiomeSector(wx, wy);                          // IL_003b — result is NEVER used
if (m_world.m_menu)
    return biome == Biome.Mountain
        ? (float)((double)GetSnowMountainHeight(wx, wy, true)  * (double)mult + (double)num)
        : (float)((double)GetMenuHeight(wx, wy)                * (double)mult + (double)num);
if (DUtils.Length(wx, wy) > 10500f) return -2f * GetHeightMultiplier();    // -400f
switch (biome) {
  case Swamp:       return (float)((double)GetMarshHeight(wx,wy)          * (double)mult + (double)num);
  case DeepNorth:   return preGeneration
                      ? scale(GetDeepNorthHeightPregenerate(wx, wy, riverPreDN))
                      : scale(GetDeepNorthHeight(wx, wy, out mask));
  case Mountain:    return scale(GetSnowMountainHeight(wx, wy, false));
  case BlackForest: return scale(GetForestHeight(wx, wy));
  case Ocean:       return scale(GetOceanHeight(wx, wy));
  case AshLands:    return preGeneration
                      ? scale(GetAshlandsHeightPregenerate(wx, wy))
                      : scale(GetAshlandsHeight(wx, wy, out mask));
  case Plains:      return scale(GetPlainsHeight(wx, wy));
  case Meadows:     return scale(GetMeadowsHeight(wx, wy));
  case Mistlands:   return preGeneration
                      ? scale(GetForestHeight(wx, wy))           // pre-gen Mistlands == BlackForest
                      : scale(GetMistlandsHeight(wx, wy, out mask));
  default:          return 0f;                                   // includes Biome.None
}
// scale(f) == (float)((double)f * (double)mult + (double)num)
```

Defaults from metadata: `preGeneration = false`, `riverPreDN = true`.
`GetHeightMultiplier()` is a static returning the literal `200f`; `GetBiomeHeight` calls it three times.
`c_HeightMultiplier` (the 200f const) is never read.

**`GetBiomeSector(wx, wy)` is called and discarded** — a port omits it entirely (it only reads
`m_world.m_biomeData`, which an offline tool does not have).

The result is **world metres**, with **sea level at 30**. (`Minimap.GetMaskColor` treats
`GetBiomeHeight < 30f` as water; `AltBiomeWorldData` counts `>= 30f` as above sea level;
`ZoneSystem.m_waterLevel` code default is 30.) Normalised 0.15 × 200 = 30.

`GetHeight(wx, wy)` = `GetBiomeHeight(GetBiome(wx, wy), wx, wy, out _)`.
`GetHeight(wx, wy, out mask)` is the same with the mask returned.
`GetPregenerationHeight(wx, wy, riverPreGen)` =
`GetBiomeHeight(GetBiome(wx, wy), wx, wy, out _, preGeneration: true, riverPreDN: riverPreGen)`.

### 3.3 The biome height functions

All of the below return a **normalised** height (multiply by `mult` afterwards). `F(h)` and `D` are §3.1.
"rivers" = `AddRivers(wx2, wy2, h)` with the **original** coordinates.

#### `GetMarshHeight(wx, wy)` — Swamp. No seed offset at all.

```csharp
float wx2 = wx, wy2 = wy;
float h = 0.137f;
wx = (float)((double)wx + 100000.0);   wy = (float)((double)wy + 100000.0);   // NO m_offset3
double U = wx, V = wy;
float n = (float)((double)P(U*0.039999999105930328, V*0.039999999105930328)
                * (double)P(U*0.079999998211860657, V*0.079999998211860657));
h = (float)((double)h + (double)n * 0.029999999329447746);
h = AddRivers(wx2, wy2, h);
h = (float)((double)h + (double)P(U*0.10000000149011612, V*0.10000000149011612) * 0.0099999997764825821);
return (float)((double)h + (double)P(U*0.40000000596046448, V*0.40000000596046448) * 0.0030000000260770321);
```

Because there is no offset, **Swamp micro-terrain is identical in every world**; only where Swamp appears
changes. Swamp also ignores `GetBaseHeight` entirely — it is a flat 0.137 (27.4 m) plus detail.

#### `GetMeadowsHeight(wx, wy)` — Meadows

```csharp
float h = base;
h = (float)((double)h + (double)D * 0.10000000149011612);
float sea = 0.15000000596046448f;
float over = (float)((double)h - (double)sea);                       // double sub + conv.r4
float k = (float)DUtils.Clamp01((double)base / 0.40000000596046448);
if (over > 0f)
    h = (float)((double)h - (double)over * ((1.0 - (double)k) * 0.75));   // GROUPING: (1-k)*0.75 first
h = AddRivers(wx2, wy2, h);
return F(h);
```
(`GetMeadowsHeight` / IL_00e9–IL_00f1 for the subtraction, IL_0115–IL_012c for the grouping.)

#### `GetPlainsHeight(wx, wy)` — Plains. **Same formula, different grouping and a float subtraction.**

```csharp
float over = h - sea;                                                 // pure FLOAT sub (IL_00ed)
...
if (over > 0f)
    h = (float)((double)h - (double)over * (1.0 - (double)k) * 0.75); // ((h-sea)*(1-k)) * 0.75
```
(`GetPlainsHeight` / IL_00e9–IL_00ee, IL_010e–IL_012b.) The float subtraction is numerically identical
to the Meadows double-sub-then-round (one rounding either way); **the 0.75 grouping is not.**

#### `GetForestHeight(wx, wy)` — BlackForest (and pre-generation Mistlands)

```csharp
float h = base;
h = (float)((double)h + (double)D * 0.10000000149011612);
h = AddRivers(wx2, wy2, h);
return F(h);
```
No sea-level squash.

#### `GetMistlandsHeight(wx, wy, out Color mask)` — Mistlands

```csharp
float M = P(U*0.019999999552965164*0.699999988079071, V*…) * P(U*0.039999999105930328*0.7…, V*…); // FLOAT mul
M = (float)((double)M + (double)P(U*0.029999999329447746*0.7…, V*…)
                      * (double)P(U*0.05000000074505806*0.7…, V*…) * (double)M * 0.5);
M = (M > 0f) ? (float)Math.Pow((double)M, 1.5) : M;                   // bgt.s at IL_0125
float h = (float)((double)base + (double)M * 0.40000000596046448);
h = AddRivers(wx2, wy2, h);
float k = (float)DUtils.Clamp01((double)M * 7.0);
h = (float)((double)h + (double)P(U*0.1f…, V*0.1f…) * 0.029999999329447746 * (double)k);
h = (float)((double)h + (double)P(U*0.4f…, V*0.4f…) * 0.0099999997764825821 * (double)k);
float a = (float)(1.0 - (double)k * 1.2000000476837158);
      a = (float)((double)a - (1.0 - (double)DUtils.LerpStep(0.10000000149011612f,
                                                            0.30000001192092896f, k)));
float smooth = (float)((double)h + (double)P(U*0.4f…, V*0.4f…) * 0.0020000000949949026);
float terr   = h;
terr = (float)((double)terr * 400.0);
terr = Mathf.Ceil(terr);                    // (float)Math.Ceiling((double)terr)
terr = (float)((double)terr / 400.0);
h = DUtils.Lerp(smooth, terr, k);
mask = new Color(0f, 0f, 0f, a);
return h;
```
(`GetMistlandsHeight` / IL_0043–IL_028b.) `0.02*0.7` etc. are two separate `mul` instructions with
the literals `0.019999999552965164` and `0.699999988079071` — do not pre-multiply them.

#### `GetSnowMountainHeight(wx, wy, bool menu)` — Mountain

```csharp
float wx2 = wx, wy2 = wy;
float h = GetBaseHeight(wx, wy, menu);            // the menu flag is passed through
float tilt = BaseHeightTilt(wx, wy);              // uses the ORIGINAL wx, wy
wx = (float)((double)wx + 100000.0 + (double)m_offset3);  wy = likewise;
double U = wx, V = wy;
float d = (float)((double)h - 0.40000000596046448);
h = (float)((double)h + (double)d);                        // h + (h - 0.4)   (can be negative)
float D2 = (float)((double)P(U*.01…, V*.01…) * (double)P(U*.02…, V*.02…));
      D2 = (float)((double)D2 + (double)P(U*.05…,V*.05…) * (double)P(U*.1…,V*.1…) * (double)D2 * 0.5);
h = (float)((double)h + (double)D2 * 0.20000000298023224);
h = AddRivers(wx2, wy2, h);
h = (float)((double)h + (double)P(U*.1…, V*.1…) * 0.0099999997764825821);
h = (float)((double)h + (double)P(U*.4…, V*.4…) * 0.0030000000260770321);
return (float)((double)h + (double)P(U*0.20000000298023224, V*0.20000000298023224) * 2.0 * (double)tilt);
```
(`GetSnowMountainHeight` / IL_0000–IL_019d.)

```csharp
// (WorldGenerator.BaseHeightTilt / IL_0000–IL_0070)  — note each coordinate step is a double add + conv.r4
private float BaseHeightTilt(float wx, float wy) {
    float a = GetBaseHeight((float)((double)wx - 1.0), wy, false);
    float b = GetBaseHeight((float)((double)wx + 1.0), wy, false);
    float c = GetBaseHeight(wx, (float)((double)wy - 1.0), false);
    float d = GetBaseHeight(wx, (float)((double)wy + 1.0), false);
    return (float)((double)Mathf.Abs((float)((double)b - (double)a))
                 + (double)Mathf.Abs((float)((double)c - (double)d)));
}
```
Four extra `GetBaseHeight` evaluations per Mountain sample — the dominant cost of a mountain query.

#### `GetOceanHeight(wx, wy)` — Ocean

```csharp
return GetBaseHeight(wx, wy, false);     // no detail, NO rivers
```

#### `GetDeepNorthHeight(wx, wy, out Color mask)` — Deep North (final)

```csharp
float wx2 = wx, wy2 = wy;
float b = GetBaseHeight(wx, wy, false) + 0.10000000149011612f;   // PURE FLOAT add (IL_000d 'add')
wx = (float)((double)wx + 100000.0 + (double)m_offset3);  wy = likewise;
double U = wx, V = wy;
float D2 = … (the standard double-multiplied D on U,V) …;
float h = b;
h = (float)((double)h + (double)D2 * 0.10000000149011612);
float sea = 0.15f;
float over = (float)((double)h - (double)sea);
float k = (float)DUtils.Clamp01((double)b / 0.40000000596046448);    // note: b, the +0.1 value
if (over > 0f) h = (float)((double)h - (double)over * ((1.0 - (double)k) * 0.75));  // Meadows grouping
h = AddRivers(wx2, wy2, h);
h = (float)((double)h + (double)P(U*.1…, V*.1…) * 0.0099999997764825821);
h = (float)((double)h + (double)P(U*.4…, V*.4…) * 0.0030000000260770321);
float g = (float)DUtils.Fbm(new Vector2((float)(U*0.0099999997764825821),
                                        (float)(V*0.0099999997764825821)), 3, 2.0, 0.5);  // DOUBLE Fbm
g = (g + 1f) / 2f;                        // float ops
g = 0.3f + g * 0.3f;                      // float ops
mask = new Color(0f, g, 0f, 0f);
return h;
```
(`GetDeepNorthHeight` / IL_0000–IL_01e0+, `DUtils::Fbm(Vector2,Int32,Double,Double)` at IL_01cd.)

#### `GetDeepNorthHeightPregenerate(wx, wy, bool riverPregen)` — pre-generation only

```csharp
float wx2 = wx, wy2 = wy;
float h = GetBaseHeight(wx, wy, false);
if (!riverPregen) h += 0.1f;                                  // float add; pass 2 only
wx = (float)((double)wx + 100000.0 + (double)m_offset3);  wy = likewise;
double U = wx, V = wy;
float d = Mathf.Max(0f, (float)((double)h - 0.40000000596046448));   // clamped, unlike the Mountain one
h = (float)((double)h + (double)d);
float D2 = … standard D …;
h = (float)((double)h + (double)D2 * 0.20000000298023224);
h = (float)((double)h * 1.2000000476837158);
h = AddRivers(wx2, wy2, h);
h = (float)((double)h + (double)DUtils.PerlinNoise(wx * 0.1f, wy * 0.1f) * 0.0099999997764825821);
return (float)((double)h + (double)DUtils.PerlinNoise(wx * 0.4f, wy * 0.4f) * 0.0030000000260770321);
```
(`GetDeepNorthHeightPregenerate` / IL_0060 `Mathf::Max`, IL_0137 and IL_015a
`call System.Single DUtils::PerlinNoise(System.Single,System.Single)`.)

**The last two noise terms use the float `PerlinNoise(float,float)` overload with float-multiplied
coordinates `wx*0.1f` / `wx*0.4f`** — every other biome multiplies in double. This is the only place in
the file that does so, and it is the reason the Deep North pre-generation height differs slightly from
its final height beyond the +0.1.

#### `GetAshlandsHeightPregenerate(wx, wy)` — pre-generation only

```csharp
float wx2 = wx, wy2 = wy;
float h = GetBaseHeight(wx, wy, false);
wx/wy → offset by 100000 + off3 → U, V;
float D2 = … standard D …;
h = (float)((double)h + (double)D2 * 0.10000000149011612);
h = (float)((double)h + 0.10000000149011612);
h = (float)((double)h + (double)P(U*.1…, V*.1…) * 0.0099999997764825821);
h = (float)((double)h + (double)P(U*.4…, V*.4…) * 0.0030000000260770321);
return AddRivers(wx2, wy2, h);           // rivers LAST, after the fine noise — unlike every other biome
```

#### `GetMenuHeight(wx, wy)` — menu world only

```csharp
float b = GetBaseHeight(wx, wy, true);        // left on the IL evaluation stack, not a local
wx/wy → offset by 100000 + off3 → U, V;
float D2 = P(U*.01…, V*.01…) * P(U*.02…, V*.02…);          // FLOAT mul (IL_0073)
      D2 = (float)((double)D2 + (double)P(U*.05…) * (double)P(U*.1…) * (double)D2 * 0.5);
return (float)((double)(float)((double)(float)((double)b + (double)D2 * 0.10000000149011612)
             + (double)P(U*.1…, V*.1…) * 0.0099999997764825821)
             + (double)P(U*.4…, V*.4…) * 0.0030000000260770321);
```
No rivers, no sea-level squash.

#### `GetAshlandsHeight(wx, wy, out Color mask, bool cheap = false)` — public

The only function that works in **double end to end**; its coordinates are **not** truncated to float
(except the `100000f + m_offset3` addend). Literals below are exact IL values
(`GetAshlandsHeight` / IL_0000–IL_0572).

```csharp
double x = (double)wx, y = (double)wy;
double a = (double)GetBaseHeight((float)x, (float)y, false);
double A = (double)WorldAngle((float)x, (float)y) * 100.0;

// ridge band around a circle 1200 m SOUTH of the AshLands ring (deeper into AshLands):
// the ring centre moves from (0, +4000) to (0, +2800), so at x = 0 the ridge crest sits at
// z = 2800 - (12000 + A) ~ -9200, while the biome boundary is at z = 4000 - (12000 + A) ~ -8000.
// (corrected by the reviewer; GetAshlandsHeight / decompiled 1221 vs IsAshlands / decompiled 751-755)
double r = DUtils.Length(x, y + (double)ashlandsYOffset - (double)ashlandsYOffset * 0.3)  // == y - 2800
         - ((double)ashlandsMinDistance + A);                       // DOUBLE Length overload
r = Math.Abs(r) / 1000.0;
r = 1.0 - DUtils.Clamp01(r);
r = DUtils.MathfLikeSmoothStep(0.1, 1.0, r);                        // float-rounded result (§7.1)
double xf = 1.0 - DUtils.Clamp01(Math.Abs(x) / 7500.0);
r *= xf;

double edge = DUtils.Length(x, y) - 10150.0;
edge = 1.0 - DUtils.Clamp01(edge / 600.0);

x += (double)(100000f + m_offset3);       // FLOAT add, then widened (IL_00f0–IL_00fc)
y += (double)(100000f + m_offset3);

// cellular fBm, 5 octaves (2 if cheap), starting frequency multiplier 0.33
double c = 0.0, amp = 1.0, freq = 0.33000001311302185;
for (int i = 0; i < (cheap ? 2 : 5); i++) {
    c += amp * DUtils.MathfLikeSmoothStep(0.0, 1.0, m_noiseGen.GetCellular(x * freq, y * freq));
    freq *= 2.0; amp *= 0.5;
}
c = DUtils.Remap(c, -1.0, 1.0, 0.0, 1.0);
double ridge = DUtils.Lerp(r, DUtils.BlendOverlay(r, c), 0.5);

double unusedD = (double)P(x*0.0099999997764825821, y*0.0099999997764825821)
               * (double)P(x*0.019999999552965164,  y*0.019999999552965164);
unusedD += (double)(P(x*0.05000000074505806, y*0.05000000074505806)
                  * P(x*0.10000000149011612, y*0.10000000149011612)) * unusedD * 0.5;
                                                     // NOTE: computed and never used afterwards

double h = DUtils.Lerp(a, 0.15000000596046448, 0.75);
h += ridge * 0.5;
h = DUtils.Lerp(-1.0, h, DUtils.MathfLikeSmoothStep(0.0, 1.0, edge));

const double seaLevel = 0.15;
// lava cellular, 3 octaves (2 if cheap), starting frequency multiplier 8.0
double lc = 0.0; amp = 1.0; freq = 8.0;
for (int j = 0; j < (cheap ? 2 : 3); j++) { lc += amp * m_noiseGen.GetCellular(x*freq, y*freq);
                                            freq *= 2.0; amp *= 0.5; }
lc = DUtils.Remap(lc, -1.0, 1.0, 0.0, 1.0);
lc = DUtils.Clamp01(Math.Pow(lc, 4.0) * 2.0);

double s = m_noiseGen.GetSimplexFractal(x * 0.075, y * 0.075);
s = DUtils.Remap(s, -1.0, 1.0, 0.0, 1.0);
s = Math.Pow(s, 1.3999999761581421);
h *= s;

double f = DUtils.Fbm(new Vector2((float)(x*0.0099999997764825821),
                                  (float)(y*0.0099999997764825821)), 3, 2.0, 0.5);   // DOUBLE Fbm
f *= DUtils.Clamp01(DUtils.Remap(r, 0.0, 0.5, 0.5, 1.0));
f  = DUtils.LerpStep(0.699999988079071, 1.0, f);                    // DOUBLE LerpStep
f  = Math.Pow(f, 2.0);
double lava = DUtils.BlendOverlay(f, lc);
lava *= DUtils.Clamp01((h - seaLevel - 0.02) / 0.01);

double dip = (double)P(x*0.05 + 5124.0, y*0.05 + 5000.0);
dip = Math.Pow(dip, 2.0);
dip = DUtils.Remap(dip, 0.0, 1.0, 0.0099999997764825821, 0.054999999701976776);
double clamped = (double)Mathf.Clamp((float)(h - dip),
                                     (float)(seaLevel + 0.0099999997764825821), 5000f);
h = DUtils.Lerp(h, clamped, lava);
mask = new Color(0f, 0f, 0f, (float)lava);
return (float)h;
```

The `unusedD` block really is dead — it is computed into a local that nothing reads afterwards
(the decompiler names it `num11`). A port may drop it; it consumes four `Mathf.PerlinNoise` calls and
nothing else, so dropping it is observationally equivalent (`Mathf.PerlinNoise` is pure).
**Unverified:** that `Mathf.PerlinNoise` is side-effect free; it is an `extern` with no managed body, but
it takes only its two coordinates and has no seed API, so this is a safe assumption.

`Minimap` calls `GetAshlandsHeight(..., cheap: true)` for the map's lava overlay; `HeightmapBuilder`,
`GetBiomeHeight` and `ZoneSystem.IsLavaPreHeightmap` use `cheap: false`.

### 3.4 Which functions add rivers

| Function | `AddRivers` | Where in the sequence |
|---|---|---|
| `GetMarshHeight` | yes | after the 0.03 detail, **before** the fine noise |
| `GetMeadowsHeight` | yes | after the sea-level squash, before `F` |
| `GetPlainsHeight` | yes | same |
| `GetForestHeight` | yes | after `+D*0.1`, before `F` |
| `GetMistlandsHeight` | yes | after `+M*0.4`, before the `k`-weighted detail and terracing |
| `GetSnowMountainHeight` | yes | after `+D2*0.2`, before the fine noise and the tilt term |
| `GetDeepNorthHeight` | yes | after the squash, before `F` |
| `GetDeepNorthHeightPregenerate` | yes | after `*1.2`, before the float-coordinate fine noise |
| `GetAshlandsHeightPregenerate` | yes | **last**, after the fine noise |
| `GetOceanHeight` | no | — |
| `GetMenuHeight` | no | — |
| `GetAshlandsHeight` (final) | **no** | AshLands terrain has no river carving |
| `GetEdgeHeight` (dead) | yes | — |

### 3.5 `AddRivers(float wx, float wy, float h)` — private

```csharp
// (WorldGenerator.AddRivers / IL_0000–IL_007c)
GetRiverWeight(wx, wy, out float weight, out float width);
if (weight <= 0f) return h;                       // bgt.un: NaN weight also returns h
float t   = DUtils.LerpStep(20f, 60f, width);
float bed = DUtils.Lerp(0.14000000059604645f, 0.11999999731779099f, t);
float mid = DUtils.Lerp(0.13899999856948853f, 0.12800000607967377f, t);
if (h > bed) h = DUtils.Lerp(h, bed, weight);
if (h > mid) h = DUtils.Lerp(h, mid, DUtils.LerpStep(0.85000002384185791f, 1f, weight));
return h;
```

Rivers only ever **lower** terrain, to 0.14 … 0.12 normalised (28 … 24 m) — below the 30 m water line.
Streams (width 20) land at the 0.14/0.139 end, wide rivers at 0.12/0.128. The fine noise `F` (up to
+0.013 ≈ +2.6 m) is added afterwards in most biomes.

### 3.6 Masks and their consumers

`GetBiomeHeight` sets `mask = Color.black` = `(0, 0, 0, 1)` and only three functions overwrite it:

| Producer | Channel | Value |
|---|---|---|
| `GetMistlandsHeight` | `a` | `1 − 1.2k − (1 − LerpStep(0.1, 0.3, k))`, `k = clamp01(7M)`; `r=g=b=0` |
| `GetDeepNorthHeight` | `g` | `0.3 + 0.3·((Fbm(U·0.01, V·0.01, 3, 2, 0.5) + 1)/2)` ∈ **[0.45, 0.7125]**, typical ≈ 0.58; `r=b=a=0` |
| `GetAshlandsHeight` | `a` | `lava` ∈ [0, 1]; `r=g=b=0` |

Everything else leaves `mask = (0,0,0,1)`.

Consumers:

- **`HeightmapBuilder.Build`** writes the mask per vertex into `data.m_baseMask`, blending the
  four corner biomes' masks with `Color.Lerp` when the corners disagree (`HeightmapBuilder.Build` /
  decompiled lines 174–207). That becomes `Heightmap.m_paintMask`.
  **Corrected by the reviewer:** the blend is *not* plain bilinear — both weights are smoothstepped
  first: `t2 = DUtils.SmoothStep(0f, 1f, (float)((double)l / (double)data.m_width))` across x and
  `t = DUtils.SmoothStep(0f, 1f, (float)((double)k / (double)data.m_width))` across y, and the four
  heights are combined with the same two weights via `DUtils.Lerp`
  (`HeightmapBuilder.Build` / decompiled 176, 181, 196–207). Anything validating the port against a
  game-generated heightmap must use the smoothstepped weights.
- **`Heightmap.GetVegetationMask(pos)`** returns `m_paintMask.a` at the vertex for `pos` shifted by
  `(−0.5, −0.5)` in x/z — so Mistlands `mask.a` suppresses
  vegetation, and AshLands `mask.a` doubles as the lava amount (`Heightmap.GetVegetationMask` /
  decompiled 925–931). **Corrected by the reviewer:** `Heightmap.IsLava(pos, lavaValue = 0.6f)` is not
  the mask test alone — it is
  `GetBiome(pos) == AshLands && !IsBiomeEdge() && GetVegetationMask(pos) > lavaValue`
  (`Heightmap.IsLava` / decompiled 958–969), so a heightmap patch that straddles a biome edge reports no
  lava however high the mask is.
- **`ZoneSystem.IsLavaPreHeightmap(pos, lavaValue = 0.6f)`**: `GetBiome != AshLands → false`, otherwise
  `GetBiomeHeight(Biome.AshLands, x, z, out mask); return mask.a > lavaValue`
  (`ZoneSystem.IsLavaPreHeightmap` / decompiled 2869–2877). Note it forces the AshLands branch.
- **Location placement** reads `mask.a` as "vegetation" and rejects candidates outside
  `[m_minimumVegetation, m_maximumVegetation]` (`ZoneSystem` / decompiled ~2020).
- **`Heightmap.GetCultivationMask`/`IsCultivated`** read `m_paintMask.g` (`> 0.5` = cultivated,
  `Heightmap.IsCultivated` / decompiled 946–950), which is what Deep North's `mask.g` feeds.
  **Corrected by the reviewer:** that range is **[0.45, 0.7125]**, not [0.3, 0.6]. `DUtils.Fbm(Vector2,
  int, double, double)` sums `amp * PerlinNoise(...)` with amps 1, 0.5, 0.25 (`DUtils.Fbm` / decompiled
  108–122), and `Mathf.PerlinNoise` is normalised to ≈[0, 1] (the same assumption §5.3 uses for
  `GetForestFactor`), so the fbm lies in ≈[0, 1.75]; `(f + 1)/2 ∈ [0.5, 1.375]` and
  `0.3 + 0.3·that ∈ [0.45, 0.7125]`, centred near 0.58 (`GetDeepNorthHeight` / decompiled 1378–1380,
  arithmetic run by the reviewer). Consequence: Deep North ground is **usually above the 0.5
  "cultivated" threshold**, i.e. deep snow, whereas the [0.3, 0.6] figure would have put it usually
  below. **Unverified:** the exact output range of `Mathf.PerlinNoise` (extern, §7.1) — the bound moves
  with it, but the sign of the conclusion does not unless Perlin can go negative.
- **`Minimap.GetMaskColor`** for AshLands returns `new Color(0, 0, mask.a, 0)` from
  `GetAshlandsHeight(..., cheap: true)` (`Minimap` / decompiled 2082–2086).

For a seed-search tool, `mask.a` is the field that answers "is there lava here" and "how thick is the
Mistlands undergrowth"; `mask.g` is cosmetic.

---

## 4. Lakes, rivers and streams

All of §4 runs once, inside the constructor, only when `!m_menu`. Every `UnityEngine.Random` draw is
marked **[Rn]**.

### 4.1 `FindLakes()`

```csharp
// (WorldGenerator.FindLakes / IL_0000–IL_009a)   no RNG at all
var pts = new List<Vector2>();
for (float z = -10000f; z <= 10000f; z = (float)((double)z + 128.0))       // OUTER = Z
  for (float x = -10000f; x <= 10000f; x = (float)((double)x + 128.0))     // INNER = X
      if (!(new Vector2(x, z).magnitude > 10000f)                          // Vector2.magnitude (float)
          && GetBaseHeight(x, z, false) < 0.05000000074505806f)
          pts.Add(new Vector2(x, z));
m_lakes = MergePoints(pts, 800f);
```

The loop values are −10000, −9872, … , 9968 → **157 × 157 = 24 649** candidate points (9968 + 128 = 10096
exceeds the bound). The radius test uses `Vector2.magnitude`, while `GetBaseHeight` internally uses
`DUtils.Length`; both appear, at different precision (§6). "Lakes" are simply low basins — **open sea
counts**.

### 4.2 `MergePoints(points, range)` and `FindClosest`

```csharp
// (WorldGenerator.MergePoints, FindClosest / decompiled; no RNG)
List<Vector2> result = new();
while (points.Count > 0) {
    Vector2 v = points[0];
    points.RemoveAt(0);                      // O(n) shift — preserves order of the rest
    while (points.Count > 0) {
        int i = FindClosest(points, v, range);
        if (i == -1) break;
        v = (v + points[i]) * 0.5f;          // running midpoint, NOT a centroid
        points[i] = points[points.Count - 1];// swap-remove: changes the order of the remainder
        points.RemoveAt(points.Count - 1);
    }
    result.Add(v);
}

int FindClosest(List<Vector2> points, Vector2 p, float maxDistance) {
    int best = -1; float bestD = 99999f;
    for (int i = 0; i < points.Count; i++) {
        if (points[i] == p) continue;                       // Vector2 epsilon equality! (§6.8)
        float d = Vector2.Distance(p, points[i]);
        if (d < maxDistance && d < bestD) { best = i; bestD = d; }
    }
    return best;
}
```

The swap-remove and the running-midpoint update make the result order- and precision-sensitive; the port
must use the same list operations, in the same order, with the same `List<T>` semantics.

### 4.3 `PlaceRivers()`

```csharp
// (WorldGenerator.PlaceRivers / IL_0000–IL_0165)
var saved = Random.state;
Random.InitState(m_riverSeed);
List<River> rivers = new();
List<Vector2> work = new List<Vector2>(m_lakes);      // a COPY; candidates are searched in m_lakes
while (work.Count > 1) {                              // the last remaining lake is never a start
    Vector2 p = work[0];
    int i = FindRandomRiverEnd(rivers, m_lakes, p, 2000f, 0.4f, 128f);       // [R?]
    if (i == -1 && !HaveRiver(rivers, p))
        i = FindRandomRiverEnd(rivers, m_lakes, p, 5000f, 0.4f, 128f);       // [R?]
    if (i != -1) {
        River r = new River { p0 = p, p1 = m_lakes[i] };
        r.center   = (r.p0 + r.p1) * 0.5f;
        r.widthMax = Random.Range(60f, 100f);                                 // [R] float
        r.widthMin = Random.Range(60f, r.widthMax);                           // [R] float, uses widthMax
        float len  = Vector2.Distance(r.p0, r.p1);
        r.curveWidth      = (float)((double)len / 15.0);
        r.curveWavelength = (float)((double)len / 20.0);
        rivers.Add(r);
    } else {
        work.RemoveAt(0);                       // only removed when NO end was found
    }
}
RenderRivers(rivers);                           // RiverAdd.All  — consumes RNG, see 4.6
Random.state = saved;
return rivers;
```

Because the start lake is not removed on success, one lake keeps spawning rivers until no valid end
remains. The 5000 m fallback is attempted only when the 2000 m search found nothing **and** this lake is
not already an endpoint of any river.

```csharp
// (WorldGenerator.FindRandomRiverEnd / IL_0000–IL_007f)
List<int> cands = new();
for (int i = 0; i < points.Count; i++)
    if (!(points[i] == p)                                   // epsilon equality
        && Vector2.Distance(p, points[i]) < maxDistance      // bge.un skip
        && !HaveRiver(rivers, p, points[i])
        && IsRiverAllowed(p, points[i], checkStep, heightLimit))
        cands.Add(i);
if (cands.Count == 0) return -1;                             // NO draw on this path
return cands[Random.Range(0, cands.Count)];                  // [R] int, maxExclusive
```

**Draw accounting:** `FindRandomRiverEnd` consumes exactly **one int draw iff it returns a valid index**,
and **zero** when it returns −1. So in `PlaceRivers`, the second (5000 m) call is only reached after a
zero-draw first call.

`HaveRiver(rivers, p0)` → true if any river has `p0 == river.p0 || p0 == river.p1`;
`HaveRiver(rivers, p0, p1)` → true if the unordered pair already exists. Both use Vector2 epsilon
equality.

```csharp
// (WorldGenerator.IsRiverAllowed / IL_0000–IL_0072)   no RNG
float len = Vector2.Distance(p0, p1);
Vector2 dir = (p1 - p0).normalized;               // Vector2.Normalize: /magnitude if magnitude > 1e-5f
bool allWater = true;
for (float s = step; s <= (float)((double)len - (double)step); s = (float)((double)s + (double)step)) {
    Vector2 q = p0 + dir * s;
    float b = GetBaseHeight(q.x, q.y, false);
    if (b > heightLimit) return false;            // any sample above 0.4 kills the river
    if (b > 0.05000000074505806f) allWater = false;
}
return !allWater;                                 // the line must cross at least some land
```

### 4.4 `PlaceStreams(bool isDN)` — called twice

```csharp
// (WorldGenerator.PlaceStreams / IL_0000–IL_016e)
var saved = Random.state;
Random.InitState(m_streamSeed);                   // the SAME seed for both passes
List<River> list = new();
for (int i = 0; i < 3000; i++) {
    if (FindStreamStartPoint(100, 26f, 31f, out Vector2 p, out _, !isDN)            // [R] 2/attempt
     && FindStreamEndPoint(100, 36f, 44f, p, 80f, 200f, out Vector2 e, !isDN)) {    // [R] 1/attempt
        Vector2 c = (p + e) * 0.5f;
        float mh = GetPregenerationHeight(c.x, c.y, !isDN);
        if (!(mh < 26f) && !(mh > 44f)) {
            River r = new River { p0 = p, p1 = e, center = c, widthMax = 20f, widthMin = 20f };
            float len = Vector2.Distance(p, e);
            r.curveWidth      = (float)((double)len / 15.0);
            r.curveWavelength = (float)((double)len / 20.0);
            list.Add(r);
        }
    }
}
RenderRivers(list, isDN ? RiverAdd.OnlyDeepNorth : RiverAdd.SkipDeepNorth);   // consumes RNG
Random.state = saved;
return list;
```

`riverPreGen = !isDN` is threaded down to `GetPregenerationHeight` → `GetBiomeHeight(..., riverPreDN)` →
`GetDeepNorthHeightPregenerate(..., riverPregen)`:

- **Pass 1** (`isDN = false`, `riverPreGen = true`): the Deep North pre-gen height is used **without**
  the `+0.1f`; `RenderRivers` then renders only streams whose **p0 is not Deep North**.
- **Pass 2** (`isDN = true`, `riverPreGen = false`): **with** the `+0.1f`; only Deep North streams are
  rendered.

Both passes reseed from the same `m_streamSeed`, but pass 2 sees a different terrain (the +0.1 *and* the
river points rendered by pass 1), so it diverges from pass 1 at the first height comparison that flips.

**`GetStreams()` returns the pass-1 list**, which contains Deep-North streams that were never rendered
and omits the ones that were. The pass-2 list is discarded (`Pregenerate` / IL_0026 `pop`).

```csharp
// (WorldGenerator.FindStreamStartPoint / IL_0000–IL_0068)
for (int i = 0; i < iterations; i++) {
    float x = Random.Range(-10000f, 10000f);        // [R] float  — drawn unconditionally
    float y = Random.Range(-10000f, 10000f);        // [R] float  — drawn unconditionally
    float h = GetPregenerationHeight(x, y, riverPreGen);
    if (h > minHeight && h < maxHeight) { p = new Vector2(x, y); starth = h; return true; }
}
p = Vector2.zero; starth = 0f; return false;        // 100 failures == 200 draws
```

```csharp
// (WorldGenerator.FindStreamEndPoint / IL_0000–IL_008c)
float stepLen = (float)(((double)maxLength - (double)minLength) / (double)iterations);  // (200-80)/100
float cur = maxLength;                                                                  // 200
for (int i = 0; i < iterations; i++) {
    cur = (float)((double)cur - (double)stepLen);                 // first try 198.8, last 80
    float ang = Random.Range(0f, 6.2831854820251465f);            // [R] float  (== (float)(MathF.PI*2))
    Vector2 q = start + new Vector2(Mathf.Sin(ang), Mathf.Cos(ang)) * cur;
    float h = GetPregenerationHeight(q.x, q.y, riverPreGen);
    if (h > minHeight && h < maxHeight) { end = q; return true; }
}
end = Vector2.zero; return false;                                  // 100 failures == 100 draws
```

`Mathf.Sin`/`Cos` are `(float)Math.Sin((double)f)` (`Mathf.Sin` / IL_0002–IL_0008).
`Random.Range(float, float)` is **max-inclusive** (parameter names `minInclusive, maxInclusive`).

### 4.5 Draw accounting summary (per pregeneration)

| Stage | RNG stream | Draws |
|---|---|---|
| ctor | `InitState(m_seed)` | 7 int |
| `FindLakes` | — | none |
| `PlaceRivers` loop | `InitState(m_riverSeed)` | per iteration: 1 int per **successful** `FindRandomRiverEnd`, then 2 float per created river |
| `PlaceRivers` → `RenderRivers` | same stream | 1 float per point step per rendered river |
| `PlaceStreams(false)` | `InitState(m_streamSeed)` | per attempt: 2 float × start tries (1…100), then 1 float × end tries (1…100) if the start succeeded |
| `PlaceStreams(false)` → `RenderRivers` | same stream | 1 float per point step (all `Range(20f,20f)`) |
| `PlaceStreams(true)` | `InitState(m_streamSeed)` again | as above |
| `PlaceStreams(true)` → `RenderRivers` | same stream | as above |

**Important simplification:** each `RenderRivers` call is the *last* RNG consumer before its enclosing
method restores `Random.state`, so the draws it makes never influence anything else. For streams those
draws are `Range(20f, 20f)`, whose value is 20 regardless — so **whether `Range(a, a)` consumes a draw is
irrelevant to the output** (it is the KB's open question; it can be ignored). For rivers the draws do
matter, because they set the per-point radius.

### 4.6 `RenderRivers(List<River> rivers, RiverAdd addRule = All)`

```csharp
// (WorldGenerator.RenderRivers / IL_0000–IL_0202)
var pending = new Dictionary<Vector2i, List<RiverPoint>>();
foreach (River r in rivers) {                                    // list order
    if (addRule != All) {
        bool dn = IsDeepnorth(r.p0.x, r.p0.y);                   // p0 only
        if ((dn && addRule == SkipDeepNorth) || (!dn && addRule == OnlyDeepNorth)) continue;
    }
    float step = (float)((double)r.widthMin / 8.0);
    Vector2 dir  = (r.p1 - r.p0).normalized;
    Vector2 perp = new Vector2(-dir.y, dir.x);
    float len = Vector2.Distance(r.p0, r.p1);
    for (float s = 0f; s <= len; s = (float)((double)s + (double)step)) {
        float t = (float)((double)s / (double)r.curveWavelength);
        float off = (float)( Math.Sin((double)t)
                           * Math.Sin((double)t * 0.634119987487793)
                           * Math.Sin((double)t * 0.3341200053691864)
                           * (double)r.curveWidth );              // double sines, one conv.r4
        float rad = Random.Range(r.widthMin, r.widthMax);         // [R] float, EVERY step
        Vector2 p = r.p0 + dir * s + perp * off;
        AddRiverPoint(pending, p, rad, r);
    }
}
foreach (var kv in pending) {                                     // key order is irrelevant (per-key)
    if (m_riverPoints.TryGetValue(kv.Key, out var existing)) {
        var merged = new List<RiverPoint>(existing);              // existing points FIRST
        merged.AddRange(kv.Value);                                // new points appended
        m_riverPoints[kv.Key] = merged.ToArray();                 // a NEW array object
    } else {
        m_riverPoints.Add(kv.Key, kv.Value.ToArray());
    }
}
```

Note `RiverAdd` values: `All = 0`, `SkipDeepNorth = 1`, `OnlyDeepNorth = 2`.

Per-cell point **order** is: (rivers, in `m_rivers` order, by step, by grid scan) then (pass-1 streams)
then (pass-2 streams). `GetWeight` accumulates a float sum over that array, so the order is
numerically load-bearing (§6.9) — the port must reproduce it.

```csharp
// (WorldGenerator.AddRiverPoint(dict, p, r, river) / IL_0000–IL_005e)
Vector2i g = GetRiverGrid(p.x, p.y);
int n = Mathf.CeilToInt((float)((double)r / 64.0));
for (int y = g.y - n; y <= g.y + n; y++)           // OUTER = y
  for (int x = g.x - n; x <= g.x + n; x++)         // INNER = x
      if (InsideRiverGrid(new Vector2i(x, y), p, r))
          AddRiverPoint(dict, new Vector2i(x, y), p, r, river);   // appends to the cell's List

// (WorldGenerator.GetRiverGrid / IL_0000–IL_003f)   — same layout as zones
public Vector2i GetRiverGrid(float wx, float wy) => new Vector2i(
    Mathf.FloorToInt((float)(((double)wx + 32.0) / 64.0)),
    Mathf.FloorToInt((float)(((double)wy + 32.0) / 64.0)));

// (WorldGenerator.InsideRiverGrid / IL_0000–IL_0069)
public bool InsideRiverGrid(Vector2i grid, Vector2 p, float r) {
    Vector2 c = new Vector2((float)((double)grid.x * 64.0), (float)((double)grid.y * 64.0));
    Vector2 d = p - c;
    return Math.Abs(d.x) < (float)((double)r + 32.0)
        && Math.Abs(d.y) < (float)((double)r + 32.0);            // System.Math.Abs(float)
}
```

`Mathf.CeilToInt(f)` = `(int)Math.Ceiling((double)f)`, `Mathf.FloorToInt(f)` = `(int)Math.Floor((double)f)`
(`Mathf` / IL_0002–IL_0008 in each).

`RiverPoint`: `p`, `w = r`, `w2 = (float)((double)r * (double)r)` (`RiverPoint..ctor` / decompiled).

### 4.7 `GetRiverWeight` — the single-entry cache, and a real staleness hazard

```csharp
// (WorldGenerator.GetRiverWeight / IL_0000–...)
Vector2i g = GetRiverGrid(wx, wy);
m_riverCacheLock.EnterReadLock();
if (g == m_cachedRiverGrid) {
    if (m_cachedRiverPoints != null) { GetWeight(m_cachedRiverPoints, wx, wy, out weight, out width);
                                       m_riverCacheLock.ExitReadLock(); }
    else                            { weight = 0f; width = 0f; m_riverCacheLock.ExitReadLock(); }
    return;
}
m_riverCacheLock.ExitReadLock();
if (m_riverPoints.TryGetValue(g, out var pts)) {          // read OUTSIDE the lock
    GetWeight(pts, wx, wy, out weight, out width);
    m_riverCacheLock.EnterWriteLock(); m_cachedRiverGrid = g; m_cachedRiverPoints = pts;
    m_riverCacheLock.ExitWriteLock();
} else {
    m_riverCacheLock.EnterWriteLock(); m_cachedRiverGrid = g; m_cachedRiverPoints = null;
    m_riverCacheLock.ExitWriteLock();
    weight = 0f; width = 0f;
}
```

The cache starts at `(-999999, -999999)` so it can never false-hit on the first query.

**Hazard the port must decide about (analysis, from code):** `RenderRivers` replaces the *array object*
for a cell (or adds a new key), but never invalidates `m_cachedRiverGrid`/`m_cachedRiverPoints`.
During pregeneration this is reachable exactly once: the cache is first populated during
`PlaceStreams(false)` (via `AddRivers`), then `PlaceStreams(false)`'s `RenderRivers` rewrites
`m_riverPoints`; if the very first `GetRiverWeight` in `PlaceStreams(true)` lands in the same 64 m cell,
it uses the **pre-stream** array (or a stale `null`). The same applies to the first post-generation
`GetHeight` query after `PlaceStreams(true)`'s `RenderRivers`. Probability per world ≈ (64·64)/(20000·20000)
≈ 1×10⁻⁵, so over a billion-seed search it happens thousands of times. `FindLakes` and `PlaceRivers`
never call `AddRivers`, so no earlier staleness is possible.
**Refinement (reviewer):** 1×10⁻⁵ is an **upper bound**, not the rate. `RenderRivers` only replaces the
array for keys present in its local `pending` dictionary (`RenderRivers` / decompiled 564–577), so a
stale hit is harmless unless the cached cell is also one that the just-rendered pass wrote to. The
divergence therefore needs *both* "first query after the render lands in the cached cell" *and* "that
cell received points in this render" — still non-zero, still worth replicating rather than fixing.

Recommendation: **replicate the cache exactly** (a plain non-locking single-entry cache in a
single-threaded port reproduces the game's sequential behaviour), and record in the tool that
the first river query after each `RenderRivers` is cache-affected. For the post-generation query phase,
either replicate the end-of-pregeneration cache state or accept a ~1e-5 per-world divergence on a single
64 m cell — flag it, do not silently "fix" it.

```csharp
// (WorldGenerator.GetWeight / IL_0000–IL_00b5)
Vector2 q = new Vector2(wx, wy);
weight = 0f; width = 0f;
float acc = 0f, wsum = 0f;
for (int i = 0; i < points.Length; i++) {
    RiverPoint rp = points[i];
    float d2 = Vector2.SqrMagnitude(rp.p - q);              // FLOAT dx*dx + dy*dy
    if (d2 < rp.w2) {
        float d = (float)Math.Sqrt((double)d2);
        float w = (float)(1.0 - (double)d / (double)rp.w);
        if (w > weight) weight = w;
        acc  = (float)((double)acc  + (double)rp.w * (double)w);
        wsum = (float)((double)wsum + (double)w);
    }
}
if (wsum > 0f) width = (float)((double)acc / (double)wsum);
```

`weight` is a max (order-insensitive); `acc`/`wsum` are float accumulators (order-**sensitive**).

---

## 5. Helpers the rest of the tool needs

### 5.1 `GetTerrainDelta(Vector3 center, float radius, out float delta, out Vector3 slopeDirection)`

```csharp
// (WorldGenerator.GetTerrainDelta / IL_0000–IL_009a)   — consumes the AMBIENT Random stream
const int n = 10;
float hi = -999999f, lo = 999999f;
Vector3 pHi = center, pLo = center;
for (int i = 0; i < n; i++) {
    Vector2 r = Random.insideUnitCircle * radius;                 // [R] native, unknown draw count
    Vector3 q = center + new Vector3(r.x, 0f, r.y);
    float h = GetHeight(q.x, q.z);
    if (h < lo) { lo = h; pLo = q; }
    if (h > hi) { hi = h; pHi = q; }
}
delta = (float)((double)hi - (double)lo);
slopeDirection = Vector3.Normalize(pLo - pHi);
```

This is used by location placement, **not** by terrain generation, and it draws from whatever
`UnityEngine.Random` state the caller set up. `Random.insideUnitCircle` is
`GetRandomUnitCircle(out Vector2)`, an extern — **Unverified:** how many underlying draws it consumes and
by what rejection/polar method. Anything that needs `GetTerrainDelta` bit-exactly (location placement)
must dump it from the game, not re-derive it.

### 5.2 `GetBiomeArea` — two overloads, one of them buggy

```csharp
// (WorldGenerator.GetBiomeArea(Vector2s) / IL_0000–...)  — the one vanilla actually uses
private static readonly Vector2s[] s_biomeAreaOffsetsInt = {
    (-64,-64), (64,-64), (64,64), (-64,64), (-64,0), (64,0), (0,-64), (0,64) };
public Heightmap.BiomeArea GetBiomeArea(Vector2s point) {
    if (s_cachedBiomeAreas.TryGetValue(point, out var v)) return v;
    var b = GetBiome(point);                                   // cached in s_cachedBiomes
    bool same = true;
    for (int i = 0; i < 8; i++) same &= (b == GetBiome(point - s_biomeAreaOffsetsInt[i]));
    v = same ? BiomeArea.Median : BiomeArea.Edge;
    s_cachedBiomeAreas.Add(point, v);
    return v;
}
private Heightmap.Biome GetBiome(Vector2s p) {
    if (s_cachedBiomes.TryGetValue(p, out var v)) return v;
    v = GetBiome(p.x, p.y);            // short → float widening
    s_cachedBiomes.Add(p, v);
    return v;
}
```

`Vector2s` holds two `Int16`; subtraction is short arithmetic (no overflow inside a ±10 500 world).
Both caches are **static** `Dictionary`s, cleared in the ctor, and are **not thread safe** — irrelevant
for a single-threaded port, but note that an in-game mod must not touch them off the main thread.

```csharp
// (WorldGenerator.GetBiomeArea(Vector3) / decompiled 739–744) — BUGGY, no vanilla caller
// It tests the (64,0,0) offset TWICE and never (-64,0,0).
```

A port should implement the `Vector2s` overload (needed to match location placement) and either omit the
`Vector3` one or reproduce the bug verbatim — never "fix" it.

### 5.3 `GetForestFactor` / `InForest` — static, seedless

```csharp
// (WorldGenerator.GetForestFactor / IL_0000–...)
public static float GetForestFactor(Vector3 pos) => DUtils.Fbm(pos * 0.01f * 0.4f, 3, 1.6f, 0.7f);
public static bool  InForest(Vector3 pos)        => GetForestFactor(pos) < 1.15f;
```

The **float** `Fbm(Vector3, int, float, float)` overload, which forwards to `Fbm(Vector2(p.x, p.z), …)`
(§7.1). `pos * 0.01f * 0.4f` is two float scalar multiplies on the Vector3. There is **no seed input**,
so the forest-density pattern is identical in every world. Range is roughly [0, 1+0.7+0.49] = [0, 2.19];
`Minimap` also uses thresholds 0.8 (Meadows) and SmoothStep(1.1, 1.3) (Mistlands).

### 5.4 `GetNormal` / `GetAverageNormal` — both ignore `radius`

```csharp
// (WorldGenerator.GetNormal(Vector2, float radius = 1f) / IL_0000–IL_006d)
// ldarg.2 (radius) is NEVER loaded. The samples are always ±1 m.
public Vector3 GetNormal(Vector2 point, float radius = 1f) {
    float hr = GetHeight(point + Vector2.right);
    float hl = GetHeight(point + Vector2.left);
    float hu = GetHeight(point + Vector2.up);
    float hd = GetHeight(point + Vector2.down);
    return new Vector3(2f * (hr - hl), 4f, 2f * (hd - hu)).normalized;   // float ops
}
public Vector3 GetNormal(float wx, float wy, float radius = 1f) => GetNormal(new Vector2(wx, wy), radius);

// (WorldGenerator.GetAverageNormal / IL_0000–...)
public Vector3 GetAverageNormal(Vector2 point, float radius, float metersPerSample = 1.5f) {
    if (radius <= 1.5f) return GetNormal(point, radius);
    int n = Mathf.Min(Mathf.FloorToInt(radius / metersPerSample), 16);
    Vector3 sum = Vector3.zero;
    for (int i = 1; i < n + 1; i++) sum += GetNormal(point, radius * (float)i) / n;
    return sum.normalized;
}
```

Because `GetNormal` ignores its radius, `GetAverageNormal` averages **n identical vectors** and returns
the same direction as `GetNormal` (up to normalisation), at n× the cost. Reproduce as written; do not
"implement" the radius.

Each `GetNormal` costs 4 full `GetHeight` calls (each of which is a `GetBiome` plus a biome height), so a
slope-filtered seed search should special-case it.

### 5.5 `GetSeed`, accessors

`GetSeed()` returns `m_world.m_seed`. `GetLakes()` / `GetRivers()` / `GetStreams()` return the internal
lists (nothing in vanilla calls them). `CleanCachedRiverData()` clears `m_riverPoints`, `m_rivers`,
`m_streams` and sets `m_cachedRiverPoints = null` — but **not** `m_cachedRiverGrid`, so after a clean the
cache can still false-hit with a null array (which yields weight 0 — the same answer, since the dictionary
is empty). `WorldGenerator.Initialize(world)` calls it on the previous instance before constructing a new one.

---

## 6. Numerics: where a naive C# port diverges

The rule the shipped IL follows almost everywhere:

> **Arithmetic in double; truncate to float (`conv.r4`) at the end of each source statement; float
> literals are widened to double (`0.002f` → `0.0020000000949949026`); Perlin inputs are cast back
> to float inside `DUtils.PerlinNoise`.**

Write the port with explicit `(float)` casts at exactly the statement boundaries shown in §2–§4 and with
`double` temporaries inside each statement. On .NET (x64 SSE, ARM64) float arithmetic is genuinely
single-precision, so `float` locals reproduce Unity's Mono. The exceptions and traps:

**6.1 The mountain-cap divisor.** `0.099999994039535522`, i.e. the widening of the float `0.38f − 0.28f`
— *not* `(double)0.1f = 0.10000000149011612`. (`GetBaseHeight` / IL_04f1.) Relative error of a naive
`0.1f` is 7×10⁻⁸ on the lerp parameter; it flips results only near the threshold but it costs nothing to
get right.

**6.2 `DUtils.MathfLikeSmoothStep` returns a float-rounded double.** Its IL ends
`add; conv.r4; conv.r8; ret` (`DUtils.MathfLikeSmoothStep` / IL_0037–IL_003a). Port:
`return (double)(float)(to * t + from * (1.0 - t));`. Used by `CreateAshlandsGap`,
`CreateDeepNorthGap` and four times inside `GetAshlandsHeight`.

**6.3 The gap functions cast before the smoothstep.** `MathfLikeSmoothStep(0.0, 1.0, (double)(float)v)`
(`CreateAshlandsGap` / IL_0054–IL_0055; `CreateDeepNorthGap` / IL_0057–IL_0058).

**6.4 Two different `LerpStep`s.** `DUtils.LerpStep(float,float,float)` computes in double and returns a
float (`conv.r4` at IL_0010). `Utils.LerpStep(float,float,float)` is all-float
(`Utils.LerpStep` / IL_0000–IL_000c) and appears **exactly once**, at the 10490 m edge in
`GetBaseHeight`. `DUtils.LerpStep(double,double,double)` (pure double) appears once, in
`GetAshlandsHeight`.

**6.5 `DUtils.Lerp` short-circuits.** `t <= 0 → a`, `t >= 1 → b`, exactly; otherwise
`(float)((double)a*(1.0-(double)t) + (double)b*(double)t)` for the float overload and the same in double
for the double overload. Substituting `Mathf.Lerp` or `a + (b-a)*t` changes the result.

**6.6 `DUtils.Length(float,float)` vs `Vector2.magnitude`.** `DUtils.Length` squares and sums in
**double**, then `(float)Math.Sqrt` (`DUtils.Length(Single,Single)` / IL_0001–IL_0010).
`Vector2.magnitude` squares and sums in **float**, then widens for `Math.Sqrt` and truncates
(`Vector2.get_magnitude` / IL_000d–IL_001d). `IsAshlands`, `GetBiome`, `GetBaseHeight`,
`GetBiomeHeight`, `GetAshlandsHeight` (double overload), the gap functions use `DUtils.Length`;
`IsDeepnorth`, `FindLakes`, `Vector2.Distance` (rivers/lakes) and `Vector2.SqrMagnitude` (river weights)
use the float path. Keep them distinct.

**6.7 `Vector2.Distance` and `SqrMagnitude`.** `dx = a.x-b.x`, `dy = a.y-b.y` (float),
`dx*dx + dy*dy` (float), then `(float)Math.Sqrt((double)…)` for `Distance`
(`Vector2.Distance` / IL_001d–IL_002a, `Vector2.SqrMagnitude` / IL_001d–IL_0020).

**6.8 `Vector2.operator ==` is an epsilon test, not equality.**
`(dx*dx + dy*dy) < 9.9999994396249292E-11f` (`Vector2.op_Equality` / IL_0024–IL_0029). Used by
`FindClosest`, `FindRandomRiverEnd`, `HaveRiver(rivers, p0)` and `HaveRiver(rivers, p0, p1)` — i.e. it
decides which lakes may be linked. A port using `==` on structs/exact floats will occasionally build a
different river graph.

`Vector2.Normalize` divides by `magnitude` only when `magnitude > 1e-05f`, otherwise returns zero
(`Vector2.Normalize` / IL_0009–IL_002e).

**6.9 Float accumulation order.** `GetWeight` sums `acc`/`wsum` as floats over the cell's `RiverPoint[]`
in array order; `DUtils.Fbm` accumulates octaves as float (float overload) or double (double overload).
The array order is fixed by §4.6 and must be reproduced.

**6.10 Grouping differences that are not cosmetic.**
Meadows and DeepNorth: `over * ((1 − k) * 0.75)`. Plains: `(over * (1 − k)) * 0.75`
(`GetMeadowsHeight` / IL_0115–IL_012c vs `GetPlainsHeight` / IL_0112–IL_0129).
`GetBaseHeight` offsets: `wx + (100000.0 + off0)`. Per-biome offsets: `(wx + 100000.0) + off3`.

**6.11 Float-only operations (each a single rounding, so portable, but write them as float).**
`GetDeepNorthHeight`: `GetBaseHeight(...) + 0.1f` (`add` on floats, IL_000d).
`GetDeepNorthHeightPregenerate`: `h += 0.1f`, `wx*0.1f`, `wx*0.4f`, and the
`DUtils.PerlinNoise(float,float)` overload.
`GetPlainsHeight`: `h − 0.15f`.
`GetMistlandsHeight` and `GetMenuHeight`: the first `P(...) * P(...)` product.
`GetAshlandsHeight`: `100000f + m_offset3`.
`CreateAshlandsGap`/`CreateDeepNorthGap`/`DeepNorthWaveFade`/`GetAshlandsOceanGradient`:
`wy + ashlandsYOffset` / `wy + 4000f` inside the float `DUtils.Length`.
`GetDeepNorthHeight` mask: `(g + 1f) / 2f`, then `0.3f + g * 0.3f`.
`GetNormal`: `2f * (hr − hl)`, `2f * (hd − hu)`, `Vector3.normalized`.

**6.12 Unity wrappers that are just `System.Math`.** `Mathf.Sin/Cos` = `(float)Math.Sin/Cos((double)f)`;
`Mathf.Abs(float)` = `Math.Abs(float)`; `Mathf.Ceil` = `(float)Math.Ceiling((double)f)`;
`Mathf.FloorToInt`/`CeilToInt` = `(int)Math.Floor/Ceiling((double)f)`;
`Mathf.Clamp(float,float,float)` = `value < min ? min : value > max ? max : value`;
`Mathf.Min/Max(int,int)` are plain comparisons. (`Mathf.il.txt`.)

**6.13 Comparison semantics.** The compiler emits `ble.un` / `bge.un` / `bgt.un` for the *negated* form
of `>` / `<`, so NaN propagates as "condition false" in the source sense. `FindLakes`'
`!(magnitude > 10000f)` uses the **ordered** `bgt.s`, and `PlaceStreams`' mid-height test uses ordered
`blt`/`bgt`. In practice NaN never appears for finite in-world coordinates, but if the tool ever samples
outside ±10 500 it should keep the same forms.

**6.14 Transcendentals.** `Math.Sin`, `Math.Atan2`, `Math.Pow`, `Math.Sqrt`, `Math.Ceiling` come from the
platform math library. `Sqrt` is IEEE-exact. **Unverified:** whether Mono's `Sin`/`Atan2`/`Pow` on the
Unity player agree with .NET 10's to the last bit on every input. `Math.Pow` is used at exponents 1.5,
4.0, 2.0 and 1.3999999761581421 (Mistlands and AshLands only); `Sin`/`Atan2` in `WorldAngle` and
`RenderRivers`. A last-bit difference only matters within one ulp of a threshold; it is a real but very
low-rate divergence source and can only be settled by comparing against dumped in-game values.

**6.15 `UnityEngine.Random` is native.** `InitState`, `Range(float,float)`, `RandomRangeInt`,
`get_value` and `GetRandomUnitCircle` have **no managed body** (`Random.il.txt`). `Range(int,int)` has a
managed body that only forwards to `RandomRangeInt`, and — **corrected by the reviewer** — `get_state` /
`set_state` also have managed bodies; they forward to the externs `get_state_Injected` /
`set_state_Injected` (`Random.il.txt`, both "(no body)"). `InitState` carries
`[NativeMethod("SetSeed")]`, and the parameter names (Mono.Cecil over
`valheim_Data\Managed\UnityEngine.CoreModule.dll`, read by the reviewer) are
`Range(Int32 minInclusive, Int32 maxExclusive)` and `Range(Single minInclusive, Single maxInclusive)`,
which is the evidence for the half-open int range and the closed float range used in §1.1 and §4.4.
`Random.State` is four `Int32` fields `s0..s3` (same dump) — consistent with xorshift128, but the
seeding recurrence, the int range mapping and the float range mapping are **Unverified**. This is the
single largest risk to the port (see §8).

---

## 7. Everything the port needs from `DUtils` and `FastNoise`

### 7.1 `DUtils` (assembly_utils) — verified against `DUtils.il.txt`

```csharp
static float  Length(float x, float y)   => (float)Math.Sqrt((double)x*(double)x + (double)y*(double)y);
static double Length(double x, double y) => Math.Sqrt(x*x + y*y);

static double BlendOverlay(double a, double b) =>
    a < 0.5 ? 2.0*a*b : 1.0 - 2.0*(1.0-a)*(1.0-b);          // both branches computed, then selected

static float  Lerp(float a, float b, float t) =>
    t <= 0f ? a : t >= 1f ? b : (float)((double)a*(1.0-(double)t) + (double)b*(double)t);
static double Lerp(double a, double b, double t) =>
    t <= 0.0 ? a : t >= 1.0 ? b : a*(1.0-t) + b*t;

static float  LerpStep(float l, float h, float v)  =>
    (float)Clamp01(((double)v-(double)l) / ((double)h-(double)l));
static double LerpStep(double l, double h, double v) => Clamp01((v-l)/(h-l));

static float SmoothStep(float min, float max, float x) {
    float t = (float)Clamp01(((double)x-(double)min)/((double)max-(double)min));   // conv.r4 HERE
    return (float)((double)t*(double)t*(3.0 - 2.0*(double)t));                     // and here
}

static double MathfLikeSmoothStep(double from, double to, double t) {
    t = Clamp01(t);
    t = -2.0*t*t*t + 3.0*t*t;                                // exact op order: ((-2*t)*t)*t + (3*t)*t
    return (double)(float)(to*t + from*(1.0 - t));           // FLOAT-ROUNDED result
}

static double Clamp01(double v) => v > 1.0 ? 1.0 : (v < 0.0 ? 0.0 : v);   // ble.un/bge.un → NaN passes through

static float  Fbm(Vector3 p, int oct, float lac, float gain) => Fbm(new Vector2(p.x, p.z), oct, lac, gain);
static float  Fbm(Vector2 p, int oct, float lac, float gain) {           // FLOAT accumulator
    float sum = 0f, amp = 1f; Vector2 v = p;
    for (int i = 0; i < oct; i++) {
        sum = (float)((double)sum + (double)amp * (double)PerlinNoise(v.x, v.y));
        amp = (float)((double)amp * (double)gain);
        v   = v * lac;                                        // Vector2 op_Multiply, float per component
    }
    return sum;
}
static double Fbm(Vector2 p, int oct, double lac, double gain) {          // DOUBLE accumulator
    double sum = 0.0, amp = 1.0, x = (double)p.x, y = (double)p.y;
    for (int i = 0; i < oct; i++) {
        sum += amp * (double)PerlinNoise(x, y);               // float Perlin result widened
        amp *= gain; x *= lac; y *= lac;
    }
    return sum;
}

static double Remap(double v, double inLo, double inHi, double outLo, double outHi)
    => Lerp(outLo, outHi, InverseLerp(inLo, inHi, v));
static double InverseLerp(double a, double b, double v) => a == b ? 0.0 : Clamp01((v-a)/(b-a));

static float PerlinNoise(double x, double y) => Mathf.PerlinNoise((float)x, (float)y);   // conv.r4 x2
static float PerlinNoise(float  x, float  y) => Mathf.PerlinNoise(x, y);
```

Which `Fbm` overload is called where (verified by the IL call targets):
`GetForestFactor` → `Fbm(Vector3, int, float, float)` (float);
`GetDeepNorthHeight` mask and `GetAshlandsHeight` lava → `Fbm(Vector2, int, double, double)` (double),
both with octaves 3, lacunarity 2.0, gain 0.5.

`Utils.LerpStep(float,float,float)` = `Utils.Clamp01((v-l)/(h-l))` with float operands, and
`Utils.Clamp01(float)` is the float ternary (`Utils.il.txt`).

**`Mathf.PerlinNoise(float, float)` has no managed body** (`Mathf.il.txt`: "(no body)"). It is
`[FreeFunction("PerlinNoise::NoiseNormalized")]`. **Unverified:** the gradient set, the fade curve and
the 0..1 normalisation. The KB records partial evidence (Ken Perlin's reference permutation table
151,160,137,91,90,15,… present as 512 int32 values at file offset 0x1BE0BC0 of `UnityPlayer.dll`). This
must be validated against dumped in-game samples before the tool is trusted (§8).

### 7.2 `FastNoise` — only three entry points are reachable

Verified by grepping the WorldGenerator IL for `FastNoise::` calls: besides the constructor and the four
setters, only

| Call site | Method | Arguments |
|---|---|---|
| `GetAshlandsHeight` / IL_0162 | `GetCellular(double, double)` | `(x*freq, y*freq)`, freq = 0.33·2ⁱ, 5 octaves (2 if cheap) |
| `GetAshlandsHeight` / IL_0306 | `GetCellular(double, double)` | `(x*freq, y*freq)`, freq = 8·2ʲ, 3 octaves (2 if cheap) |
| `GetAshlandsHeight` / IL_03a0 | `GetSimplexFractal(double, double)` | `(x*0.075, y*0.075)` |

Nothing else in `WorldGenerator` touches FastNoise. `FastNoise` is a double-precision C# port
(`Float2`/`Float3` hold doubles) — **do not substitute upstream FastNoise/FastNoiseLite**, whose floats
and tables differ.

```csharp
// (FastNoise.GetCellular(double,double) / decompiled 2385–2395)
public double GetCellular(double x, double y) {
    x *= m_frequency;  y *= m_frequency;                 // 0.01
    return (uint)m_cellularReturnType <= 2u ? SingleCellular(x, y) : SingleCellular2Edge(x, y);
}

// (FastNoise.SingleCellular(double,double) / decompiled 2397–2481) — Euclidean branch (the default case)
int xi = FastRound(x), yi = FastRound(y);
double best = 999999.0; int bx = 0, by = 0;
for (int i = xi-1; i <= xi+1; i++)
  for (int j = yi-1; j <= yi+1; j++) {
      Float2 c = CELL_2D[Hash2D(m_seed, i, j) & 0xFF];
      double dx = (double)i - x + c.x * (double)m_cellularJitter;   // (double)0.45f = 0.44999998807907104
      double dy = (double)j - y + c.y * (double)m_cellularJitter;
      double d  = dx*dx + dy*dy;
      if (d < best) { best = d; bx = i; by = j; }
  }
// m_cellularReturnType == Distance (2):
return best;                                             // the SQUARED euclidean distance
```

Note the loop nesting: `i` over x is the **outer** loop, `j` over y the inner (this matters only for
tie-breaking, which `<` resolves in favour of the first visited cell).

```csharp
// (FastNoise.GetSimplexFractal(double,double) / decompiled 1768–1794) with m_fractalType == FBM
public double GetSimplexFractal(double x, double y) {
    x *= m_frequency; y *= m_frequency;                                   // 0.01
    int seed = m_seed;                                                    // 0
    double sum = SingleSimplex(seed, x, y), amp = 1.0;
    for (int i = 1; i < m_octaves; i++) {                                 // m_octaves == 2 → one extra
        x *= m_lacunarity; y *= m_lacunarity;                             // 2.0
        amp *= m_gain;                                                    // 0.5
        sum += SingleSimplex(++seed, x, y) * amp;                         // seed 1 for octave 2
    }
    return sum * m_fractalBounding;                                       // 1/1.5
}

// (FastNoise.SingleSimplex(int,double,double) / decompiled 1831–1891) — classic 2D simplex
double F2 = 0.3660254037844386, G2 = 0.21132486540518713, G2x2 = 0.42264973081037427;
// …skew, unskew, three corner contributions with (0.5 - x*x - y*y) falloff, each n = t*t*t*t*grad…
return 50.0 * (n0 + n1 + n2);
```

Helper functions the port needs verbatim (`FastNoise` / decompiled 819–948):

```csharp
static int FastFloor(double f) => f >= 0.0 ? (int)f : (int)f - 1;
static int FastRound(double f) => f >= 0.0 ? (int)(f + 0.5) : (int)(f - 0.5);
static int Hash2D(int seed, int x, int y) {
    int n = seed; n ^= 1619*x; n ^= 31337*y; n = n*n*n*60493; return (n >> 13) ^ n;   // unchecked
}
static double GradCoord2D(int seed, int x, int y, double xd, double yd) {
    int n = seed; n ^= 1619*x; n ^= 31337*y; n = n*n*n*60493; n = (n >> 13) ^ n;
    Float2 g = GRAD_2D[n & 7];
    return xd*g.x + yd*g.y;
}
static readonly Float2[] GRAD_2D = { (-1,-1),(1,-1),(-1,1),(1,1),(0,-1),(-1,0),(0,1),(1,0) };
```

All the integer maths is 32-bit and **must be unchecked** (`n*n*n*60493` overflows constantly);
`n >> 13` is an **arithmetic** shift on a signed int.

`CELL_2D` is a 256-entry `Float2[]` of **float-valued doubles** (e.g. the first entry is
`(-0.2700222134590149, -0.9628540873527527)`, the last `(-0.7743120193481445, -0.6328039765357971)`) —
these are the upstream FastNoise `float` constants widened. Copy the table verbatim from
`scratchpad\decomp\FastNoise.cs` lines 150–409 (or re-extract it from `assembly_utils.dll`); do not
regenerate it from the unit-circle formula, and do not round-trip it through `float`.

`m_cellularJitter` is declared `float` (0.45f) and is widened at every use, so the effective constant is
`(double)0.45f` = **`0.44999998807907104`** (exactly 0.449999988079071044921875). Corrected by the
reviewer: the literal `0.449999988079071` given in the first draft is a different, 1-ulp-lower double.

---

## 8. Port checklist and validation plan

1. **Implement in this order:** `DUtils` → `FastNoise` (2D cellular + 2D simplex fractal only) →
   `GetStableHashCode` → `Mathf.PerlinNoise` replacement → `UnityEngine.Random` replacement →
   `GetBaseHeight`/`WorldAngle`/`GetBiome` → per-biome heights → lakes/rivers/streams → `GetHeight`.
2. **Biome-only mode is cheap and safe.** `GetBiome` needs only `m_offset0/1/2/4` (four of the five
   offsets — `m_offset3` is detail noise and is not read on this path), the version constants
   and `Mathf.PerlinNoise`. It needs **no** RNG replacement if those offsets are dumped from the game,
   and no rivers at all. A seed search filtered on biomes alone (distances, biome presence, biome area
   fractions) can ship before the RNG is solved.
   **One trap (reviewer):** this only holds while `waterAlwaysOcean` stays `false`. With it `true`,
   `GetBiome` calls `GetHeight(wx, wy)` (`GetBiome` / IL_004e), which is the full height path — rivers,
   `m_offset3` and all. It does not recurse infinitely, because `GetHeight` calls `GetBiome` with the
   default `waterAlwaysOcean = false` (`GetHeight` / decompiled 998–1003), but a biome-only build must
   simply not expose the flag.
3. **Height mode needs the RNG.** `m_offset3`, plus `m_riverSeed`/`m_streamSeed` and an exact
   `UnityEngine.Random` (`InitState`, `Range(int,int)`, `Range(float,float)`). The alternative is to have
   the BepInEx dumper emit `m_riverPoints` (or `GetRivers()`/`GetLakes()` plus the per-point radii) per
   seed, which does not scale to a search but is perfect for validation.
4. **Validation data, in increasing strength:**
   a. dump `m_offset0..4`, `m_riverSeed`, `m_streamSeed` by reflection for a set of known seeds — this
      alone proves or disproves the RNG replacement in one step;
   b. dump `GetBaseHeight` (private) and `GetBiome` on a grid;
   c. dump `GetHeight`/`GetBiomeHeight` + `mask` on a grid;
   d. the user's world `asdasdasd` (seed text `MWd8eV6svz`, int seed −1772362158, worldGenVersion 2)
      has a minimap cache at `…\worlds_local\asdasdasd\cacheMinimapHeight` — half-float, so compare with
      a tolerance, and remember `Minimap` samples pixel centres
      `(i − size/2)*pixelSize + pixelSize/2` with code defaults 256/64 (prefab values unverified).
      **Read-only; never write there.**
5. **Search-space note for the seed tool.** Seed *text* is hashed to a 32-bit int
   (`World.m_seed = seedName.GetStableHashCode()`, 0 for empty), so there are at most **2³² ≈ 4.29×10⁹
   distinct worlds** per world-gen version, not 8.53×10¹⁷. The user's count of
   853 058 371 866 181 866 for 1–10 characters over a 62-symbol alphabet is arithmetically correct
   (Σ 62ⁿ, n = 1…10 = 853 058 371 866 181 866 — recomputed by the reviewer) but it counts *seed
   strings*, which collide heavily onto the 2³² seed space. The right search domain is the **int seed**,
   with a reverse map to a short printable string afterwards.
   Two facts for that reverse map (added by the reviewer):
   `World.GenerateSeed()` — what the game itself puts in the box — builds a **10-character** string from
   a **59-symbol** alphabet, `abcdefghijklmnpqrstuvwxyzABCDEFGHIJKLMNPQRSTUVWXYZ023456789`, i.e. the
   ambiguous `o`, `O` and `1` are excluded (`World.GenerateSeed` / decompiled 163–171; Σ 59ⁿ, n = 1…10 =
   519 929 111 116 169 700). And the text the player types is passed to `new World(name, seed)`
   unfiltered and unvalidated — there is no character or length check in code
   (`FejdStartup.OnNewWorldDone` / decompiled 1489–1502), so any string the field accepts is a legal
   seed. **Unverified:** the input field's character limit, which is a prefab property and not visible
   in code.

---

## 9. Unverified items (and why)

- **`Mathf.PerlinNoise`** — `extern`, no managed body. Gradient set, fade curve and normalisation
  unknown; only the presence of Perlin's reference permutation table in `UnityPlayer.dll` is evidence.
  Must be validated against dumped samples.
- **`UnityEngine.Random`** — `InitState`, `RandomRangeInt`, `Range(float,float)`,
  `GetRandomUnitCircle` are all `extern`. `State` is four int32s (consistent with xorshift128), but the
  seeding, the int mapping and the float mapping are unverified. Whether `Range(a, a)` consumes a draw
  does not matter for this spec (§4.5).
- **Last-bit agreement of `Math.Sin`/`Atan2`/`Pow`** between Unity's Mono and .NET 10 (§6.14).
- **`Random.insideUnitCircle` draw count/method** — affects `GetTerrainDelta` only (§5.1).
- **`Mathf.PerlinNoise` purity** — assumed (it has no seed API and takes only its coordinates).
- **Prefab/scene values** used by consumers, not by the generator: `ZoneSystem.m_waterLevel` (code
  default 30), `Minimap.m_textureSize`/`m_pixelSize` (code defaults 256/64), `Heightmap.m_width`/`m_scale`
  (code defaults 32/1).
- **No numerical output in this document was produced by running the game.** Every formula comes from
  reading decompiled C# and raw IL of build 1.0.15 / `assembly_valheim.dll`
  sha256 59f53fb5…33adb1. (Still true after review: the reviewer ran arithmetic checks on constants and
  the seed hash, but nothing against the running game.)
- **`Mathf.PerlinNoise` for NaN / non-finite arguments** — extern, so `GetBaseHeight` and therefore
  `GetBiome` are unpredictable for NaN coordinates (§2.3). Keep sample coordinates finite.

---

## 10. Open questions (added by the reviewer)

1. **Does the `Mathf.PerlinNoise` output range really start at 0?** Two derived statements in this
   document depend on it and on nothing else: the Deep North `mask.g` range [0.45, 0.7125] (§3.6) and
   the `GetForestFactor` range [0, 2.19] (§5.3). If the extern can return slightly negative values, or
   is not normalised the way "NoiseNormalized" suggests, both bounds move. The same dumper that settles
   the noise function settles this: log `Mathf.PerlinNoise` min/max over a dense grid.
2. **What should the tool's "is there lava at (x, z)?" mean?** There are two different in-game answers
   and they disagree near biome boundaries: `ZoneSystem.IsLavaPreHeightmap(pos, 0.6)` =
   `GetBiome == AshLands && GetBiomeHeight(AshLands, …).mask.a > 0.6` (`ZoneSystem` / decompiled
   2869–2877), which is what world generation itself uses; and `Heightmap.IsLava(pos, 0.6)`, which also
   requires the 64 m heightmap patch to be single-biome (`Heightmap.IsLava` / 958–969, `IsBiomeEdge` /
   520–526). A seed search should use the first and say so; only a "what will I see standing there"
   feature needs the second.
3. **Which cached-river-cache state should the query phase start from?** §4.7 recommends replicating the
   cache. Whether the post-pregeneration cache state (grid + array) must also be reproduced before the
   first user query, or whether the tool may start cold, was not settled here; it is a ≤1e-5 per-world,
   single-cell effect and needs a dumped `GetHeight` comparison to close.
4. **Seed-string input limits.** `FejdStartup` applies no validation in code (§8.5); the input field's
   character limit and whether the game trims whitespace are prefab/UI properties. A reverse map that
   emits strings the player cannot type back in would be worse than useless — confirm the limit from a
   dumper or by typing into the game before shipping the reverse map.

---

## 11. Verification

Independent adversarial check of this document, 2026-09-22, against the same build
(`check-game-version.ps1` re-run: exit 0, `assembly_valheim.dll` sha256 `59f53fb5…33adb1`, Valheim
1.0.15 / network 40 / Steam build 25390630). Method: every constant, ordering, RNG consumption point,
float/double claim and determinism claim was re-derived from `scratchpad\decomp\*.cs` and
`scratchpad\probe\*.il.txt` rather than trusted from the quotes here; quoted excerpts were diffed
against the real files; numeric claims were recomputed.

### Checked and confirmed

- **Constructor.** Field initialisers → `Object::.ctor` → `m_world` → cache clears → `m_version` →
  `VersionSetup` → save state → `InitState(m_world.m_seed)` → FastNoise setup → **seven draws in the
  order off0, off1, off2, off3, riverSeed, streamSeed, off4** → `Pregenerate` (non-menu) → restore
  state. Verified instruction by instruction (`WorldGenerator..ctor` / IL_0000–IL_01c3); the draw IL
  offsets quoted in §1.1 are exact, as are `ldc.i4 -2147483648 / 2147483647` for the two seeds.
- **`Random.Range` semantics.** Parameter names read with Mono.Cecil from
  `UnityEngine.CoreModule.dll`: `Range(Int32 minInclusive, Int32 maxExclusive)`,
  `Range(Single minInclusive, Single maxInclusive)`; `InitState` is `[NativeMethod("SetSeed")]`;
  `Random.State` = `s0..s3` Int32. So `m_offset0..4 ∈ [−10000, 9999]` and `int.MaxValue` is never drawn.
- **`Mathf.PerlinNoise`** is `[FreeFunction("PerlinNoise::NoiseNormalized")]` with no managed body
  (Cecil + `Mathf.il.txt`) — the §7.1/§9 framing is right.
- **Mountain-cap divisor** `0.099999994039535522` (`GetBaseHeight` / IL_04f1 `ldc.r8`, `div` at IL_04fa)
  and the single `Utils::LerpStep` call at IL_04b3 — both confirmed in IL. Recomputed:
  `(double)0.38f − (double)0.28f` and `(float)(0.38f − 0.28f)` both give exactly this value, and it is
  not `(double)0.1f = 0.10000000149011612`.
- **Every other ≥8-decimal literal in this document** was extracted mechanically and checked twice: that
  it is exactly representable as a `float` (where it claims to be a widened float constant) and that it
  occurs verbatim in the decompiled sources or the IL dumps. All pass except the ones that are
  legitimately not widened floats (`0.0078125` — the derived ulp; `0.6666666666666666` = 1/1.5; the
  simplex F2/G2 constants and the `Vector2` epsilon, which are genuine doubles) and the one error listed
  below.
- **`GetBiome`** order, thresholds, offsets and coordinate shape (`ldfld off; conv.r8; ldarg; conv.r8;
  add; conv.r4; conv.r8; ldc.r8 0.0010000000474974513; mul`), and the band terms
  `add; conv.r4; ble.un` — all confirmed in IL. Offset→biome mapping (off0 Swamp, off1 Plains,
  off2 BlackForest, off4 Mistlands; off0/off1 for the base-height X/Y) confirmed.
- **All per-biome height functions** re-read line by line against `WorldGenerator.cs` 1073–1400: the
  Meadows-vs-Plains grouping difference, the Plains float subtraction, the DeepNorth `+0.1f` float add
  and its `k = Clamp01(b/0.4)` using the shifted base, the DeepNorth pre-generation float-coordinate
  `PerlinNoise(float,float)` overload and `*1.2`, `Mathf.Max(0f, h−0.4)` there versus the unclamped
  Mountain version, AshLands pre-generation putting `AddRivers` last, Mistlands/Menu's float first
  product, the Mistlands `Pow(M,1.5)`/terracing, the Mountain tilt term, and `GetMarshHeight` having no
  offset at all.
- **`GetAshlandsHeight`** matched statement by statement, including the dead `num11` block
  (computed at decompiled 1244–1245, never read again), `Mathf.Clamp` clamping, and
  `DUtils.LerpStep(double,…)` being used exactly once.
- **Lakes, rivers, streams.** `FindLakes` grid (157×157 = 24 649 points, last value 9968 — recomputed),
  `MergePoints` swap-remove and running midpoint, `FindRandomRiverEnd` drawing exactly one int only on
  success, `PlaceRivers` not removing a successful start lake, `PlaceStreams` reseeding from the same
  `m_streamSeed` for both passes and discarding the second list (`Pregenerate` / IL_0026 `pop`),
  `RenderRivers` add-rules (`All=0, SkipDeepNorth=1, OnlyDeepNorth=2`), the `widthMin/8` step, the
  triple-sine offset, and the per-cell merge order. `Random.Range(0f, MathF.PI*2f)`: recomputed,
  `(float)(MathF.PI*2f) = 6.2831854820251465`.
- **Numerics.** `DUtils.MathfLikeSmoothStep` really ends `add; conv.r4; conv.r8; ret` (IL_0037–IL_003a);
  `DUtils.LerpStep(float…)` ends `Clamp01; conv.r4`; `DUtils.Length(float,float)` squares in double;
  `Vector2.get_magnitude`/`Distance`/`SqrMagnitude` are float-interior; `Vector2.op_Equality` is
  `dx*dx+dy*dy < 9.9999994396249292E-11`; `Vector2.Normalize` guards on `magnitude > 1e-05f`;
  `Utils.LerpStep`/`Utils.Clamp01` are all-float. All as documented.
- **FastNoise.** Enum values (`Cellular = 6`, `Euclidean = 0`, `Distance = 2`), the field defaults,
  `CalculateFractalBounding` giving 1/1.5 at octaves 2 / gain 0.5, `GetCellular` multiplying by
  `m_frequency` and returning the **squared** Euclidean distance, the i-outer/j-inner cell scan,
  `SingleSimplexFractalFBM`'s `++seed` per octave, `FastFloor`/`FastRound`/`Hash2D`/`GradCoord2D`, and
  the `CELL_2D` first/last entries. Only three FastNoise call sites exist in `WorldGenerator`
  (IL_0162, IL_0306, IL_03a0) — confirmed by grepping the IL.
- **Dead code.** `GetEdgeHeight` has no call site anywhere in `WorldGenerator`'s IL and is private, so
  it is genuinely unreachable; the `GetBiomeArea(Vector3)` overload really does test `(64,0,0)` twice
  and never `(−64,0,0)` (decompiled 739–744). `Vector2s` is two `Int16` with wrapping `sub; conv.i2`
  subtraction (Cecil dump of `assembly_utils.dll`), so the ±10 500 world never overflows it.
- **Consumers.** `Heightmap.Biome` values, `BiomeArea` values, `ZoneSystem.m_waterLevel = 30f`,
  `ZoneSystem.IsLavaPreHeightmap`, the location `m_minimumVegetation`/`m_maximumVegetation` test
  (decompiled 2020–2031), `Minimap.GenerateWorldMap`'s pixel-centre sampling and `GetMaskColor`'s
  AshLands branch.
- **Ground truth.** `"MWd8eV6svz".GetStableHashCode()` recomputed from `StringExtensionMethods`
  (decompiled 62–76) = **−1772362158**, matching the world `asdasdasd` used for validation in §8.4d.
  The user's seed-string count Σ 62ⁿ (n = 1…10) recomputed = 853 058 371 866 181 866.

### Corrected in place

1. **§1.4 / §7.2 — `m_cellularJitter`.** `0.449999988079071` → `0.44999998807907104`. The first value is
   a different double, one ulp low; `(double)0.45f` is exactly 0.449999988079071044921875. This was the
   one literal in the document that would have silently biased every AshLands cellular sample.
2. **§3.6 — Deep North `mask.g` range.** [0.3, 0.6] → **[0.45, 0.7125]**, typical ≈ 0.58. The old figure
   assumed an fbm in [−1, 1]; `DUtils.Fbm` sums Perlin values in ≈[0, 1] with amps 1/0.5/0.25, so
   `0.3 + 0.3·((f+1)/2)` cannot go below 0.45. This flips the expected answer to
   `Heightmap.IsCultivated`'s `> 0.5` test for Deep North ground.
3. **§3.3 — AshLands ridge direction.** "pulled 1200 m north of it" → **south**: the ridge circle centre
   moves from (0, +4000) to (0, +2800), so at x = 0 the crest is at z ≈ −9200 against a biome boundary
   at z ≈ −8000.
4. **§2.3 — NaN behaviour.** "Only Meadows/BlackForest" → with a NaN coordinate `dist` is NaN, so
   BlackForest (both branches), Swamp, Mistlands, Plains and DeepNorth are all unreachable; what remains
   is Ocean/Mountain/Meadows depending on the extern Perlin's NaN result, which is unverifiable.
5. **§3.6 — `Heightmap.IsLava`.** Restated with its full condition (`GetBiome == AshLands &&
   !IsBiomeEdge() && mask > lavaValue`), and `GetVegetationMask`'s (−0.5, −0.5) sample shift added.
6. **§3.6 — `HeightmapBuilder.Build`.** "bilinearly" → the two blend weights are
   `DUtils.SmoothStep(0, 1, k/m_width)`, applied to both the heights and the masks.
7. **§6.15 — `UnityEngine.Random` bodies.** `get_state`/`set_state` do have managed bodies (they forward
   to extern `*_Injected`); the parameter-name evidence for the range semantics was added.
8. **Header — "two corrections to the knowledge base".** Both are already recorded in
   `valheim-worldgen/references/world-generator.md` (stamped 2026-09-22); reworded so the next reader
   does not re-apply them.
9. **§0 — `GetBiomeArea` signature.** `(short x, short y)` → `(Vector2s point)`.
10. **§4.7 — staleness rate.** 1e-5 marked as an upper bound: a stale hit only diverges if the cached
    cell is also one the just-finished `RenderRivers` wrote to.
11. **§8.2 — biome-only mode.** Clarified that four offsets are used, and added the `waterAlwaysOcean`
    trap (it pulls in the whole height path; it does not recurse, because `GetHeight` calls `GetBiome`
    with the default flag).
12. **§8.5 — seed strings.** Added the game's own alphabet (59 symbols, 10 characters, no `o`/`O`/`1`)
    and the fact that typed seeds are unvalidated in code.

### Still unverified after this review

Everything in §9 stands, unchanged and for the same reasons: `Mathf.PerlinNoise` (gradients, fade curve,
normalisation, NaN behaviour, purity), `UnityEngine.Random` (seeding recurrence, int and float range
mappings, `insideUnitCircle`), last-bit agreement of `Math.Sin`/`Atan2`/`Pow` between Unity's Mono and
.NET 10, and the prefab/scene values. Nothing in this document has been compared against the running
game; §10 lists what a dumper must settle before any search result is trusted. Note that correction 2
above is itself derived from the unverified Perlin range — it holds if Perlin is in [0, 1], which is the
same assumption the rest of the document already makes.

*Checked by an independent reviewer.*
