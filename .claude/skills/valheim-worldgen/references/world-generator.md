# WorldGenerator: seed → biomes, terrain height, rivers

> Researched 2026-09-22 against Valheim 1.0.15 by decompiling the shipped assemblies; every claim was then checked by an independent refute-by-default verifier, who corrected errors in place. Items marked **Unverified:** could not be settled from code. Re-check with `valheim-modding/scripts/decompile.ps1` after a game update.

Contents: 1. Lifecycle and threads · 2. The seed · 3. Initialization · 4. Base height ·
5. Biome determination (5.1 straight biome edges) · 6. Terrain height · 7. Lakes, rivers, streams ·
8. How the game uses it · 9. Determinism, the native functions, and everything measured against the
game · 10. Modder notes and pitfalls · Unverified (and why)

Build this was checked against: Valheim Steam build on Unity 6000.0.75. `WorldGenerator`, `World`, `AltBiomeWorldData`, `BiomeSector` and `HeightmapBuilder` are in `assembly_valheim.dll`. `DUtils`, `FastNoise`, `Utils` and `StringExtensionMethods` are in `assembly_utils.dll`. All of it was decompiled with ILSpy, and the key float and double points were checked against the raw IL with Mono.Cecil.

## Summary

- **The seed is one `int`.** `World.m_seed` is set to `seedName.GetStableHashCode()`, or to 0 when the seed name is empty. It is saved in the `.fwl` and sent to clients. `WorldGenerator` turns it into 5 integer noise offsets and 2 RNG seeds through `UnityEngine.Random`. `m_worldGenVersion` is the only other input (new worlds use 2).
- **Biome = f(seed, version, x, z).** It is decided by distance bands from (0,0) plus low-frequency Perlin masks, tested in this order: menu → AshLands → Ocean → DeepNorth → Mountain → Swamp → Mistlands → Plains → BlackForest → BlackForest (beyond about 5 km) → Meadows. Rivers do not affect the biome.
- **Height = per-biome function × 200** (world metres), and sea level is at 30 m. Most biome height functions also run `AddRivers`, so **height depends on rivers**. Rivers come from a pre-generation pass (lakes → rivers → streams) that uses only the seed.
- **Nothing about terrain shape is stored in the save.** Every peer (server and each client) runs `WorldGenerator` locally from `m_seed` and `m_worldGenVersion`.
- **In-game ground height is not the same as `WorldGenerator.GetHeight`.** The terrain builder blends the height functions of the four heightmap-corner biomes, and player or location terrain edits are applied on top. The gameplay biome at a point comes from a 12 m sector grid (`AltBiomeWorldData`), not from a direct `GetBiome` call.
- **Reproducing this outside the game** means porting the pure C# code exactly (it uses a hand-written mix of double and float arithmetic, and Mono keeps parts of it at double precision) and **re-implementing two native Unity functions**: `Mathf.PerlinNoise` and `UnityEngine.Random`. Neither can be read from managed code, but both are now **solved and confirmed against the running game**, and the whole pipeline is reproduced bit-exactly offline (§9; the tool is the **seedlab** skill).

---

## 1. Lifecycle: who creates it, when, and on which thread

| Caller | World passed | When |
|---|---|---|
| `FejdStartup.Awake` | `World.GetMenuWorld()` | Main menu background (FejdStartup.Awake / decompiled) |
| `ZNet.Awake` (only when `m_isServer`) | the loaded world, or `World.GetDevWorld()` | Host or dedicated server start (ZNet.Awake / decompiled) |
| `ZNet.RPC_PeerInfo` (client branch) | a new `World` built from network data: name, **seed (int)**, seedName, uid, **worldGenVersion** | Client joining a server (ZNet.RPC_PeerInfo / decompiled) |
| `TestSceneSetup.Awake` | `World.GetMenuWorld()` | Test scene only; not used in normal play (TestSceneSetup.Awake / decompiled) |

- `ZNet.Awake` calls `WorldGenerator.Deitialize()` first (the misspelling is the real method name), which sets `instance = null`. **On a client, `WorldGenerator.instance` stays null until `RPC_PeerInfo` arrives.** Mods must null-check it. (ZNet.Awake / decompiled)
- `Initialize(world)` calls `CleanCachedRiverData()` on the old instance and then `new WorldGenerator(world)`. (WorldGenerator.Initialize / decompiled)
- Both sides then call `AltBiomeWorldData.VerifyBiomeData(world)`. The client calls it immediately after `Initialize` in `ZNet.RPC_PeerInfo`. The server calls it later, in `ZNet.ServerLoadWorld` (run from `ZNet.Start` after the world DB is loaded), while its `Initialize` ran earlier in `ZNet.Awake`. That call deletes the cache file, then runs `GetBiome` and `GetBiomeHeight` on a 2048×2048 grid and builds the sector data (§8). (ZNet.ServerLoadWorld, ZNet.RPC_PeerInfo, AltBiomeWorldData.VerifyBiomeData / decompiled)
- **A mod that swaps `WorldGenerator.instance` must not blindly restore its saved value.** Those four rows are the only callers in `assembly_valheim.dll` (`find-usages.ps1 -Needle "WorldGenerator::Initialize"`, 2026-09-23), and `ZNet.Awake` is one of them: if a world starts loading while the mod holds the static, the game has already installed the real world's generator, and putting the saved menu world back over it makes every heightmap build from the wrong seed. Restore only while the object you installed is still `WorldGenerator.instance` (reference equality), and if a world has loaded in the meantime, `Initialize(ZNet.World)` — the generator the game itself would have. Also note the saved generator OBJECT cannot be reinstated: `Initialize` runs `CleanCachedRiverData()` on the outgoing instance and `Pregenerate()` only ever runs from the constructor, so keep the `World` and rebuild. (`tools\SeedLab.Dumper\src\ModeWorldGen.cs`, 2026-09-23)
- **Threads:** `HeightmapBuilder` runs `Build()` on its own `System.Threading.Thread`, and that calls `GetBiome`, `GetBiomeHeight` and `GetBiomeSector` (HeightmapBuilder ctor/BuildThread/Build / decompiled). **A Harmony patch on these methods runs off the main thread**, so it must be thread-safe and must not call Unity APIs that are main-thread only. Only the single-entry river cache (`m_cachedRiverGrid`/`m_cachedRiverPoints`) is guarded by `m_riverCacheLock` (a `ReaderWriterLockSlim`). `m_riverPoints.TryGetValue` runs outside the lock; the dictionary is written only by `Pregenerate` and cleared by `CleanCachedRiverData` (WorldGenerator.GetRiverWeight / decompiled).

## 2. The seed

```csharp
// World(string name, string seed)  (World ctor / decompiled)
m_seedName = seed;
m_seed = (m_seedName != "") ? m_seedName.GetStableHashCode() : 0;
m_worldGenVersion = 2;
```
```csharp
// StringExtensionMethods.GetStableHashCode (assembly_utils / decompiled): djb2-style, two interleaved lanes
int num = 5381, num2 = num;
for (int i = 0; i < str.Length && str[i] != 0; i += 2) {
    num = ((num << 5) + num) ^ str[i];
    if (i == str.Length - 1 || str[i + 1] == '\0') break;
    num2 = ((num2 << 5) + num2) ^ str[i + 1];
}
return num + num2 * 1566083941;          // unchecked int overflow
```
Sample outputs, computed by running the decompiled code under .NET: `"a"` → 372029373, `"abc"` → 1099313834, `"HHcLC5acQt"` → 298112588. Note that `""` hashes to 371857150, but the `World` constructor forces the seed to **0** for an empty seed name.

- **New seed names:** `World.GenerateSeed()` picks 10 characters with unseeded `UnityEngine.Random.Range` from a 59-character alphabet that has no `o`, `O` or `1` (`"abcdefghijklmnpqrstuvwxyzABCDEFGHIJKLMNPQRSTUVWXYZ023456789"`). (World.GenerateSeed / decompiled)
- **Saved:** `World.SaveWorldFWLData` writes a `ZPackage` in this order: int 41 (world version), string name, string seedName, **int seed**, long uid, **int worldGenVersion**, bool needsDB, starting global keys, player history. Before the package it writes an int32 byte count. On load, `m_seed` is read straight from the file and is **not** recomputed from `m_seedName`, so the two can disagree if a tool edits one. `m_worldGenVersion` is read only when the file's world version ≥ `Version.World.WorldGenVersion`; otherwise it stays 0. (World.SaveWorldFWLData, World.LoadWorld / decompiled). `ZPackage.Write(string)` is `BinaryWriter.Write(string)`: a 7-bit-encoded length followed by UTF-8 bytes (ZPackage / decompiled).
- **Networked:** the server sends name, seed, seedName, uid and worldGenVersion, and the client builds its own `WorldGenerator` from them (ZNet.RPC_PeerInfo / decompiled). A client-only mod that changes world generation will therefore disagree with the server about terrain.

- **Only the int reaches generation, so there are exactly 2^32 distinct worlds.** `World..ctor` keeps
  `m_seedName` for display and saves it, but `WorldGenerator` is built from `m_seed`,
  `m_worldGenVersion` and `m_menu` alone; no generation path reads `m_seedName`. Two seed texts with
  the same `GetStableHashCode` therefore give a bit-identical world - verified 2026-09-23 by measuring
  `"hnBd9gJf2G"` and `"G74EJD"` (both -> 319486907) on a 96 m grid: identical biome field, land, island
  and structure numbers, differing only in wall-clock timings. Any "search every seed" tool should
  enumerate the int32 range (-2,147,483,648 .. 2,147,483,647), not seed strings: the 1-10 character
  A-Z a-z 0-9 text space is sum(62^1..62^10) = 853,058,371,866,181,866 texts, which is 198,618,129.8
  texts per world and 198.6 million times more work for the same answer. Seven characters are enough to
  reach every int32; six reach 77.08 % of them, leaving **984,542,424** ints with no text of six
  characters or fewer - seed 0, the menu world, among them. The create-world seed box takes **1-10
  characters, `characterValidation` Alphanumeric** (measured from the live UI component, see
  `references/seeds-and-world-files.md` 2.2), so every int32 SeedLab names can be typed back into the
  game.

## 3. Initialization from a World

```csharp
// WorldGenerator(World world)  (WorldGenerator..ctor / decompiled; overloads confirmed in IL)
m_world = world;
s_cachedBiomeAreas.Clear(); s_cachedBiomes.Clear();
m_version = m_world.m_worldGenVersion;
VersionSetup(m_version);
UnityEngine.Random.State state = UnityEngine.Random.state;      // save global RNG
UnityEngine.Random.InitState(m_world.m_seed);
if (m_noiseGen == null) { m_noiseGen = new FastNoise(m_world.m_seed); /* Cellular, Euclidean, Distance, 2 octaves */ }
m_noiseGen.SetSeed(0);                                          // FastNoise seed is ALWAYS 0
m_offset0 = Random.Range(-10000, 10000);   // int overload -> conv.r4
m_offset1 = Random.Range(-10000, 10000);
m_offset2 = Random.Range(-10000, 10000);
m_offset3 = Random.Range(-10000, 10000);
m_riverSeed  = Random.Range(int.MinValue, int.MaxValue);
m_streamSeed = Random.Range(int.MinValue, int.MaxValue);
m_offset4 = Random.Range(-10000, 10000);   // drawn LAST (after the two river seeds)
if (!m_world.m_menu) Pregenerate();        // lakes, rivers, streams
UnityEngine.Random.state = state;          // restore global RNG
```
- The IL calls `UnityEngine.Random::Range(Int32,Int32)` for all 7 draws. The offsets are **whole numbers** in [-10000, 9999] stored as float, because Unity names the int overload's second parameter `maxExclusive` (UnityEngine.Random stub in UnityEngine.CoreModule / decompiled). **Draw order matters:** `m_offset4` is the 7th draw.
- What each offset feeds:

| Field | Used by |
|---|---|
| `m_offset0` | base height X, Swamp mask |
| `m_offset1` | base height Y, Plains mask |
| `m_offset2` | BlackForest mask |
| `m_offset3` | all per-biome detail noise |
| `m_offset4` | Mistlands mask |

  (GetBaseHeight, GetBiome, Get*Height / decompiled)
- **FastNoise** (a C# port that works entirely in `double`, type `FastNoise` in assembly_utils) is a private **static** field. It is constructed once per process with the first world's seed, but `SetSeed(0)` runs on every construction, so **the cellular and simplex noise do not depend on the seed**. Its default `m_frequency = 0.01` multiplies every coordinate passed to `GetCellular` and `GetSimplexFractal`. (WorldGenerator..ctor, FastNoise ctor/SetSeed/GetCellular / decompiled)
- **VersionSetup(version):** (WorldGenerator.VersionSetup / decompiled)

| worldGenVersion | `m_minMountainDistance` | `minDarklandNoise` (Mistlands) | `maxMarshDistance` (Swamp) |
|---|---|---|---|
| 0 | 1500 | 0.5 | 8000 |
| 1 | 1000 | 0.5 | 8000 |
| 2 (all new worlds) | 1000 | 0.4 | 6000 |

- **Menu world:** `World.GetMenuWorld()` = `new World("menu", "") { m_menu = true }`, so it has seed 0 and version 2. The offsets are still drawn from `InitState(0)`, but `Pregenerate()` is **skipped**: there are no rivers, and `GetLakes()` returns null. `m_biomeData` is null, so `GetBiomeSector` returns `BiomeSector.EmptyBlackForest`. (World.GetMenuWorld, WorldGenerator..ctor, WorldGenerator.GetBiomeSector / decompiled)

## 4. Base height (`GetBaseHeight`, private)

This is the continent shape. Its output is normalised (0.15 is sea level after ×200) and it is the backbone of both biome selection and heights. Below, `P(a,b)` means `DUtils.PerlinNoise(double,double)`, which is `Mathf.PerlinNoise((float)a, (float)b)` (DUtils / decompiled). `dist = DUtils.Length(wx, wz)` is computed in double and cast to float.

```
X = wx + (100000 + off0)   // double, not truncated      Y = wz + (100000 + off1)
h  = P(X*0.002f*0.5, Y*0.002f*0.5) * P(X*0.003f*0.5, Y*0.003f*0.5)
h += P(X*0.002f,     Y*0.002f)     * P(X*0.003f,     Y*0.003f)     * h * 0.9f
h += P(X*0.005f,     Y*0.005f)     * P(X*0.01f,      Y*0.01f)      * 0.5 * h
h -= 0.07f
// sea channels: long winding water bands where two very-low-frequency noises are nearly equal
n10 = P(X*0.002f*0.25 + 0.123f, Y*0.002f*0.25 + 0.15123f)
n11 = P(X*0.002f*0.25 + 0.321f, Y*0.002f*0.25 + 0.231f)
c   = (1 - LerpStep(0.02, 0.12, |n10 - n11|)) * SmoothStep(744, 1000, dist)
h  *= (1 - c)
if dist > 10000:  h = Lerp(h, -0.2, LerpStep(10000, 10500, dist))
                  if dist > 10490: h = Lerp(h, -2, LerpStep(10490, 10500, dist))
                  return h
if dist < m_minMountainDistance && h > 0.28:      // no mountains near spawn
    h = Lerp( Lerp(0.28, 0.38, clamp01((h-0.28)/0.099999994039535522)), h,
              LerpStep(minMtn-400, minMtn, dist) )
```
(WorldGenerator.GetBaseHeight / decompiled)

- **The mountain-cap divisor is `0.099999994039535522`, not `(double)0.1f`** (`ldc.r8` at GetBaseHeight
  IL_04f1, `div` at IL_04fa, verified 2026-09-22). It is the widening of the float `0.38f − 0.28f`, i.e.
  the compiler folded the constant expression. A port that writes `/ 0.1f` is off by 7e-8 relative.
- The 10490 m edge branch calls **`Utils.LerpStep`** (all-float), while every other LerpStep here is
  `DUtils.LerpStep` (double interior, float result) — `GetBaseHeight` IL_04b3 vs IL_0486/IL_0530.

- The sea channels are switched off within 744 m of the centre and are at full strength beyond 1000 m.
- Mountains need base > 0.4. The code caps base height at 0.38 when dist < `m_minMountainDistance − 400` (600 m on v≥1, 1100 m on v0), and fades the cap out towards `m_minMountainDistance`. **So no Mountain biome can appear within 600 m of (0,0)** on a v2 world.
- Menu terrain (`menuTerrain: true`) uses only the first four lines: no channels, no edge, no mountain cap.

## 5. Biome determination (`GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false)`, public)

`wy` is the world **Z** coordinate. `GetBiome(Vector3 p)` is `GetBiome(p.x, p.z)`. The tests below run in this order (WorldGenerator.GetBiome / decompiled):

| # | Test (first match wins) | Result |
|---|---|---|
| 0 | `m_world.m_menu`: menu base height ≥ 0.4 → Mountain, else BlackForest | menu only |
| 1 | `waterAlwaysOcean && GetHeight(x,z) <= oceanLevel` (no vanilla caller passes `true`, see §10) | Ocean |
| 2 | `IsAshlands(x,z)` | **AshLands** (before the Ocean test, so the Ashlands sea is "AshLands") |
| 3 | `base <= oceanLevel` (0.02 by default, i.e. ≤ 4 m before the gap factors) | **Ocean** |
| 4 | `IsDeepnorth(x,z)` | **DeepNorth** (after the Ocean test, so the Deep North sea is "Ocean") |
| 5 | `base > 0.4` | **Mountain** (no distance band) |
| 6 | `P((off0+x)*0.001f, (off0+z)*0.001f) > 0.6` and `2000 < dist < maxMarshDistance` (6000 on v2) and `0.05 < base < 0.25` | **Swamp** |
| 7 | `P((off4+x)*0.001f, (off4+z)*0.001f) > minDarklandNoise` (0.4 on v2) and `dist > 6000 + A` and `dist < 10000` | **Mistlands** |
| 8 | `P((off1+x)*0.001f, (off1+z)*0.001f) > 0.4` and `dist > 3000 + A` and `dist < 8000` | **Plains** |
| 9 | `P((off2+x)*0.001f, (off2+z)*0.001f) > 0.4` and `dist > 600 + A` and `dist < 6000` | **BlackForest** |
| 10 | `dist > 5000 + A` | **BlackForest** (no Meadows beyond about 5 km) |
| 11 | otherwise | **Meadows** |

- For the mask noises, `(float)(off + x)` is truncated to float first, then multiplied by `0.001f` widened to double (0.0010000000474974513), and cast to float again inside `PerlinNoise`.
- **Wobble term** `A = WorldAngle(x,z) * 100`, where `WorldAngle(wx,wy) = (float)Math.Sin((float)((float)Math.Atan2(wx, wy) * 20.0))`: the IL truncates to float after `Atan2`, again after the `* 20.0` (conv.r4 before `Sin`), and once more after `Sin`. Note that the arguments are `atan2(x, z)`. The effect is a ±100 m ripple with 20 lobes (every 18°) around the centre. The Swamp band and every upper bound (6000 / 8000 / 10000) have **no** wobble. (WorldGenerator.WorldAngle / decompiled)
- **Ashlands** `IsAshlands(x,z)`: `Length(x, z − 4000) > 12000 + A`, compared in double. That is the outside of a circle of radius about 12 km centred on **(0, +4000)**, so it is the far **south**: at x = 0 it starts near z ≈ −8000. The public static readonly fields are `ashlandsMinDistance = 12000`, `ashlandsYOffset = −4000`. (WorldGenerator.IsAshlands / decompiled)
- **Deep North** `IsDeepnorth(x,z)`: `new Vector2(x, z + 4000).magnitude > 12000 + A`. That is the outside of a circle centred on **(0, −4000)**, so it is the far **north**: at x = 0 it starts near z ≈ +8000. The constants are private: `deepNorthMinDistance = 12000`, `deepNorthYOffset = 4000`. This check uses float `Vector2.magnitude`, while the Ashlands check uses a double comparison. (WorldGenerator.IsDeepnorth / decompiled)
- **World edge:** the public consts are `worldSize = 10000`, `waterEdge = 10500`, `waterEdgeSqr = 110250000`. Beyond 10000 m the base height is lerped towards −0.2 (reached at 10500 m, with a steeper drop towards −2 from 10490 m). High ground therefore stays land for a few hundred metres past 10000 m, and points turn Ocean progressively as the base falls to 0.02 or below, except in the Ashlands region, which test 2 catches first.
- **Biome enum** (`Heightmap.Biome`, flags): None=0, Meadows=1, Swamp=2, Mountain=4, BlackForest=8, Plains=0x10, AshLands=0x20, DeepNorth=0x40, Ocean=0x100, Mistlands=0x200, All, Land=0x27F. `Heightmap.BiomeIndex` (byte) is a dense 0..9 index, and it starts at **None = 0, Meadows = 1**, Swamp 2, Mountain 3, BlackForest 4, Plains 5, AshLands 6, DeepNorth 7, Ocean 8, Mistlands 9, Count 10. `BiomeHelpers.ToIndex(this Heightmap.Biome)` is just `(int)b.ToBiomeIndex()` and **throws NotImplementedException** for a combined mask or an undefined bit. Any tool that keeps its own 0-based biome array is off by one against this and must not call the result a `BiomeIndex`. (Heightmap / decompiled)
- **Derived: the distance band each biome can ever occupy**, read off the tests above with |A| <= 100.
  These are bounds on `DUtils.Length(x, z)` at the sampled point and they hold for **every seed**, so
  they are what a search or feasibility check should reason with. Verified 2026-09-23 against the
  branch conditions in the table above; every row is `worldGenVersion`-dependent where marked, and
  hard-coding the v2 column makes a v0/v1 answer wrong.

  | Biome | Closest possible | Furthest possible | Why |
  |---|---|---|---|
  | Meadows | 0 | **5,100** | test 11 is reached only when `dist <= 5000 + A` |
  | BlackForest | **500** | 10,500 (world edge) | `dist > 600 + A`, or the `dist > 5000 + A` fallback |
  | Swamp | **2,000** | 6,000 (8,000 on v<=1) | `2000 < dist < maxMarshDistance`, no wobble either side |
  | Plains | **2,900** | 8,000 | `dist > 3000 + A && dist < 8000` |
  | Mistlands | **5,900** | 10,000 | `dist > 6000 + A && dist < 10000` |
  | Mountain | **600** (1,100 on v0) | 10,500 | `GetBaseHeight` clamps the base to `Lerp(0.28, 0.38, t) <= 0.38` while `LerpStep(minMountainDistance - 400, minMountainDistance, dist) == 0`, and Mountain needs `base > 0.4` |
  | AshLands | **7,907.7** | 10,500 | `Length(x, z - 4000) > 12000 + A`. **Corrected 2026-09-23 — this row previously said 7,900, from `z = -(12000 - 100) + 4000` along -z.** That is wrong: `A = 100*sin(20*atan2(x,z))` is about **0**, not -100, at due south, so the boundary there is at 8,000 m. The true minimum is off-axis, at the bearing `theta = 0.975*pi` where `sin(20*theta) = -1`: `r = -4000*cos(0.025*pi) + sqrt(11900^2 - 16e6*sin(0.025*pi)^2) = 7,908.2`, and minimising over all bearings gives **7,907.7 m**. Probed in SeedLab: along that bearing the biome flips to AshLands between 7,908 and 7,910 m, identically in seeds 12345, -998877 and 2000000000 |
  | DeepNorth | **7,907.7** | 10,500 | the mirror of the above, but only as a bound on the *region*; the realised biome is the region minus its ocean part (see below) |
  | Ocean | 0 | 10,500 | `base <= oceanLevel` is reachable at any distance, including the origin |

- **AshLands is the same set of points in every seed; DeepNorth is not.** `IsAshlands` is tested at step 2, **before** the ocean test, and it is a pure function of (x, z) — no noise, no `m_offset`, no seed. So the AshLands *region* is the AshLands *biome*, and both its area and its nearest distance from (0,0) are **constants of the game, not properties of a world**: measured with SeedLab's 2048x2048 / 12 m game grid, **298,829 cells = 43,031,376 m2 = 12.4236 % of the 10,500 m disc, nearest 7,908.87 m (grid) / 7,907.7 m (continuum)**, byte-identical across 40 uniformly drawn seeds. `IsDeepnorth` is equally seed-free but is tested at step 4, **after** `base <= oceanLevel -> Ocean`, so the DeepNorth biome is the region minus a seed-dependent ocean part: over the same 40 seeds its area ranged 21.1-26.6 km2 and its nearest distance 7,908.9-7,971.7 m. Any search criterion on AshLands area or AshLands nearest-distance is therefore **vacuous** (verified 2026-09-23).
- **"Nearest <biome> to the centre" barely moves with the seed for most biomes.** Over the same 40 seeds (SeedLab `vseed seed --json`, 12 m grid, 8.49 m distance quantum): Black Forest took **one** distinct value (508.3 m), AshLands one (7,908.9 m), Plains three spanning 0.7 m (2,901.9-2,902.6), Mistlands three spanning 0.5 m (5,900.4-5,900.9), Swamp four spanning 0.5 m (2,000.0-2,000.5), Deep North ten spanning 62.9 m. Only **Mountain** (619.4-1,187.5 m), **Ocean** (8.5-846.0 m) and weakly **Meadows** (8.5-119.1 m) vary usefully. The reason is geometric and should generalise: each band's inner edge is a circle tens of kilometres long and the biome test there is a Perlin threshold that much of the ring passes, so the measured nearest is pinned to the band edge. Mountain is the exception because its test is `base > 0.4` under a cap that is blended off between 600 and 1,000 m. Biome **area** does still vary usefully (Black Forest 11.8-17.4 %, Plains 9.3-14.2 %, Mistlands 16.2-18.3 %, Meadows 2.5-3.9 % of the disc) — except AshLands. *Empirical: 40 seeds, not 4.29 billion.* (verified 2026-09-23)
- Unused private consts such as `meadowsMaxDistance = 5000` and `maxDeepForestDistance = 6000` are documentation only. The code uses literals, and consts are inlined anyway.

### 5.1 Straight biome edges: the Perlin-lattice artifact (verified 2026-09-23)

Every Valheim world contains dead-straight, kilometre-long biome boundaries, usually east-west. They are
vanilla — they are in the game's own `cacheMinimapBiome` — and they come from Unity's Perlin gradient
set, not from the distance bands.

- Each mask in `GetBiome` is a **single** Perlin sample, `DUtils.PerlinNoise((double)(float)(m_offsetK + wx) * 0.0010000000474974513, (double)(float)(m_offsetK + wy) * 0.0010000000474974513)` — the same offset on both axes — so the noise lattice is **1000 m** wide; and because `m_offset0..4 = Random.Range(-10000, 10000)` is the **int** overload, the offsets are whole metres and the lattice lines sit at **w = -m_offsetK + 1000k** (x and z alike).
- `Mathf.PerlinNoise` normalises as `(raw + 0.69f) / 1.483f`, and its 16 gradient classes are lopsided: 4/16 are (0,-1), 2/16 (0,+1), 2/16 each (±1,0). When two horizontally adjacent lattice corners carry the same purely vertical gradient (7.8 % of pairs) the raw noise near that row is **exactly -fz** (or +fz) — independent of x — because the quintic fade weights the far row only `Fade(0.0968) = 0.0078`.
- So the mask's threshold contour is a straight line at **d = |T * 1.483f - 0.69f| * 1000 m** from the lattice row: **96.8 m** for the 0.4 masks (Plains `m_offset1`, BlackForest `m_offset2`, Mistlands `m_offset4` at worldGenVersion 2), **199.8 m** for Swamp (0.6, `m_offset0`), **51.5 m** for Mistlands on worldGenVersion ≤ 1 (0.5). Which side depends on the gradient sign and on the sign of `T*1.483-0.69`. Measured straightness along the run: ±0.8 m (51.5 m), ±8 m (96.8 m), ±30 m (199.8 m); the residual is `(l1 + fz)*Fade(fz)`.
- The permutation table is a constant in `UnityPlayer.dll`, so **the degenerate rows/columns are the same in every world**; only their placement moves with the offsets. Rows (noise index, column run): 0 (20-21), 1 (5-8), 2 (10-11), 3 (5-7), 7 (5-6), 8 (11-12, 20-21), 9 (4-5, grad +y), 10 (18-20), 11 (20-21), 12 (11-12 grad +y, 19-20), 13 (1-2, 20-21), 16 (0-2). Columns: 5 (rows 18-19, 20-21), 7 (20-21), 13 (15-16, 17-18), 17 (0-1, 10-11, 12-13).
- `Mathf.PerlinNoise` folds its inputs with `abs()`, so **each mask is exactly mirror-symmetric about x = -m_offsetK and z = -m_offsetK** (bit-identical at integer coordinates, ≤1.3e-6 apart otherwise). Every seam therefore appears up to four times per world — two z positions × two x extents — and lattice line 0 coincides with the fold, which is itself a crease (the mask's slope flips sign there). The *biome* is not mirrored, because base height and distance are not: only ~48 % of points match across the fold.
- North-south seams exist by the mirrored rule ((±1,0) gradients down a column) but are 2.5× rarer and shorter: longest measured 875 m against 1.2-1.5 km for east-west ones; scan peaks are 10-17 sd above background horizontally against 4-7 sd vertically. That is why the artifact reads as east-west banding.
- **It is not a discontinuity** and not a kink that merely looks straight: the mask is C2 and crosses the threshold at about `1/(1000*1.483)` per metre; only the level line is straight. The biome edge is as hard as any other biome edge.

Recipe for a seed: seam candidates are z (or x) = `-m_offsetK ± (1000*nz + d)`, with the x extent
`-m_offsetK ± [1000*nx0, 1000*nx1]` of that row's column run (and both mirror images). A candidate is
visible only where mask K actually decides the biome (distance bands, base height and the earlier tests
come first).

Worked examples. Seed -1772362158 (offsets -6080, 4986, -7704, -59, 718): z = +378.8 and its mirror
z = -1814.8 are the Mistlands mask 96.8 m off its rows z = 282 / -1718 (noise columns 5-8 =
x -8718..-5718 and 4282..7282); z = +2378.8 / -3814.8 are the same mask's row 3; z = -3889.2 is the
Plains mask; the vertical edge near x ≈ -5400 is the BlackForest mask at x = -5392.8. Seed 319486907
(offsets 5311, 1567, 7130, -6771, 8271): z = -470.2, 1529.8, -2663.8, -4663.8 (Plains), -6174.2,
-7174.2, 4825.8 (Mistlands), -4510.8 (Swamp) — the eight strongest measured seams, 8/8 predicted from
the offsets alone.

Evidence: `WorldGenerator.GetBiome`, `WorldGenerator..ctor` (assembly_valheim 59f53fb5); UnityPlayer's
`PerlinNoise::Noise` through SeedLab's `UnityPerlin` (bit-exact on the game's own samples, §9); measured
over both ground-truth worlds and confirmed in the game's own decoded biome cache (rows wy 378 → 390
differ in 109 of 124 columns over x -8000..-6525 in `asdasdasd`, while neighbouring row pairs differ in
0, 0, 0 and 13); 25 of 25 random seeds have their strongest horizontal seam within 8.2 m of a predicted
line (mean 3.5 m). **Careful:** not every straight line on a biome map is this — see the attribution
pitfall in `valheim-modding/references/pitfalls.md` section 9.

## 6. Terrain height (`GetBiomeHeight`, public)

`GetHeight(x,z)` = `GetBiomeHeight(GetBiome(x,z), x, z, out mask)` (WorldGenerator.GetHeight / decompiled). Inside `GetBiomeHeight`:
```
mult = preGeneration ? 200 : 200 * CreateAshlandsGap(x,z) * CreateDeepNorthGap(x,z)   // GetHeightMultiplier() == 200
menu world: Mountain -> GetSnowMountainHeight(menu:true)*mult, else GetMenuHeight*mult
if Length(x,z) > 10500 -> return -400   (-2 * 200)
switch(biome) -> f_biome(x,z) * mult
```
(WorldGenerator.GetBiomeHeight / decompiled)

- **The output is in world metres, and sea level is 30.** Evidence: `Minimap.GetMaskColor` treats `height < 30f` from `GetBiomeHeight` as water, and `AltBiomeWorldData` counts `>= 30f` as "above sea level". `ZoneSystem.m_waterLevel` has code default `30f` and is a public serialized field. (Minimap.GetMaskColor, AltBiomeWorldData.tryFill, ZoneSystem / decompiled). Normalised 0.15 therefore equals sea level.
- **Gaps (ocean moats):** `CreateAshlandsGap` and `CreateDeepNorthGap` = `MathfLikeSmoothStep(0,1, clamp01(|ringDist − (12000+A)| / 400))`. The height multiplier drops to 0 on each biome ring and is back to full strength 400 m away, which gives the sea gap in front of Ashlands and Deep North. The Ashlands gap function is private and the Deep North one is public static. (CreateAshlandsGap/CreateDeepNorthGap / decompiled)
- `GetBiomeHeight` also calls `GetBiomeSector(x,z)` and **never uses the result** in this build.

Shared pieces. `U = (float)(x + 100000 + off3)` and `V = (float)(z + 100000 + off3)` are **truncated to float**, so noise coordinates are quantised to 1/128 m near 1e5 (confirmed by `conv.r4` in the GetMeadowsHeight IL). The same `off3` is used for both axes. Then:
`D = P(U*.01,V*.01)*P(U*.02,V*.02); D += P(U*.05,V*.05)*P(U*.1,V*.1)*D*0.5`, and the fine noise is `F = P(U*.1,V*.1)*0.01 + P(U*.4,V*.4)*0.003`, added after the rivers step.

| Biome | f(x,z) (normalised; ×mult afterwards) | Rivers? |
|---|---|---|
| Meadows / Plains | `h = base + D*0.1`; if `h > 0.15`: `h -= (h−0.15)*(1−clamp01(base/0.4))*0.75` (squashes land near sea level) ; `AddRivers` ; `+F`. For an exact port: Meadows (and DeepNorth) evaluate `(h−0.15) * ((1−k)*0.75)` in double, while Plains evaluates `((h−0.15)*(1−k)) * 0.75` and computes `h−0.15` as a float subtraction (GetMeadowsHeight/GetPlainsHeight IL). | yes |
| BlackForest | `base + D*0.1` ; `AddRivers` ; `+F` | yes |
| Swamp | coordinates `(float)(x+100000)` with **no seed offset**: `0.137 + P(.04)*P(.08)*0.03` ; `AddRivers` ; `+P(.1)*0.01 + P(.4)*0.003`. Swamp micro-terrain is the same in every world. | yes |
| Mountain | `base + (base−0.4) + D*0.2` ; `AddRivers` ; `+F + P(U*.2,V*.2)*2*tilt`, where `tilt = |b(x+1)−b(x−1)| + |b(z−1)−b(z+1)|` (BaseHeightTilt) | yes |
| Ocean | `base` | no |
| DeepNorth | `b' = base+0.1` ; `h = b' + D*0.1`, then the Meadows-style squash using `clamp01(b'/0.4)` ; `AddRivers` ; `+F`. `mask.g = 0.3 + 0.3*(Fbm(U*.01,V*.01,3,2,0.5)+1)/2` | yes |
| Mistlands | `M = P(U*.014)*P(U*.028); M += P(U*.021)*P(U*.035)*M*0.5; M = M^1.5` ; `h = base + M*0.4` ; `AddRivers` ; `k = clamp01(7M)` ; `h += P(.1)*0.03k + P(.4)*0.01k` ; **terraces** `h = Lerp(h + P(.4)*0.002, ceil(h*400)/400, k)` ; `mask.a = 1 − 1.2k − (1 − LerpStep(0.1,0.3,k))` | yes |
| AshLands | `GetAshlandsHeight` (below) | **no** (only the pre-generation variant adds rivers) |

(GetMeadowsHeight, GetPlainsHeight, GetForestHeight, GetMarshHeight, GetSnowMountainHeight, GetOceanHeight, GetDeepNorthHeight, GetMistlandsHeight / decompiled.) In the Mistlands row, 0.014 means `0.02*0.7` and so on, and the literal values are the widened float constants.

**AshLands** (`GetAshlandsHeight(x, z, out mask, cheap=false)`, public; the Minimap calls it with `cheap:true`):
- The coordinates are `x + (100000f + off3)` in double and are *not* truncated to float.
- The ridge band is `1 − clamp01(|Length(x, z−2800) − (12000+A)|/1000)` → smoothstepped into the range 0.1..1, then multiplied by `1 − clamp01(|x|/7500)`.
- The edge fade is `1 − clamp01((dist − 10150)/600)`. The pre-multiplier height is lerped towards −1 there, and a simplex factor in [0,1] multiplies it afterwards. Beyond 10500 m, `GetBiomeHeight` returns −400 regardless.
- FastNoise cellular fBm: 5 octaves (2 if `cheap`) starting at 0.33, plus 3 octaves (2 if `cheap`) starting at 8.0 for lava. FastNoise simplex fractal at 0.075, raised to the power 1.4.
- The lava mask is `lava = BlendOverlay(LerpStep(0.7, 1, Fbm(x'*.01, z'*.01, 3, 2, 0.5) × clamp01(Remap(ridge,0,0.5,0.5,1)))², lavaCellular) × clamp01((h − 0.15 − 0.02)/0.01)`, where x', z' are the untruncated Ashlands coordinates above, `Fbm` is the Perlin-based `DUtils.Fbm`, and `lavaCellular = clamp01(Remap(cellular8, −1,1, 0,1)^4 × 2)` from the 8.0-start cellular octaves. The dip is `Remap(P(x'*.05+5124, z'*.05+5000)², 0,1, 0.01,0.055)`. Then `h = Lerp(h, clamp(h − dip, 0.16, 5000), lava)`, and `mask.a = lava`.
- `ZoneSystem.IsLavaPreHeightmap` uses `mask.a > 0.6`.
(WorldGenerator.GetAshlandsHeight, ZoneSystem.IsLavaPreHeightmap / decompiled)

**Pre-generation variants** are used only while rivers and streams are being placed, through `GetPregenerationHeight`:
- There is no gap multiplier.
- Mistlands uses the BlackForest formula.
- AshLands uses `base + D*0.1 + 0.1 + F`, then rivers.
- DeepNorth uses `base (+0.1 unless riverPregen) + max(0, h−0.4) + D*0.2`, ×1.2, rivers, then fine noise built from **float-only** `wx*0.1f`.
(GetBiomeHeight, GetAshlandsHeightPregenerate, GetDeepNorthHeightPregenerate / decompiled)

**AddRivers(x, z, h)**, private: (WorldGenerator.AddRivers, GetWeight / decompiled)
```
weight = max over nearby river points of (1 - d/r);  width = Σ(r*w)/Σw
t = LerpStep(20, 60, width)
if h > Lerp(0.14, 0.12, t):   h = Lerp(h, Lerp(0.14, 0.12, t), weight)                    // 28 m (streams) .. 24 m (rivers)
if h > Lerp(0.139, 0.128, t): h = Lerp(h, Lerp(0.139,0.128,t), LerpStep(0.85, 1, weight))  // 27.8 .. 25.6 m in the centre line
```
Rivers only ever **lower** terrain, pulling the bed to 24–28 m, which is below the 30 m water line. The fine noise `F` (up to +2.6 m) is added afterwards.

## 7. Lakes, rivers and streams (`Pregenerate`, private; skipped for the menu world)

Order: `FindLakes()` → `m_rivers = PlaceRivers()` → `m_streams = PlaceStreams(false)` → `PlaceStreams(true)`, whose return value is discarded (WorldGenerator.Pregenerate / decompiled). Only the seed-derived fields and `m_worldGenVersion` go in, so **rivers are deterministic from the seed and are not stored in the save.**

- **Pre-generation is ~99.5 % of the cost of building a world, and nothing but height reads it.** Measured 2026-09-23 on the SeedLab port (AMD Ryzen 7 9800X3D): building a world and sampling its biomes on a 56×56 grid cost **299.3 ms per seed** with pre-generation and **1.5 ms per seed** without it. The only readers of `m_rivers` / `m_streams` / `m_riverPoints` / `m_lakes` are `GetRiverWeight` (i.e. `AddRivers`, reached from `GetHeight` / `GetBiomeHeight`) and the four public accessors `GetLakes/GetRivers/GetStreams/GetRiverPoints`; `GetBaseHeight` and `GetBiome` at its default arguments never touch them (`GetBiome` tests `GetBaseHeight`, not `GetHeight`, unless `waterAlwaysOcean: true` is passed). The seven RNG draws all happen **before** `Pregenerate()` and pre-generation is the last thing the constructor does, so running it lazily on first use changes nothing observable - proven bit-exact (rivers, streams, lakes, river-point grid and 64,512 `GetHeight` samples) in `SeedLab.Search.Tests`. This matters for any tool that asks a biome-only question about many seeds. (WorldGenerator..ctor, WorldGenerator.GetBiome, WorldGenerator.GetRiverWeight / decompiled; measured)
- **FindLakes:** samples a 128 m grid from −10000 to 10000 on both axes (157×157), inside radius 10000, and keeps points where `GetBaseHeight < 0.05` (≈10 m). Those points are merged with `MergePoints(range 800)`. MergePoints repeatedly averages the current point with its closest neighbour within 800 m: `(v+p)*0.5`, which is not a true centroid, and the list order changes through swap-removes. "Lakes" here are simply low basins, **including open sea**. (FindLakes, MergePoints / decompiled)
- **PlaceRivers:** `Random.InitState(m_riverSeed)`. It works through a copy of the lake list. On each iteration it takes the first remaining lake and links it to a random valid lake within **2000 m**. If there is none *and* that lake has no river yet (as either end), it tries **5000 m** instead. When no link is found, the lake is removed from the list. Candidate ends are searched in the full `m_lakes` list (including lakes already removed from the working copy), and the loop runs only `while (count > 1)`, so the last remaining lake is never processed as a start. The result is that almost every pair of lakes within 2000 m with a valid line gets a river (validity is tested from the starting lake's side, and the 128 m sampling is not exactly symmetric), with at most one 5000 m fallback link started by each lake.
  - A link is valid if it is not a duplicate and `IsRiverAllowed` holds: sampling the base height every 128 m along the straight line, **no sample may exceed 0.4** and **at least one sample must exceed 0.05** (the line has to cross land).
  - Width: `widthMax = Random.Range(60f,100f)`, `widthMin = Random.Range(60f, widthMax)`.
  - `curveWidth = len/15`, `curveWavelength = len/20`.
  - The global RNG state is saved and restored.
  (PlaceRivers, FindRandomRiverEnd, IsRiverAllowed / decompiled)
- **PlaceStreams(isDN):** `Random.InitState(m_streamSeed)`, then **3000** attempts. Each attempt:
  - Start point: up to 100 tries of `(Range(-10000f,10000f), Range(-10000f,10000f))` until the pre-generation height is in (26, 31) m.
  - End point: up to 100 tries. The length goes from 198.8 down to 80 m in 1.2 m steps, with angle `Range(0, 2π)`, until the height is in (36, 44) m.
  - The midpoint height must be in [26, 44].
  - Width is fixed at 20.
  Both passes reseed with the **same** `m_streamSeed` and pass `riverPreGen = !isDN` down to the height functions:
  - Pass 1 (`isDN=false`, `riverPreGen=true`) uses the Deep North pre-generation height **without** the +0.1, and renders only streams whose start point is not Deep North.
  - Pass 2 (`isDN=true`, `riverPreGen=false`) uses it **with** the +0.1, and renders only Deep North streams. **`GetStreams()` returns the pass-1 list, which includes Deep North streams that were never rendered and leaves out the ones that were.** (PlaceStreams, FindStreamStartPoint, FindStreamEndPoint, RenderRivers / decompiled)
- **RenderRivers:** steps along p0→p1 every `widthMin/8` m. The lateral offset is `sin(t)·sin(0.63412t)·sin(0.33412t)·curveWidth`, where `t = s/curveWavelength`. **Each step draws `r = Random.Range(widthMin, widthMax)`**. Every point is stored in each 64 m river-grid cell (`floor((x+32)/64)`, the same layout as zones) whose centre `(gx*64, gy*64)` is within `r+32` of the point on both axes, i.e. the 64 m cell grown by `r` on every side contains it. The storage is private `Dictionary<Vector2i, RiverPoint[]> m_riverPoints`. (RenderRivers, AddRiverPoint, InsideRiverGrid, GetRiverGrid / decompiled)
- **Coupling:** stream placement queries `GetPregenerationHeight`, and that already includes the rivers rendered before it (`AddRivers`). Stream pass 2 also sees the pass-1 streams. **One changed RNG draw or one flipped height comparison shifts every later stream**, which makes streams the hardest part to reproduce exactly.
- **Public accessors:** `GetLakes()` (`List<Vector2>`), `GetRivers()`, `GetStreams()` (`List<River>`: p0, p1, center, widthMin, widthMax, curveWidth, curveWavelength). Nothing in vanilla calls them. `GetRiverGrid` and `InsideRiverGrid` are also public.

## 8. How the game actually uses it (why `GetHeight` ≠ ground height)

- **Terrain mesh:** `HeightmapBuilder.Build` (background thread) evaluates `GetBiome` at the **4 corners** of each heightmap. If all four match, every vertex uses that biome's function. Otherwise every vertex is a **bilinear blend of the 4 corner-biome height functions**, weighted by `DUtils.SmoothStep` of the local coordinates. Distant-LOD heightmaps instead use `GetBiome` per vertex and apply a 4-pass spike smoothing (differences over 10 m are averaged). Then `Heightmap.ApplyModifiers()` applies terrain edits. (HeightmapBuilder.Build, Heightmap.Generate / decompiled)
  - As a result, a small biome patch that no heightmap corner touches is built with the surrounding biome's height function.
  - Heights are the Heightmap's local vertex Y (`Heightmap` vertex = `(…, m_heights[i], …)`), and the zone root is instantiated at y = 0 (`ZoneSystem.GetZonePos` → `(x*64, 0, z*64)`). They equal world Y as long as the Heightmap child sits at the zone prefab's origin. That is prefab data, but the code treating 30 as sea level on these same values supports it.
  - In-game, `ZoneSystem.GetGroundHeight` / `GetSolidHeight` are the ground truth for loaded zones. `WorldGenerator.GetHeight` works anywhere, even in unloaded zones, but returns the unblended, unmodified value.
- **Sector grid (`AltBiomeWorldData`):** 2048×2048 points with 12 m pixels. `MapSpaceToWorldSpace(i) = (i−1024)*12 + 6` and `WorldSpaceToMapSpace(x) = (int)((x−6)/12 + 1024)`, which truncates, so a query snaps *down* by up to 12 m, not to the nearest point. Each point stores `GetBiome` and `GetBiomeHeight`. Points outside radius 10500 are Ocean at −1000. The grid is flood-filled into `BiomeSector`s (connected regions of one biome), and all AshLands, all DeepNorth and all Ocean form one sector each. Alt-biomes are assigned per sector with `Random.InitState(seed + 920)` and so on. `GetBiomeSector` returns `EmptyBlackForest` if `m_biomeData` is null and `EmptyMeadows` if it is not ready. The cache file `…/cache/<world>_biomedatacache.bin` is **deleted on every load**, and `TryLoadCache`/`SaveCache` have no callers. (AltBiomeWorldData.* / decompiled)
- **Gameplay biome at a point:**
  - `Heightmap.FindBiome(p)` → `Heightmap.GetBiome`. For non-LOD heightmaps, if all 4 **corner sectors** share a biome, it returns that biome. Otherwise each corner adds `(1.414 − d)^3` to its biome's weight, where d is the distance in normalised 0..1 heightmap space, and the biome with the largest summed weight wins. It is not a direct `GetBiome` call. (Heightmap.GetBiome, Heightmap.Distance / decompiled)
  - `Player.UpdateBiome` uses `GetBiomeSector(pos)` as the current biome and logs `"GetBiome error"` when that differs from `GetBiome(pos)`. (Player.UpdateBiome / decompiled)
- **Minimap:** `Minimap.GenerateWorldMap` samples `GetBiome` and `GetBiomeHeight` at pixel centres `(i − size/2)*pixelSize + pixelSize/2`. **The shipped prefab values are `m_textureSize = 2048` and `m_pixelSize = 12`** — read out of the loaded `Minimap` component in the running game 2026-09-22 (`prefab-constants.json` in the SeedLab data snapshot), which settles what the cache geometry had only been *derived* from before. The code defaults (256 and 64f) are field initialisers the prefab overrides; never quote them. The result is cached, keyed by seed, as `<worlds dir>/<worldName>/cacheMinimapMask|Biome|Height|Meta`, with heights stored as a compressed **half-float** buffer. (Minimap.GenerateWorldMap, SaveMapTextureDataToDisk, DeleteMapTextureData / decompiled). The paths are set once in `Minimap.Start`, only if `ZNet.World` is non-null, from `World.m_worldName`. **Unverified for clients:** joining sets `ZNet.World` to null (`ZNet.SetServer(false, …, null)`), and the `World` built in `RPC_PeerInfo` has an empty `m_worldName`. If `Minimap.Start` runs before `RPC_PeerInfo` (timing not checked), a client neither loads nor saves this cache.
- `GetForestFactor(pos) = DUtils.Fbm(pos*0.01*0.4, 3, 1.6, 0.7)` and `InForest = factor < 1.15` are static and have **no seed input**, so the forest-density pattern is the same in every world. (WorldGenerator.GetForestFactor / decompiled)

## 9. Determinism and reproducing it outside the game

**Inputs (verified):**
- `GetBiome(x,z)` depends on `m_seed` (through off0, off1, off2, off4), `m_worldGenVersion`, `m_menu` and `Mathf.PerlinNoise`. It does not depend on rivers.
- `GetHeight(x,z)` additionally depends on off3, the river and stream point set (from `m_riverSeed`, `m_streamSeed` and `UnityEngine.Random`) and FastNoise with seed 0.
- Nothing else is involved: no save data, no time, no config.
- Not seed-only: player terrain edits, location terrain modifiers, and heightmap corner blending (§8).

**Pure managed code**, which can be ported verbatim from decompilation: `WorldGenerator`, `DUtils`, `FastNoise` (the decompiled double variant; do not substitute upstream FastNoise), `GetStableHashCode`, and Unity's managed `Vector2.magnitude`/`Distance`/`Normalize`, which compute `(float)Math.Sqrt(x*x+y*y)` with the **sum accumulated in double** (see the multi-op float chain note below — this line previously said "in float", which was wrong), and `Mathf.Sin/Cos/FloorToInt/CeilToInt/Ceil`, which wrap `System.Math` (UnityEngine.CoreModule / decompiled).

**Float/double discipline matters.** The shipped IL really uses `conv.r8` / `conv.r4` throughout; this is not decompiler noise (checked `WorldAngle`, `GetBaseHeight`, `GetMeadowsHeight` and `.ctor` IL). The rules to follow:
- Arithmetic is done in **double**.
- Results are **truncated to float at the end of each statement**.
- Literals are **float constants widened to double**, for example 0.002f = 0.0020000000949949026.
- Perlin inputs are cast to float.
- **`DUtils.MathfLikeSmoothStep` returns a float-rounded double**: its IL ends `add; conv.r4; conv.r8; ret` (DUtils.MathfLikeSmoothStep IL_0037–IL_003a, verified 2026-09-22). A port must write `return (double)(float)(to*t + from*(1.0-t));`. It is used by `CreateAshlandsGap`, `CreateDeepNorthGap` and four times inside `GetAshlandsHeight`, and both gap functions additionally cast their clamped input with `(float)` before calling it (CreateAshlandsGap IL_0054–IL_0055).
- **`Vector2.operator ==` is an epsilon test, not equality**: `(dx*dx + dy*dy) < 9.9999994396249292E-11f` (Vector2.op_Equality IL_0024–IL_0029, verified 2026-09-22). It decides which lakes may be linked, through `FindClosest`, `FindRandomRiverEnd` and both `HaveRiver` overloads, so an external port that compares floats exactly will occasionally build a different river graph.
- **`Random.Range(float,float)` is max-INCLUSIVE** while `Range(int,int)` is max-exclusive (parameter names `minInclusive, maxInclusive` / `minInclusive, maxExclusive` in UnityEngine.CoreModule, verified 2026-09-22). So `m_riverSeed`/`m_streamSeed` can never be `int.MaxValue`.
- A few spots really are single float operations whose result is **stored**, and those are portable as written: `GetDeepNorthHeightPregenerate`'s `wx*0.1f` and `+0.1f`, `GetPlainsHeight`'s `h − 0.15`, the Mistlands and `GetMenuHeight` `P*P`, the `wy + 4000f` / `wy + ashlandsYOffset` inside `CreateDeepNorthGap`, `CreateAshlandsGap`, `DeepNorthWaveFade` and `GetAshlandsOceanGradient`, and `100000f + m_offset3` in `GetAshlandsHeight` (exact, since both are integers). One IEEE float op gives the same result whether evaluated in float or in double and then rounded — but that argument only holds while the result is narrowed before the next operation.
  **Corrected 2026-09-22: `GetDeepNorthHeight`'s `base + 0.1f` was on this list and does NOT belong here.** The IL `dup`s it: one copy is narrowed by `stloc.s V_5`, the other is `conv.r8`'d **unnarrowed** and is what divides by `0.4f` for the sea-level squash, so Mono computes that `k` from the *double* `(double)base + (double)0.1f`. Getting it wrong cost 17 of 895 and 15 of 925 river-free DeepNorth location heights (1-2 float ulps each). See the multi-op bullet below and pitfalls.md section 9.
- **Multi-op float chains in Unity's managed code** — `Vector2.magnitude`, `Vector2.Distance`, `Vector2.SqrMagnitude` — are now **verified from IL** (2026-09-22): the IL really does `mul; mul; add` on float32 stack values and only then `conv.r8; Math.Sqrt; conv.r4` (Vector2.get_magnitude IL_000d–IL_001d, Vector2.Distance IL_001d–IL_002a). **Corrected 2026-09-22 — this sentence previously said the opposite, and it was the single largest source of error in the SeedLab port.** The old claim was: "on .NET/Mono x64 and ARM64 those are single-precision SSE/NEON ops, so a port written with C# `float` locals reproduces them". That is **wrong**. Unity's Mono holds every FP evaluation-stack slot at `R8` and narrows only at a `conv.r4` or a store into a float32 location, so `x*x + y*y` is accumulated in **double** and rounded once, whereas a C# `float` chain rounds three times. Writing these members with `(double)` interiors (together with `GetDeepNorthHeight`'s stack-resident `base + 0.1f`, see pitfalls.md) took the SeedLab port from 12 244/12 314 and 12 227/12 287 bit-exact location heights to **12 314/12 314 and 12 287/12 287**, and the 2048x2048 minimap height sweep from 5 and 9 differing binary16 codes to **0 and 0** — fully bit-exact on both worlds, the hold-out included. Lake, river, stream and river-point counts were **unchanged**, so it is a precision fix and not a geometry change. The same rule applies to `Vector2.op_Equality` (IL_001f–IL_0029), which is part of this group. Used by `IsDeepnorth` (magnitude), `FindLakes` (magnitude), river lengths and lake merging (Distance) and river weights (SqrMagnitude). It follows that `Vector2.magnitude` and `DUtils.Length(float,float)` are **numerically identical** under Mono — `DUtils.Length` merely spells the `conv.r8` out explicitly (DUtils.il.txt IL_0001–IL_000a). The earlier claim that the two differ is **withdrawn**. The subtraction inside `Distance`/`op_Equality` *is* narrowed, because it stores into a float32 local (Distance IL_000e, IL_001c). Valheim's own code has two more multi-op float chains: `GetBaseHeight`'s 10490 m edge uses the all-float `Utils.LerpStep` = `Clamp01((v−l)/(h−l))` (harmless there: with l = 10490, h = 10500 and 10490 < v < 20980, both subtractions are exact by Sterbenz's lemma, leaving one rounding), and the DeepNorth `mask.g` (`(f+1)/2`, then `0.3f + f*0.3f`). **Corrected 2026-09-22:** the second of those **does** round twice if written as a float chain — IL_01e5–IL_01f3 is `mul; add; stloc`, one narrowing for two operations — so it needs the same double interior as the `Vector2` members above; `(f+1)/2` is genuinely immune, because halving commutes with rounding. It affects only the mask, which neither the minimap cache nor the `.db2` can see, so it is corrected from the IL rather than measured.
- `Math.Sin`, `Atan2` and `Pow` come from the platform math library and could differ in the last bit. That would only matter at an exact threshold, such as `WorldAngle` near a band boundary. **Resolved 2026-09-23:** the dumper recorded Mono's own `Math.Sin/Cos/Atan2/Pow` results inside the game on exactly these arguments (`goldens/natives-libm.json`), and .NET 10 reproduces **93/93 of them bit-identically, max |ULP| 0** (Atan2 49/49 over a 7x7 world grid, Sin 10/10, Cos 10/10, Pow 24/24 including the Mistlands ^1.5 and Ashlands ^1.4 exponents), with `WorldGenerator.WorldAngle` itself **49/49 bit-exact** as float. Bound on what a last-bit difference could ever have cost, measured rather than guessed: perturbing `WorldAngle` by +/-1 ULP at every one of the 4 194 304 minimap pixel centres flips `IsAshlands` or `IsDeepnorth` at **0** of them.

**Native Unity parts that cannot be verified from managed code:**
- `Mathf.PerlinNoise` is `[FreeFunction("PerlinNoise::NoiseNormalized")] extern` (UnityEngine.CoreModule / decompiled). **Partial evidence:** `UnityPlayer.dll` contains Ken Perlin's reference permutation table (151,160,137,91,90,15,…) as 512 int32 values (the 256-entry table written twice) at file offset 0x1BE0BC0 (byte search, this build). That is consistent with classic or improved Perlin noise. **Resolved 2026-09-23, first-hand:** the dumper plugin called `Mathf.PerlinNoise` inside the running game and wrote 262 780 results as float32 bit patterns (`%USERPROFILE%/AppData/valheim-dumper/1.0.15-59f53fb5/goldens/natives-perlin.bin`, copied into `groundtruth/natives/`). The SeedLab transcription matches **262 780 / 262 780 bit for bit** over four blocks: 24 hand-picked 2-D probes (the `abs` fold on either axis, exact lattice points, the 256 period, 2^24 where the fraction is gone), the same 24 through `PerlinNoise1D`, a dense 512x512 grid spanning negatives (262 144), and 588 of the exact arguments `GetBiome`'s four mask offsets and `GetBaseHeight`'s six octaves feed it. So: the table above **is** the one `NoiseNormalized` uses, the fade curve is Perlin's `6t^5-15t^4+10t^3` (not smoothstep), the gradient set is the classic 16-way one keyed on the low 4 bits of the hash, the fold is `abs` (not `floor`), and the normalisation is `(n + 0.69f) / 1.483f` **with a real divide**. Every rival variant was re-measured against these same 262 780 first-hand samples: the reciprocal multiply `(n + 0.69f) * (1f/1.483f)` is wrong on **98 674** of them (37.5 %, always by exactly 1 ULP - the earlier figure of "2 in 217 959" was what survived the minimap's half-precision quantisation, not the real rate), and `n*0.5f + 0.5f`, no normalisation, `+0.70f` and `/1.48f` are each wrong on all **262 780**. `PerlinNoise1D(x)` is bit-identical to `PerlinNoise(x, 0f)`.
- `UnityEngine.Random`: `InitState`, `Range(float,float)` and `RandomRangeInt` are `extern`, as are `get_state_Injected` / `set_state_Injected` and `GetRandomUnitCircle` (which is what the `insideUnitCircle` property calls). `Random.State` holds four ints `s0..s3` (UnityEngine.CoreModule / decompiled), which is consistent with a xorshift128 generator. **Those four ints are `private`, each carrying `[SerializeField]`** — a dumper cannot read them as fields and needs `JsonUtility.ToJson(Random.state)` (which works *because* of the attributes), reflection over the non-public instance fields, or an unsafe reinterpret of the 16-byte struct. Note also that the float overload's parameter is named `maxInclusive` (`extern float Range(float minInclusive, float maxInclusive)`), so "it lerps when `min > max`" is an assumption, not something managed code states. **Resolved 2026-09-23, first-hand** (`goldens/natives-random.json`, recorded in the running game; SeedLab replayed it at **276/276 traces, 1980/1980 draws, 268/268 `InitState` seeds**, checking the result bits *and* the four state words after every draw). `InitState(s)` is `s0 = s; s1 = s0*1812433253 + 1; s2 = s1*...; s3 = s2*...`; the step is xorshift128 with shifts 11, 8, 19; `value` is `(Next() & 0x7FFFFF) * (1f/8388607f)`; `Range(int,int)` is `min + Next() % (uint)(max-min)` (and the mirrored form when `min > max`); **`Range(float,float)` is `(1f - f)*max + f*min`** - the forward form `min + f*(max-min)` and `Mathf.Lerp` are both 31 ULPs away on `Range(60f,100f)` from `InitState(744350289)` (game 0x42BF3B2E, they give 0x4280C4D2), and the third candidate `max + f*(min-max)` is separated by the reversed call `Range(100f,60f)` (game 0x4280C4D3, it gives 0x4280C4D2). **`Range(a,a)` consumes a draw for the float overload and NOT for the int overload** (measured directly as `Random.state` before/after: `Range(5,5)` and `Range(0,0)` leave all four words untouched, `Range(20f,20f)` does not). `insideUnitCircle` is **exactly two draws, no rejection sampling**, `x = cos(a)*r`, `y = sin(a)*r` with `a = Range(0f, 2*pi)` and `r = sqrt(Range(0f,1f))` - the component order is confirmed, swapping x and y misses all 16 components of the 8 recorded pairs. Seven draws after `InitState(seed)` give all the offsets and seeds, in the order offset0..3, riverSeed, streamSeed, **offset4 last** - re-confirmed on 268 separate seeds. Rivers and streams consume thousands more. **Still Unverified:** whether the native `cos`/`sin` inside `insideUnitCircle` is the float `cosf`/`sinf` or a double-precision path. The 8 recorded pairs match `(float)Math.Cos((double)a)` bit for bit, but `MathF.Cos(a)` matches them too: the two forms differ on only **21 694 of the 8 388 608 angles `Range(0f, 2*pi)` can produce (0.2586 %), always by exactly 1 ULP**, so 8 samples had a 98 % chance of agreeing by coincidence. It reaches nothing but `GetTerrainDelta`, so it cannot move a biome or a height - only whether a candidate location point passes its terrain filter. **Narrowed further 2026-09-23:** the end-to-end oracle this note asked for now exists and passes - the fresh world's **12 228 / 12 228** location instances reproduce bit-exactly (`zones-locations-vegetation.md` 3.7) with the `(float)Math.Cos((double)a)` form, and `GetTerrainDelta` spends ten of these draws on every candidate point. That is a strong confirmation but still not a *discrimination*: nobody has re-run that gate with the `MathF.Cos/Sin` spelling to see it fail. Closing it means exactly that negative control (or ~2000 recorded draws). **Resolved 2026-09-22:** whether `Range(20f,20f)` (the width draw for every stream point) consumes a draw *does not matter* — `RenderRivers` is the last RNG consumer inside `PlaceRivers`/`PlaceStreams`, which then restore `Random.state`, so its draws never influence anything else, **Corrected 2026-09-22:** the original wording added "and for streams the value is 20 either way", which is **false**. `Range(float,float)` computes `(1f - f) * max + f * min` in float32 (it is native code, so the Mono R8 rule does *not* apply), and that is two separately rounded products, not an identity: over the 2^23 possible draws, **2.50 % of them make `Range(20f,20f)` return 20.000002f**, one ULP above 20 (measured; only those two values ever occur — 10 205 of 392 809 stream-radius points in `asdasdasd`, 9 302 of 376 841 in `testworldclaude`). The *conclusion* still holds, but only for the state-restore reason, not because the value is constant. Practical consequence: **never use `w == 20f` to tell a stream point from a river point** — it misses about one in 40. The river width draws inside `PlaceRivers`' `RenderRivers` still set the per-point radii and so do affect terrain.

**Practical recommendations:**
1. *Inside a mod:* call the public `WorldGenerator.instance.GetBiome(x,z)` / `GetHeight(x,z)` / `GetBiomeHeight(...)`. They are cheap enough to sample and work for unloaded areas.
2. *External tool, biomes only:* the only RNG output needed is the five offsets. A tiny mod can log them via reflection, since `m_offset0..4` are private instance fields, and `Mathf.PerlinNoise` is then the only native piece left.
3. *External tool, heights:* besides the offsets you also need the river point set. Either re-implement `UnityEngine.Random` exactly, or have a mod dump the private `m_riverPoints` (or `GetRivers()`/`GetLakes()` plus the per-point radii, which are random).
4. *Validation data:* a mod dumping `GetBiome` and `GetHeight` on a grid, or the minimap height cache. The height cache is half precision, but the comparison can still be **exact** — round your float to half the way Unity does (see the `Mathf.FloatToHalf` bullet below) rather than applying a tolerance.

**Measured against the game, 2026-09-22 (first numbers in this file that were not derived by reading code).** A full C# port of `WorldGenerator` + `DUtils` + `FastNoise` (SeedLab, `src\SeedLab.WorldGen`) was run over all 2048² = 4 194 304 minimap pixel centres of the user's world `asdasdasd` (seed text `MWd8eV6svz`, int seed −1772362158, worldGenVersion 2, pixel size 12, centres `(j−1024)·12+6`) and compared with that world's own `cacheMinimapBiome` / `cacheMinimapHeight`:

- **Biome: 0 mismatches** over the 2 542 492 pixels whose colour is unambiguous, and all 1 651 812 white pixels resolved to Ocean (1 406 660), DeepNorth (166 037) or Mountain (79 115) — a total that equals the white count exactly. So `GetBaseHeight`, `GetBiome`, `WorldAngle`, `IsAshlands`, `IsDeepnorth` and the four mask noises are confirmed bit-exact for this seed, and with them the ported `Mathf.PerlinNoise` and the seven constructor `UnityEngine.Random` draws.
- **Height: 4 194 304 of 4 194 304 pixels bit-identical after half rounding** (re-measured
  2026-09-23). Before the Mono-R8 correction below closed the gap it was **4 194 299 of
  4 194 304** (1.2 ppm differ); the diagnosis that follows is kept, because it is what located it. Each of the five differs by exactly one half-ulp (≤ 0.0625 m), and **all five lie inside a river** (`GetRiverWeight` > 0, widths 50–87 m, i.e. rivers rather than the fixed-20 m streams). That points at the river point field, not at any height formula. **Corrected 2026-09-22 (same day):** the first suspect named here, the `Random.Range(60f, 100f)` / `Range(60f, widthMax)` radius draw, is **ruled out** — see the full-precision measurement below; reproducing the differences needs a point radius to move by ~100 float ulps, and the candidate interpolation forms differ by at most one. Lakes 111, rivers 140, pass-1 stream list 2135, 23 380 occupied river-grid cells for this seed.
- **`UnityEngine.Mathf.FloatToHalf` breaks ties AWAY FROM ZERO**, not to even. It is a native extern (`[FreeFunction(IsThreadSafe = true)] extern ushort FloatToHalf(float)`) reached from `Utils.FloatsToCompressedHalfBuffer`, which is what writes `cacheMinimapHeight`. Evidence: of 163 height pixels that disagreed when the port's value was rounded with .NET's `(Half)` cast (which rounds ties to even), **158 were exactly on the half midpoint and every one of them matched the game once the tie was broken away from zero**; the count drops 163 → 5. Consistent with a "mantissa + 0x1000, then shift" implementation. **Confirmed 2026-09-23 on an adversarial set** the dumper ran through the real `Mathf.FloatToHalf` (`goldens/natives-half.json`, 1635 values: signed zeros, exact midpoints at 1025/1027/2049/-2049/2051, the smallest normal and subnormal halves, 65504/65519/65520/-65520/65536, `float.MaxValue`, `float.Epsilon`, NaN, both infinities, and an 0.5 m sweep of real `GetBiomeHeight` magnitudes from -400 to +400). **1634/1634 finite inputs match .NET's `(Half)f` with exact midpoints redirected away from zero**, including negatives; plain ties-to-even matches only 1632/1634. So the rule now holds for negative values too, not just positive ones. **Unverified:** the full native algorithm. **New, and the one place Unity and .NET really differ:** `FloatToHalf(float.NaN)` (0xFFC00000) returns half **0xFF00**, whereas `(Half)float.NaN` gives 0xFE00 - both are NaN, only the payload differs, and Unity's own `HalfToFloat(0xFF00)` is 0xFFE00000, so not even Unity round-trips the payload. Harmless for world generation: `GetBiomeHeight` cannot produce a NaN at finite coordinates and neither ground-truth minimap cache holds a NaN or infinity code.

**Hold-out confirmation and a full-precision oracle, 2026-09-22 (SeedLab acceptance suite,
`tests/SeedLab.Acceptance.Tests`).** The sweep above was repeated on a second
world the port had never been tuned against — `testworldclaude`, seed text `hnBd9gJf2G`, int seed
319 486 907 — and extended with a second, independent oracle.

- **Hold-out biome: 0 mismatches** over its 2 562 380 unambiguous pixels, and all 1 631 924 white
  pixels resolved to Ocean (1 391 626), DeepNorth (174 074) or Mountain (66 224). A seed that was
  never used while debugging agreeing exactly is what separates a port from a fit.
- **Hold-out height: 4 194 304 of 4 194 304 bit-identical** (measured 2026-09-23). Before the Mono-R8
  correction it was 4 194 295 of 4 194 304 (2.15 ppm differ), every difference exactly one half-ulp,
  max 0.03125 m, 8 of the 9 inside a river.
- **Seed-independent geometry re-confirmed on both worlds:** exactly **1 788 980** pixels hold the
  −400 m world-edge constant, and that set equals `DUtils.Length(wx, wy) > 10500f` with **0**
  disagreements. `GetBiomeHeight`'s early-out (`return -2f * GetHeightMultiplier()`) is therefore
  reproduced exactly, as is the `double`-accumulating `DUtils.Length`.
- **`Mathf.FloatToHalf`'s away-from-zero tie-break holds on the hold-out world too:** 322 exact
  midpoints, 321 stored as the away-from-zero neighbour, 0 as the even one, 1 as neither (that one is
  itself one of the 9 genuinely differing pixels). Combined with the development world's 318/318, the
  direction is now measured on 640 midpoints across two seeds. **Updated 2026-09-23:** negatives,
  subnormals, 65504/65519/65520, `float.MaxValue`, `float.Epsilon`, signed zeros and both infinities
  *were* then exercised, on the 1 635-value adversarial set the dumper pushed through the real
  `Mathf.FloatToHalf` — 1 634/1 634 finite inputs agree with the away-from-zero rule (see the
  `FloatToHalf` bullet above). **Still Unverified:** the native algorithm itself, which was never read;
  only its behaviour over that domain is measured.
- **The `.db2` location instances are a FLOAT32 oracle for `GetHeight`, and the only one there is.**
  `ZoneSystem.GenerateLocationsTimeSliced` assigns `randomPointInZone.y = WorldGenerator.instance
  .GetHeight(x, z, out var mask)` and `RegisterLocation` stores that `Vector3` unchanged, so every
  stored `y` is `GetHeight(x, z)` exactly, at full precision, including the river pass. Measured
  2026-09-23: **12 314 / 12 314 (100 %)** bit-exact on `asdasdasd`, **12 287 / 12 287 (100 %)** on the
  hold-out, worst difference 0 m. Before the Mono-R8 correction it was 12 244 / 12 314 (99.43 %) and
  12 227 / 12 287 (99.51 %), worst 1.03×10⁻⁴ m (27 float ulps) and 1.79×10⁻⁴ m (47 float ulps) — i.e.
  the port was *not* bit-exact at float precision while already being 99.9998 % exact at half
  precision, because the minimap cache cannot see an error smaller than a half-ulp. That is why the
  float32 oracle was worth building.
- **The float-precision residual, now zero, split cleanly in two while it existed** (kept because it is
  the diagnosis that led to the Mono-R8 correction; both halves are closed):
  1. *River-touched* (53 of 70, and 45 of 60): the dominating river point is off by about **one float
     ulp of its position** (the arithmetic is: a height off by ΔH implies a weight off by
     ΔH / (200·|bed − h|), hence a dominating point off by that times its radius). They spread over 35
     and 31 distinct watercourses out of 140 rivers + 2135 streams, so it is not one bad endpoint.
     **`Random.Range(float,float)`'s interpolation form is ruled out as the cause**: reproducing these
     differences needs the point radius to move by ~100 float ulps, while the two candidate forms
     (`(1−f)·max + f·min`, as disassembled, versus the naive `min + f·(max−min)`) differ by at most one.
  2. *River-free* (17 of 70, and 15 of 60): **every single one is DeepNorth**, and all are 1–2 float
     ulps (≤ 7.6×10⁻⁶ m). `GetDeepNorthHeight` itself is exonerated — re-deriving its chain from
     `GetBaseHeight` and `DUtils.PerlinNoise` reproduces the port bit-for-bit, and five single-change
     variants (`+0.1` as a double, `GetPlainsHeight`'s multiply association, `Clamp01` on the raw base
     height, a float subtract for `h − 0.15`, a float `d·0.1f`) each repair at most 5 of the differing
     instances while breaking 6–460 that already agree. The difference is upstream, in `GetBaseHeight`
     or in the last bits of `Mathf.PerlinNoise`; settling it needs the dumper's base-height grid
     (spec 04 §3.6.2, `GridFlagBaseHeight`) or a dump of `m_riverPoints`.
- **River coverage, for anyone judging how much a height comparison proves:** 352 006 (14.63 %) of
  the 2 405 324 in-world pixels of `asdasdasd` carry a non-zero river/stream weight, and 353 094
  (14.68 %) of the hold-out's. Of the land pixels whose weight exceeds 0.9, **56.3 %** (dev) and
  **58.7 %** (hold-out) have a *game-stored* height inside the 23–30 m band that `AddRivers` lerps
  toward, against **13.3 %** / **12.6 %** for land with no river weight — the game's own cache agreeing
  that a river is where the port puts one.

**Validated against the generator's OWN INTERNAL STATE, 2026-09-23 — three seeds, everything
bit-identical.** The 1.0.15 dumper (`tools\SeedLab.Dumper`) was run in the live
game and reflected `m_offset0..4`, `m_riverSeed`, `m_streamSeed`, `m_lakes`, `m_rivers`, `m_streams`
and the whole `m_riverPoints` dictionary out of a running `WorldGenerator`. SeedLab's port was then
compared field by field, every float as its IEEE-754 bit pattern
(`tools\SeedLab.GoldenCheck`). This tests the pipeline's internals, not its
output, and the two earlier oracles (minimap colours, binary16 heights) could not see any of it.

| seed | capture | lakes | rivers | streams | river cells | river points | floats compared | differing |
|---|---|---|---|---|---|---|---|---|
| −1 772 362 158 `MWd8eV6svz` | menu | 111 | 140 | 2 135 | 23 380 | 675 579 | 2 702 316 | **0** |
| 319 486 907 `hnBd9gJf2G` | menu | 119 | 161 | 2 059 | 23 262 | 677 094 | 2 708 376 | **0** |
| 75 539 276 `ClaudeTest` | in-world | 126 | 183 | 2 162 | 26 079 | 764 577 | 3 058 308 | **0** |

- The five offsets, both RNG seeds and the three `VersionSetup` constants are bit-identical on all
  three seeds, and the dumper's independent 7-draw replay on a scratch `Random` stream matches the
  port's `UnityRandom` draw for draw. The order off0, off1, off2, off3, riverSeed, streamSeed, **off4
  last** is confirmed from the running game, not only from decompiled IL.
- The lake list matches in **count, order and both coordinates**, which pins `FindLakes`' 157×157
  sampling *and* `MergePoints`' averaging order and swap-removes — the part most likely to drift,
  because it is order-dependent and not a true centroid.
- The river and stream lists match in **order** with every endpoint, centre, width and curve
  parameter bit-identical. These come from thousands of chained `Random` draws, each conditional on a
  height comparison, so this is an exact replay of `PlaceRivers`' 2000 m / 5000 m link search,
  `IsRiverAllowed`, `FindRandomRiverEnd`, and both `PlaceStreams` passes including the 3000 attempts
  with their 100-try start/end loops.
- `m_riverPoints` matches **cell for cell and point for point, in order inside each cell** — 2 117 250
  points over 72 721 cells across the three seeds, 8 469 000 floats (`p.x`, `p.y`, `w`, `w2`), 0
  differing. The in-cell order is load-bearing (`GetWeight` accumulates in float), and this confirms
  `RenderRivers`' emission order: rivers in `m_rivers` order by step by grid scan, then pass-1
  streams, then pass-2 streams. It also confirms that the discarded return value of
  `PlaceStreams(isDN: true)` still contributes its points to the grid.
- Side effect: the `Range(20f,20f)` finding above is confirmed from the game's own data. The dumped
  stream-point radii contain both 20f and 20.000002f exactly where the port produces them.
- **Still not tested by this:** `GetBiome` / `GetHeight` grids. This dump carries no
  `worldgrid-*.bin` (the dumper was run without `grid=`), so the base-height grid that would settle
  the last `Mathf.PerlinNoise` bits still does not exist.
- **Third seed.** 75 539 276 (seed text `ClaudeTest`, world `ClaudeTestWold2`) had never been used;
  the port had only ever been checked on the two ground-truth worlds. Its 12 228 fresh location
  instances are also a float32 `GetHeight` oracle — **12 228 / 12 228 bit-exact**, including all 1 629
  that sit in a river or stream. A fresh world is the better oracle: nothing has been pruned by
  exploration, so the unplaced candidates are there too.

## 10. Modder notes and pitfalls

- **Accessibility:**
  - Public instance: `m_world`, `GetBiome`, `GetHeight`, `GetBiomeHeight`, `GetPregenerationHeight`, `GetBiomeArea(Vector2s|Vector3)`, `GetBiomeSector`, `GetAshlandsHeight`, `GetNormal`, `GetAverageNormal`, `GetTerrainDelta`, `GetSeed`, `GetLakes/GetRivers/GetStreams`, `GetRiverGrid`, `InsideRiverGrid`, `CleanCachedRiverData`.
  - Public static: `instance`, `Initialize`, `Deitialize`, `IsAshlands`, `IsDeepnorth`, `WorldAngle`, `GetAshlandsOceanGradient`, `CreateDeepNorthGap`, `DeepNorthWaveFade`, `InForest`, `GetForestFactor`, `GetHeightMultiplier`.
  - **Private:** `GetBaseHeight`, every `Get<Biome>Height` except the public `GetAshlandsHeight`, `AddRivers`, `CreateAshlandsGap`, `m_offset0..4`, `m_riverSeed`, `m_streamSeed`, `m_riverPoints`, `m_version`, `m_noiseGen` (static), and `m_minMountainDistance`/`minDarklandNoise`/`maxMarshDistance` (instance fields, set in the constructor by `VersionSetup`).
- **Consts are inlined** (`worldSize`, `waterEdge`, `m_waterTreshold = 0.05`, `c_HeightMultiplier = 200`, and so on). Changing them through reflection does nothing; you need a transpiler or a patch on the method. The exceptions are the `public static readonly` fields `ashlandsMinDistance` and `ashlandsYOffset`: they are not consts and are read at runtime (`ldsfld`) by `IsAshlands`, `GetAshlandsOceanGradient`, `CreateAshlandsGap` and `GetAshlandsHeight`, while the Deep North checks use literals. `GetHeightMultiplier()` is a static that returns the literal 200, and `GetBiomeHeight` calls it three times. `c_HeightMultiplier` itself is not read. **Unverified:** whether a Harmony patch on `GetHeightMultiplier()` takes effect, since a 6-byte static like this is a likely JIT-inlining candidate once `GetBiomeHeight` is compiled.
- **Order-sensitive RNG:** the constructor saves and restores `UnityEngine.Random.state`. A `UnityEngine.Random` draw consumed by a patch after `InitState(m_seed)` and before the 7th draw (for example in a patched `FastNoise` setter) shifts every offset and seed drawn after it. `PlaceRivers` and `PlaceStreams` reseed with `InitState(m_riverSeed)` / `InitState(m_streamSeed)`, so a draw during `FindLakes` has no effect. A draw during river placement or a stream pass changes that pass, and through `AddRivers` the stream placement that follows it.
- **Vanilla quirks (verified; do not copy):**
  - `EnvMan.GetBiome` and `EnvMan.UpdateEnvironment` both call `IsDeepnorth(position.x, position.y)` on the camera position, passing altitude instead of z (`TerrainComp.PaintCleared` passes z correctly).
  - `GetBiomeArea(Vector3)` tests the `(64,0,0)` offset twice and never `(-64,0,0)`. Nothing in vanilla calls this overload; location generation uses the `Vector2s` overload.
  - **`GetNormal(Vector2 point, float radius = 1f)` never reads `radius`** — the IL loads `this` and `point` only and always samples ±1 m (`GetNormal(Vector2,Single)` IL_0000–IL_006d, verified 2026-09-22). So `GetAverageNormal(point, radius, metersPerSample)` averages up to 16 *identical* vectors and returns the same direction as `GetNormal`, at n× the cost (n = `min(floor(radius/metersPerSample), 16)`). Each `GetNormal` costs four full `GetHeight` calls.
  - `GetAshlandsOceanGradient` uses `WorldAngle(x, z−4000)`, while `IsAshlands` uses `WorldAngle(x, z)`, so the wobble is slightly out of phase between them.
  - The `waterAlwaysOcean` path compares `GetHeight` (metres) against `oceanLevel` (0.02 by default). No vanilla caller passes `true`.
  - `GenerateBiomes()` is dead code that would throw; `GetEdgeHeight` and `FindClosestRiverEnd` are dead code.
  - (Related, in `AltBiomeWorldData.RandomBiomeFromBiomes`: Plains maps to BlackForest, the Ocean branch tests the Meadows flag, and `Range(0, num-1)` never picks the last flag.)
- **Stale river cache (analysis, refined 2026-09-22):** the single-entry cache (`m_cachedRiverGrid`/`m_cachedRiverPoints`) is not invalidated when `RenderRivers` replaces a cell's `RiverPoint[]` (it always allocates a *new* array) or adds a new key. `FindLakes` and `PlaceRivers` never call `AddRivers`, so the cache is first filled during `PlaceStreams(false)`; it can therefore go stale at exactly two points — the first `GetRiverWeight` of `PlaceStreams(true)`, and the first `GetHeight` after pre-generation finishes — and only when that query lands in the one cached 64 m cell (≈1e-5 per world). It self-heals on the next query elsewhere. Negligible in play, but an external port that wants bit-exact agreement must replicate the cache, not "fix" it.

## Unverified (and why)

Four items that stood here are **now settled** and were removed from this list rather than left beside
their answers (2026-09-23):

- `Mathf.PerlinNoise` and `UnityEngine.Random` — both algorithms are recorded first-hand from the
  running game and reproduced bit-exactly; see §9.
- Serialized prefab values — read out of the loaded components: `ZoneSystem.m_waterLevel` **30**,
  `m_zoneTTL` **10** / `m_zoneTTS` **5** (code 4/4), `m_locationVersion` **32** (code 1), the zone
  prefab's `Heightmap.m_width`/`m_scale` **64 / 1** (code 32/1, so the common belief was right; the
  distant-LOD heightmap is 128 / 20), `Minimap.m_textureSize`/`m_pixelSize` **2048 / 12** (code
  256/64), and `AltBiomeList` — **32** alt biomes, **all enabled**.
- Last-bit behaviour of `Math.Sin`/`Atan2`/`Pow` under Mono — 93/93 identical to .NET 10 on every
  argument world generation uses (§9).
- Whether Mono evaluates float32 arithmetic in single precision — **it does not**: it holds the
  evaluation stack at R8 and narrows only at a `conv.r4` or a store (§9, and pitfalls.md section 9).

What is still open:

- **The full native `Mathf.FloatToHalf` algorithm.** Its tie-break is measured (away from zero, 640
  real midpoints across two seeds plus a 1 634-value adversarial set), but the code was not read, and
  its NaN payload differs from .NET's.
- **The `cos`/`sin` flavour inside `Random.insideUnitCircle`** — confirmed, not discriminated (§9).
- **Client-side minimap cache behaviour** (§8), which depends on a `Minimap.Start` / `RPC_PeerInfo`
  ordering nobody has observed.
- **Whether a Harmony patch on `GetHeightMultiplier()` takes effect** (§10) — a likely inlining
  candidate.
- **Numerical output for a seed nobody has sampled.** The measured blocks in §9 cover three seeds
  (two played worlds and one fresh one) end to end, plus 25 more seeds for the §5.1 seam rule. The
  code-derived parts of this file hold for every seed only in so far as the port that was measured is
  a faithful transcription of them.
