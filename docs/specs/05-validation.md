# 05 — Validation against the real game, and the file-format parsers

Target: Valheim **1.0.15**, network 40, world file version **41**, worldGenVersion **2**.
Every numeric claim below is either quoted from a decompiled member (`Type.Member`) or measured on this
machine by decompressing the real files. Measurements were produced by throwaway scripts in
`…\scratchpad\probe\` (`minimap_probe.py`, `minimap_probe2.py`, `minimap_probe3.py`, `names2.py`,
`files.py`; re-measured independently by `rv_cache.py`, `rv_geom.py`, `rv_stats.py`, `rv_mask.py`,
`rv_saves.py`, `rv_fch.py` — see the Verification section at the end). Anything that could not be
settled is marked **Unverified:** with what would settle it.

Ground truth available offline, on this machine:

| Artefact | Path | What it proves |
|---|---|---|
| minimap cache, world `asdasdasd`, seed `-1772362158` | `%USERPROFILE%\AppData\LocalLow\IronGate\Valheim\worlds_local\asdasdasd\` | biome + height + mask for **4 194 304** world points |
| minimap cache, world `testworldclaude`, seed `319486907` (seed text `hnBd9gJf2G`) | `…\worlds_local\testworldclaude\` | independent hold-out seed, same 4 194 304 points |
| world save `asdasdasd` | `<Steam>\userdata\<accountId>\892970\remote\worlds\asdasdasd\_main.<N>.*` | 12 314 location instances with **float32** positions |
| world save `testworldclaude` | `…\remote\worlds\testworldclaude\_main.1.*` | second seed's metadata |
| character `asda.fch` | `…\remote\characters\asda.fch` | per-world map blob layout, 2 world UIDs |

**Both save locations are read-only for this project.** Steam Cloud syncs them, and
`SaveCollection.KeepOnlyNewest` deletes any unexpected `_main.*` file in a world folder. The save
number moves under you: during this session `asdasdasd` went from `_main.2.*` to `_main.3.*` because
the user was playing. Always resolve the newest complete set at open time; never cache the path.

---

## 1. The minimap cache — exact format, end to end

### 1.1 Who writes it, and when

`Minimap.Update` → `if (!TryLoadMinimapTextureData(ZNet.World.m_seed)) GenerateWorldMap();`
*(Minimap.Update / decompiled, line 662)*. `GenerateWorldMap` writes the four files through
`SaveMapTextureDataToDisk` *(Minimap.GenerateWorldMap, Minimap.SaveMapTextureDataToDisk / decompiled)*.

Directory: `World.GetSaveDirectory(FileHelpers.FileSource.Local)` = `…/LocalLow/IronGate/Valheim/worlds_local/<m_worldName>/`
— **always Local, even for a Steam Cloud world** *(Minimap.Start, line 571:
`ZNet.World.GetSaveDirectory(FileHelpers.FileSource.Local)`; World.GetSaveDirectory line 91 returns
`root + "/" + m_worldName + "/"` — note the trailing slash, so `GetCompleteTexturePath`'s own `"/"`
produces a doubled separator at runtime; build the path from the directory and the name, do not
reproduce the literal string)*. File names are fixed string constants
*(Minimap lines 398/400/402/404: `c_MaskTextureName`/`c_BiomeTextureName`/`c_HeightTextureName`/`c_MetaName`)*,
joined by `GetCompleteTexturePath(root, name) => root + "/" + name` (line 2050), so there is no extension:

```
cacheMinimapBiome     Color32[N*N], gzip
cacheMinimapMask      Color32[N*N], gzip
cacheMinimapHeight    ushort(half)[N*N], gzip
cacheMinimapMeta      8 raw bytes: int32 seed, int32 Version.CachedMinimap (1)
```

The cache is only *read* when `ZNet.World.m_worldVersion == Version.World.DeepNorth (41)`, the meta
seed equals `m_seed`, and the meta version is 1 *(Minimap.TryLoadMinimapTextureData / decompiled)*. It
is written whenever `FileHelpers.LocalStorageSupport == Supported`, regardless of world version — which
is why a cache exists for a Steam Cloud world.

### 1.2 Compression and framing

```csharp
// Utils.Compress  (assembly_utils, public static)
using MemoryStream ms = new MemoryStream();
using (GZipStream gz = new GZipStream(ms, CompressionLevel.Fastest)) gz.Write(input, 0, input.Length);
return ms.ToArray();
```
*(Utils.Compress / Utils.Decompress / decompiled)*

Plain **gzip** (RFC 1952), no length prefix, no ZPackage wrapper — the file *is* the gzip stream.
Observed header on all three buffers of both worlds: `1f 8b 08 00 00 00 00 00 00 0a`
(magic, CM=8 deflate, FLG=0, MTIME=0, XFL=0, OS=0x0A). The gzip trailer's last 4 bytes are the
little-endian uncompressed size, which is safe to use to pre-size the output buffer here (16 MiB max).
`Utils.Compress2`/`Decompress2` are **Brotli** and are *not* used for these files.

Measured, world `asdasdasd`:

| File | on disk | inflated | implied layout |
|---|---|---|---|
| `cacheMinimapBiome` | 149 980 | 16 777 216 | 4 B × 2048² |
| `cacheMinimapMask` | 501 538 | 16 777 216 | 4 B × 2048² |
| `cacheMinimapHeight` | 3 851 430 | 8 388 608 | 2 B × 2048² |
| `cacheMinimapMeta` | 8 | — | `52 e6 5b 96 | 01 00 00 00` |

`0x965be652` as int32 = **-1772362158** = the stored `m_seed` of `asdasdasd`
(`seedText "MWd8eV6svz".GetStableHashCode()`), and the second int is `1` =
`Version.CachedMinimap.Original`. **The meta holds exactly the seed expected.** The hold-out world's
meta reads `(319486907, 1)`, matching its `.fwl2`.

### 1.3 Element encodings

```csharp
public static byte[] ColorsToCompressedBuffer(Color32[] px) => Compress(MemoryMarshal.AsBytes(px.AsSpan()).ToArray());
public static byte[] FloatsToCompressedHalfBuffer(float[] px) {
    ushort[] a = new ushort[px.Length];
    for (int i = 0; i < px.Length; i++) a[i] = Mathf.FloatToHalf(px[i]);
    return Compress(MemoryMarshal.AsBytes(a.AsSpan()).ToArray());
}
```
*(Utils.ColorsToCompressedBuffer / FloatsToCompressedHalfBuffer / decompiled)*

- `Color32` is 4 bytes in **R, G, B, A** order — this is now from the type, not inference:
  `[StructLayout(LayoutKind.Explicit)] struct Color32 { [FieldOffset(0)] int rgba; [FieldOffset(0)] byte r;
  [FieldOffset(1)] byte g; [FieldOffset(2)] byte b; [FieldOffset(3)] byte a; }`
  *(UnityEngine.Color32, UnityEngine.CoreModule.dll / decompiled)*. Corroborated by the measured
  histograms — alpha is 255 on every biome pixel and 0 on every mask pixel, matching `GetPixelColor`
  returning opaque `Color`s and `GetMaskColor` returning `a = 0`.
- The half buffer is little-endian `ushort`, IEEE-754 binary16.
- **`Color` → `Color32` rounds to nearest, HALF TO EVEN, and it does not truncate.** The conversion is
  managed code, so this is settled from the decompiler and needs no dumper:
  ```csharp
  public static implicit operator Color32(Color c) =>
      new Color32((byte)Mathf.Round(Mathf.Clamp01(c.r) * 255f), (byte)Mathf.Round(Mathf.Clamp01(c.g) * 255f),
                  (byte)Mathf.Round(Mathf.Clamp01(c.b) * 255f), (byte)Mathf.Round(Mathf.Clamp01(c.a) * 255f));
  // UnityEngine.Mathf.Round(float f) => (float)Math.Round(f);        <- System.Math.Round, MidpointRounding.ToEven
  ```
  *(UnityEngine.Color32.op_Implicit, UnityEngine.Mathf.Round, UnityEngine.Mathf.Clamp01 /
  UnityEngine.CoreModule.dll / decompiled)*. So implement it as
  `(byte)MathF.Round(Math.Clamp(c.r, 0f, 1f) * 255f)` in .NET — `MathF.Round(float)` is also
  round-half-to-even. **Do not implement `+ 0.5f` then truncate**: that is half-*up* and differs at
  every exact tie (`v*255f == k + 0.5f` → half-up gives `k+1`, the game gives the even one of `k`,`k+1`).
  Note the multiply `Clamp01(x) * 255f` is a **float** multiply, and only then widened to double inside
  `Math.Round`.
  Measured corroboration (`rv_mask.py`, world `asdasdasd`, all 4 194 304 pixels): over the 10 363
  non-Ashlands-coloured pixels whose stored mask byte is `0 < B < 255` — i.e. the pixels that took the
  `new Color(0, 0, Clamp01(GetAshlandsOceanGradient(wx,wy)), 0)` branch and were not clamped —
  round-to-nearest reproduced the stored byte **0 times wrong** while truncation was wrong **5 302**
  times; and the 2 437 750 such pixels with `B == 0` all predicted 0. No exact tie occurs in that set,
  which is why the earlier measurement could not distinguish half-up from half-even — the decompiled
  operator does.
- **Unverified:** the exact rounding mode of `Mathf.FloatToHalf`. It is
  `[MethodImpl(InternalCall)] [FreeFunction(IsThreadSafe = true)] public static extern ushort FloatToHalf(float)`
  *(UnityEngine.Mathf.FloatToHalf, UnityEngine.CoreModule.dll / decompiled)* — native, unreadable from
  managed code. Assume IEEE round-to-nearest-even (what `System.Half`/`(Half)f` does in .NET) and
  **settle it with the dumper plugin**: call `Mathf.FloatToHalf` over a table of adversarial floats
  (exact ties such as `2049f`, `-2049f`, `0.00006103515625f*1.5f`, subnormals, `±65520f`, `±65536f`,
  NaN, ±Inf) and compare against `BitConverter.HalfToUInt16Bits((Half)f)`. The acceptance test in §4
  is written so that a disagreement shows up as a bounded ±1-ULP error, not as a silent pass.

### 1.4 Pixel layout and world coordinates

```csharp
// Minimap.GenerateWorldMap (decompiled)
int num = m_textureSize / 2;            // 1024
float num2 = m_pixelSize / 2f;          // 6
for (int i = 0; i < m_textureSize; i++) {
    float wy = (float)(i - num) * m_pixelSize + num2;
    for (int j = 0; j < m_textureSize; j++) {
        float wx = (float)(j - num) * m_pixelSize + num2;
        Heightmap.Biome biome = worldGenerator.GetBiome(wx, wy);          // oceanLevel 0.02, waterAlwaysOcean false
        float biomeHeight = worldGenerator.GetBiomeHeight(biome, wx, wy, out _);
        int num3 = i * m_textureSize + j;
        array [num3] = GetPixelColor(biome);                  // -> cacheMinimapBiome
        array2[num3] = GetMaskColor(wx, wy, biomeHeight, biome);          // -> cacheMinimapMask
        array4[num3] = biomeHeight;                                        // -> cacheMinimapHeight
    }
}
```

So, with `N = m_textureSize`, `P = m_pixelSize`, index `k = i*N + j`:

```
wx = (j - N/2) * P + P/2          j = 0 … N-1   (east positive)
wy = (i - N/2) * P + P/2          i = 0 … N-1   (north positive)
```

Row-major, **row 0 is the southernmost row** (Unity texture convention; `SetPixels32` fills bottom-up).
Column 0 is the westernmost. Orientation is *proved*, not assumed: `WorldGenerator.IsAshlands(wx,wy)`
is asymmetric in both x and y, and with this mapping it is true for **exactly** the 1 361 539 pixels
that carry the Ashlands colour and for no other pixel (§2). Any transposition or flip destroys that.

Matching accessors: `WorldToPixel(p)` → `px = Utils.RoundToInt(p.x/P + N/2)`, `py = Utils.RoundToInt(p.z/P + N/2)`
*(Minimap.WorldToPixel / decompiled, lines 1817-1819)*. Note `WorldToPixel` rounds while
`GenerateWorldMap` uses the pixel *centre*, so `WorldToPixel(centre of pixel k) == k` holds.

**`Utils.RoundToInt` is not `Math.Round`.** It is
```csharp
public static int RoundToInt(float f) { return (int)(f + 64000.5f) - 64000; }   // Utils.RoundToInt, line 1216
public static int FloorToInt(float f) { return (int)(f + 64000f)   - 64000; }   // Utils.FloorToInt, line 1222
```
*(assembly_utils, Utils / decompiled)*. The `+ 64000` bias is added in **float**, so the fractional part
is quantised before the truncation: near 64000 the float spacing is 2⁻⁸ = 0.00390625, and near 66048
(the `WorldToPixel` range, argument ≈ 0…2048) it is 2⁻⁷ = 0.0078125. Consequences a port must
reproduce, measured by running the two expressions (`rv` probe):
`RoundToInt(100.4999f) == 101` (round-half-even would give 100, half-up would give 100);
`RoundToInt(2.5f) == 3` (round-half-even would give 2); `FloorToInt(163.999999f) == 164`
(`MathF.Floor` gives 163); `FloorToInt(-0.0001f) == 0` (`MathF.Floor` gives -1).
Port them **literally**, as the two expressions above, everywhere `WorldToPixel`, `Minimap.Explore` or
`ZoneSystem.GetZone` is reproduced. This also means `WorldToPixel` is only a round-half-up *to within
±1/256 of a pixel*; do not substitute an exact rounding rule.

### 1.5 `m_textureSize` and `m_pixelSize` — measured, not assumed

Both are serialized Unity fields whose **code defaults are wrong at runtime**:
`public int m_textureSize = 256;` and `public float m_pixelSize = 64f;` *(Minimap fields, lines 232/234)*.

**N = 2048**, three independent ways:
1. `16 777 216 / 4 = 4 194 304 = 2048²` for the biome and mask buffers, and `8 388 608 / 2 = 2048²`
   for the height buffer. 4 bytes/px is forced by `Color32`, 2 by `half`, so N is determined.
2. The same three sizes in the hold-out world `testworldclaude`.
3. The `.fch` map blob for both world UIDs decodes with `textureSize = 2048`
   *(Minimap.GetMapData writes `m_textureSize` first; verified on disk)*, and the runtime log line
   "compressed mapData 8388617" = 2·2048² + 9.

**P = 12.0**, measured from the world-edge discontinuity. `GetBiomeHeight` starts with
```csharp
if (DUtils.Length(wx, wy) > 10500f) return -2f * GetHeightMultiplier();   // = -400
```
*(WorldGenerator.GetBiomeHeight line 1032; GetHeightMultiplier() => 200f)*. So the set of pixels whose
stored half is exactly `-400.0` is exactly the set with `Length(wx,wy) > 10500`. Let
`u(i,j) = hypot(j-1024+0.5, i-1024+0.5)` be the pixel radius in pixel units; then `P = 10500 / u*`
where `u*` is the threshold. Measured over all 4 194 304 pixels:

```
max u over pixels with height != -400 : 874.999714
min u over pixels with height == -400 : 875.000857
=> P ∈ (11.999988, 12.000004)
```

That brackets **12.0** and excludes every other plausible value (the competing hypothesis
"threshold = 10000 m" would give P ∈ (11.428560, 11.428575), which is not a value any prefab would
hold, and would also contradict `waterEdge = 10500f`). Corroboration: the count of `-400` pixels is
**1 788 980** = 42.6526 % against the analytic 1 - π·10500²/24576² = 42.6536 %; and
`public const float c_pixelSize = 12f` *(AltBiomeWorldData line 18 / decompiled)*.
This upgrades the knowledge base's "**Unverified:** `Minimap.m_pixelSize = 12`" to verified.
(Both bracket figures and both percentages re-measured independently in `rv_cache.py`; they hold
bit-for-bit on the hold-out world too, which shares the same `u*` bracket.)

Consequences: the map covers x, z ∈ [-12282, +12282] (pixel centres), i.e. 24 576 m across, comfortably
past the 10 500 m water edge; 1 pixel = 12 m; the world disc occupies 57.3 % of the image.

### 1.6 Measured content (world `asdasdasd`, seed -1772362158)

`cacheMinimapBiome`, 7 distinct RGBA values over 4 194 304 pixels:

| RGBA | count | share | biome (see §2) |
|---|---|---|---|
| (255,255,255,255) | 1 651 812 | 39.382 % | **Ocean, Mountain and DeepNorth (shared)** |
| (123, 32, 32,255) | 1 361 539 | 32.462 % | AshLands |
| ( 51, 51, 51,255) | 402 651 | 9.600 % | Mistlands |
| (231,171,120,255) | 327 479 | 7.808 % | Plains |
| (107,116, 63,255) | 307 927 | 7.342 % | BlackForest |
| (146,167, 92,255) | 80 277 | 1.914 % | Meadows |
| (163,114, 88,255) | 62 619 | 1.493 % | Swamp |

`cacheMinimapMask`, 512 distinct values, alpha 0 on all 4 194 304 pixels; R ∈ {0, 255} only;
G and B each take all 256 values. Top entries: (0,0,0,0) 2 475 151; (0,0,255,0) 1 201 819;
(255,0,0,0) 186 552; (0,255,0,0) 142 747.

`cacheMinimapHeight`: 32 978 distinct half **bit patterns**; min **-400.0**, max **457.0**;
`-400.0` occurs 1 788 980 times, `+0.0` (code `0x0000`) 263 724 times — plus **one** pixel holding
`-0.0` (code `0x8000`), so 263 725 pixels compare equal to zero as floats. Count bit patterns, not
float values (`rv_cache.py`).

Hold-out world `testworldclaude` (seed 319486907): identical 7 colours, identical **inflated** sizes
(16 777 216 / 16 777 216 / 8 388 608 — the *on-disk* compressed sizes differ: 151 432 / 519 630 /
3 856 078), **identical AshLands count 1 361 539 and identical `-400` count 1 788 980**, height
min -400.0 max 410.5, 33 030 distinct halves, `+0.0` 258 017 and `-0.0` 2. Those two counts are pure
geometry and therefore **seed-independent invariants** — see test T2a/T3a.

---

## 2. `GetPixelColor` / `GetMaskColor` — recovering a biome from a pixel

### 2.1 The colour table

```csharp
private Color GetPixelColor(Heightmap.Biome biome) => biome switch {
    Meadows => m_meadowsColor, AshLands => m_ashlandsColor, BlackForest => m_blackforestColor,
    DeepNorth => m_deepnorthColor, Plains => m_heathColor, Swamp => m_swampColor,
    Mountain => m_mountainColor, Mistlands => m_mistlandsColor,
    Ocean => Color.white, _ => Color.white };
```
*(Minimap.GetPixelColor / decompiled)*

Every `m_*Color` except `m_mistlandsColor` is a **public serialized field, so the prefab overrides the
code default**. `m_mistlandsColor` is `private` with no `[SerializeField]`, so it keeps its code value —
and indeed the measured (51,51,51) is exactly `new Color(0.2f,0.2f,0.2f)`. Code defaults vs measured:

| field | code default | as Color32 | **measured on disk** |
|---|---|---|---|
| `m_meadowsColor` | (0.45, 1, 0.43) | (115,255,110) | **(146,167, 92)** |
| `m_ashlandsColor` | (1, 0.2, 0.2) | (255, 51, 51) | **(123, 32, 32)** |
| `m_blackforestColor` | (0, 0.7, 0) | (0,**178**,0) | **(107,116, 63)** |
| `m_deepnorthColor` | (1,1,1) | (255,255,255) | **(255,255,255)** |
| `m_heathColor` (Plains) | (1,1,0.2) | (255,255,51) | **(231,171,120)** |
| `m_swampColor` | (0.6,0.5,0.5) | (153,128,128) | **(163,114, 88)** |
| `m_mountainColor` | (1,1,1) | (255,255,255) | **(255,255,255)** |
| `m_mistlandsColor` | (0.2,0.2,0.2) | (51,51,51) | **( 51, 51, 51)** |
| Ocean | `Color.white` | (255,255,255) | **(255,255,255)** |

The `m_blackforestColor` row was **(0,179,0)** in the first draft; it is (0,**178**,0). `0.7f * 255f`
is exactly `178.5f` — a true tie — and `Mathf.Round` is half-to-**even** (§1.3), so it yields 178. It
is the one place in this document where the two rounding rules visibly disagree, which is a useful
smoke test: if a port's `ColorToColor32((0f, 0.7f, 0f))` gives 179, it implemented `+ 0.5f` and will
be wrong on real data too. (`0.5f * 255f == 127.5f` is also a tie, but 128 is even, so both rules agree
there.)

**Do not hard-code the code defaults.** Hard-code the measured bytes, and have the dumper plugin emit
the live `Minimap` colour fields so a game update that re-tints the map is detected instead of silently
failing every test.

### 2.2 How each colour was pinned to a biome (evidence, not inference)

All from `minimap_probe2.py` / `minimap_probe3.py` over all 4 194 304 pixels:

- **AshLands = (123,32,32).** `WorldGenerator.IsAshlands(wx,wy)` — pure geometry,
  `Length(x, y-4000) > 12000 + WorldAngle(x,y)*100` — is true for 1 361 539 pixels, and the set of
  pixels with this colour is exactly that set (1 361 539 of 1 361 539, and 0 for every other colour).
  Re-derived independently with float32 semantics in `rv_geom.py`: 1 361 539 true, 1 361 539 coloured,
  **0 disagreements**. `GetBiome` tests `IsAshlands` first (after the menu branch, and after the
  `waterAlwaysOcean` branch which `GenerateWorldMap` never takes), so the match is exact by
  construction. Exact source, for the constants and the cast discipline:
  ```csharp
  public static readonly float ashlandsMinDistance = 12000f;   // WorldGenerator line 168
  public static readonly float ashlandsYOffset    = -4000f;    // WorldGenerator line 170
  public static bool IsAshlands(float x, float y) {            // WorldGenerator line 751
      double num = (double)WorldAngle(x, y) * 100.0;
      return (double)DUtils.Length(x, (float)((double)y + (double)ashlandsYOffset)) > (double)ashlandsMinDistance + num;
  }
  ```
- **`IsAshlands` and `IsDeepnorth` do NOT use the same length function.** This is the single easiest
  way to get a port subtly wrong:
  ```csharp
  // DUtils.Length(float,float): the sum of squares is accumulated in DOUBLE
  public static float Length(float x, float y) { return (float)Math.Sqrt((double)x * (double)x + (double)y * (double)y); }
  // UnityEngine.Vector2.magnitude: the sum of squares is accumulated in FLOAT
  public float magnitude { get { return (float)Math.Sqrt(x * x + y * y); } }
  public static bool IsDeepnorth(float x, float y) {           // WorldGenerator line 773
      float num = (float)((double)WorldAngle(x, y) * 100.0);
      return new Vector2(x, (float)((double)y + 4000.0)).magnitude > (float)(12000.0 + (double)num);
  }
  ```
  *(DUtils.Length; UnityEngine.Vector2.magnitude, UnityEngine.CoreModule.dll; WorldGenerator.IsDeepnorth
  / decompiled)*. At map scale `x*x` reaches ~1.5·10⁸, well past 2²⁴, so the float accumulation really
  does round. `GetBiome`'s own `num = DUtils.Length(wx, wy)` and `GetBiomeHeight`'s `> 10500f` early-out
  are the **double** flavour; `FindLakes`' `new Vector2(num2, num).magnitude > 10000f` is the **float**
  flavour. Keep two separate functions in the port and never unify them.
- **Mistlands = (51,51,51).** Its pixels have world radius ∈ [5900, 10000]; `GetBiome`'s Mistlands
  branch requires `num > 6000 + WorldAngle*100` (so ≥ 5900, since `sin` ∈ [-1,1]) and `num < 10000f`
  *(WorldGenerator.GetBiome line 816)*. It is also the only colour whose mask green channel takes
  many values (256 distinct), matching the Mistlands mask branch.
- **Plains = (231,171,120).** Radius ∈ [2901.9, **8000.0**]; the Plains branch requires
  `num > (float)(3000.0 + num2)` and `num < 8000f` (an inline literal; the matching named constant is
  `maxHeathDistance = 8000f`, line 140, but the branch does not read it). The upper bound is hit exactly.
- **Swamp = (163,114,88).** Radius ∈ [2000.0, **6000.0**]; the branch is
  `num > 2000f && num < maxMarshDistance && baseHeight > 0.05f && baseHeight < 0.25f`. The lower bound
  is an inline literal (matching `minMarshDistance = 2000f`, line 126); the upper bound is the **field**
  `maxMarshDistance` (default 6000f, line 128), the only one of the four that `VersionSetup` changes
  (to 8000f for worldGenVersion ≤ 1, line 255). Swamp heights span only 24.312–33.938 m (land mean
  30.630), which is the flat `GetMarshHeight` base of 0.137·200 = 27.4 m plus small noise.
- **BlackForest = (107,116,63).** It is the only colour whose mask red channel is 255 on essentially
  every land pixel (134 578 of 134 631) — `GetMaskColor` returns `s_forestColor` unconditionally for
  BlackForest. Its radius reaches 10 376.6 m, consistent with the unbounded fallback
  `if (num > (float)(5000.0 + num2)) return BlackForest`. (The 53 exceptions are a half-precision
  artefact of the *measurement*, see §4 T4.) Note there is also a *bounded* BlackForest branch just
  above it — `PerlinNoise(m_offset2…) > 0.4f && num > (float)(600.0 + num2) && num < 6000f` — so the
  colour is produced by two different branches and a port that drops either one changes the count.
- **Meadows = (146,167,92).** Minimum radius 8.5 m (it is the centre biome and the final fallback), max
  5098.1 m (`meadowsMaxDistance = 5000f` plus the ±100 angle term — the fallback uses the inline
  literal `5000.0`); its mask red channel is 255 or 0 depending on `InForest`, as `GetMaskColor`'s
  Meadows branch requires.
- **White = Ocean ∪ Mountain ∪ DeepNorth.** 1 651 812 pixels; 999 263 of them satisfy the pure-geometry
  `IsDeepnorth(wx,wy)`; 203 998 have height ≥ 30 m and the maximum is 457 m, which only
  `GetSnowMountainHeight`/`GetDeepNorthHeight` reach. **`IsDeepnorth` is true for 1 361 539 pixels in
  total** (`rv_geom.py`), so the other 362 276 of them are *not* white: they are Ashlands-coloured,
  because `IsAshlands` is tested before `IsDeepnorth`. 999 263 + 362 276 = 1 361 539 and no
  `IsDeepnorth` pixel carries any third colour — a complete partition, which makes T2b a sharp test
  rather than a one-sided one.

### 2.3 The colour collision, and what it means for validation

`Ocean`, `Mountain` and `DeepNorth` all render as pure white, so **a biome index cannot be recovered
from a biome-cache pixel for those three**. The other six are uniquely decodable.

The branch order the white set hides, verbatim from `WorldGenerator.GetBiome(float, float, float
oceanLevel = 0.02f, bool waterAlwaysOcean = false)` (line 779) — this is the unit test §6 item 9 asks
for; the order of the last five tests is load-bearing because several of them can be true at once:

| # | condition | result |
|---|---|---|
| 0 | `m_world.m_menu` | `GetBaseHeight(menuTerrain:true) >= 0.4f ? Mountain : BlackForest` |
| — | `num = DUtils.Length(wx,wy)`; `baseHeight = GetBaseHeight(wx,wy,false)`; `num2 = (float)((double)WorldAngle(wx,wy)*100.0)` | (computed before any test) |
| 1 | `waterAlwaysOcean && GetHeight(wx,wy) <= oceanLevel` | `Ocean` (never taken from `GenerateWorldMap`) |
| 2 | `IsAshlands(wx,wy)` | `AshLands` |
| 3 | `!waterAlwaysOcean && baseHeight <= oceanLevel` | `Ocean` |
| 4 | `IsDeepnorth(wx,wy)` | `DeepNorth` |
| 5 | `baseHeight > 0.4f` | `Mountain` |
| 6 | `PerlinNoise(m_offset0…) > 0.6f && num > 2000f && num < maxMarshDistance && baseHeight > 0.05f && baseHeight < 0.25f` | `Swamp` |
| 7 | `PerlinNoise(m_offset4…) > minDarklandNoise && num > (float)(6000.0+num2) && num < 10000f` | `Mistlands` |
| 8 | `PerlinNoise(m_offset1…) > 0.4f && num > (float)(3000.0+num2) && num < 8000f` | `Plains` |
| 9 | `PerlinNoise(m_offset2…) > 0.4f && num > (float)(600.0+num2) && num < 6000f` | `BlackForest` |
| 10 | `num > (float)(5000.0+num2)` | `BlackForest` |
| 11 | *(fallback)* | `Meadows` |

Every `PerlinNoise(m_offsetN…)` above is literally
`DUtils.PerlinNoise((double)(float)((double)m_offsetN + (double)wx) * 0.0010000000474974513,
(double)(float)((double)m_offsetN + (double)wy) * 0.0010000000474974513)` — the offset is added in
double, **narrowed to float**, then widened again and scaled in double. Note Mistlands is tested
*before* Plains and BlackForest, not after.

Therefore the acceptance test is stated in **colour space, not biome space**: the tool computes
`GetBiome`, maps through the same table, and compares Color32 bytes. That is exactly what the game
stored, it needs no disambiguation, and it still catches every biome error except a confusion strictly
inside {Ocean, Mountain, DeepNorth}. Those three are then separated by the *height* test (T3) and by
the geometric `IsDeepnorth` predicate (T2b), which together leave no blind spot of practical size:
an Ocean↔Mountain confusion changes the height by tens of metres, and an Ocean↔DeepNorth confusion
changes it too (`GetOceanHeight` vs `GetDeepNorthHeight`).

### 2.4 What the mask encodes

```csharp
private Color GetMaskColor(float wx, float wy, float height, Heightmap.Biome biome) {
    if (height < 30f) return new Color(0, 0, Mathf.Clamp01(WorldGenerator.GetAshlandsOceanGradient(wx, wy)), 0);
    switch (biome) {
      case Meadows:    return WorldGenerator.InForest(new Vector3(wx,0,wy)) ? s_forestColor : s_noForestColor;
      case Plains:     return (WorldGenerator.GetForestFactor(new Vector3(wx,0,wy)) < 0.8f) ? s_forestColor : s_noForestColor;
      case BlackForest:return s_forestColor;
      case Mistlands:  { float f = WorldGenerator.GetForestFactor(new Vector3(wx,0,wy));
                         return new Color(0, 1f - Utils.SmoothStep(1.1f, 1.3f, f), 0, 0); }
      case AshLands:   { WorldGenerator.instance.GetAshlandsHeight(wx, wy, out var mask, cheap: true);
                         return new Color(0, 0, mask.a, 0); }
      default:         return s_noForestColor;
    }
}
// s_forestColor = (1,0,0,0)  ->  Color32(255,0,0,0)
// s_noForestColor = (0,0,0,0)
```
*(Minimap.GetMaskColor, fields at lines 138/140 / decompiled)*

Channel meaning, as stored:

- **R ∈ {0,255}** — "forest here" for the map shader. 255 for all BlackForest land, for Meadows land
  with `InForest` (`GetForestFactor < 1.15`), and for Plains land with `GetForestFactor < 0.8`.
- **G ∈ [0,255]** — Mistlands land only: `1 - Utils.SmoothStep(1.1, 1.3, GetForestFactor)`, i.e. a
  continuous "how dense is the mist forest" ramp. **It is `Utils.SmoothStep`, not `DUtils.SmoothStep`**,
  and the two are different functions:
  `Utils.SmoothStep(a,b,x) { float n = Mathf.Clamp01((x-a)/(b-a)); return n*n*(3f-2f*n); }` — all float
  *(Utils line 321)* — whereas `DUtils.SmoothStep` does the same arithmetic in double
  *(DUtils.SmoothStep)*. Use the float one here.
- **B ∈ [0,255]** — two different quantities: for **any** pixel with height < 30 m it is
  `Clamp01(GetAshlandsOceanGradient(wx,wy))`, a closed-form, noise-free 0→1 ramp over 300 m; for
  **AshLands land** it is the alpha of the Ashlands terrain mask (`GetAshlandsHeight(..., cheap:true)`),
  i.e. lava/ash blending. The ramp's zero line is **not** the `IsAshlands` boundary, because the two
  functions feed `WorldAngle` different arguments:
  ```csharp
  public static float GetAshlandsOceanGradient(float x, float y) {          // WorldGenerator line 757
      double num = (double)WorldAngle(x, y + ashlandsYOffset) * 100.0;      // <- OFFSET y
      return (float)(((double)DUtils.Length(x, y + ashlandsYOffset) - ((double)ashlandsMinDistance + num)) / 300.0);
  }
  // IsAshlands uses WorldAngle(x, y)  -- UN-offset y -- for the same +-100 m term.
  ```
  Also note `y + ashlandsYOffset` here is a plain **float** add, while `IsAshlands` writes it as
  `(float)((double)y + (double)ashlandsYOffset)`. Reproducing the offset-`WorldAngle` form is what makes
  T4a pass: with it, all 10 363 non-clamped non-Ashlands B bytes match exactly (`rv_mask.py`).
- **A = 0** everywhere (4 194 304 of 4 194 304, both worlds).

The mask is the best *isolated* test of several sub-systems (see T4), because its B channel below sea
level is pure closed-form geometry: 1 201 819 pixels are exactly `(0,0,255,0)` and 2 475 151 are
exactly `(0,0,0,0)`, with 21 681 non-zero-B pixels inside AshLands land.

---

## 3. The world save as ground truth (`_main.<N>.db2`)

### 3.1 What it holds

`ZNet.SaveWorldThread` writes `int 41`, `double m_netTime`, then `ZoneSystem.Save`, `RandEventSystem.Save`,
`PersistentEventSystem.Save`. `ZoneSystem.Save` writes one `int length` + gzip blob *(ZoneSystem.Save /
decompiled, lines 987-1025)* containing, in order:

1. `int n` + n × `Vector2s` generated zones
2. `int m_locationVersion`
3. `int n` + n × `string` global keys (server-option keys are removed first)
4. `bool locationsGenerated`
5. `int n` + n × { `int prefabNameHash`, `float x`, `float y`, `float z`, `bool placed` }

The hash is `item.m_location.m_prefabName.GetStableHashCode()` — the same string hash as the seed.

Measured on `asdasdasd` `_main.3.db2` (146 725 bytes; ZoneSystem blob 146 669 → 209 803 inflated;
netTime 2222.679928; the 40 bytes after it are decoded in §5.3):

```
generated zones      112
m_locationVersion    32          (code default is 1 — the ZoneSystem prefab overrides it)
global keys          0
locationsGenerated   true
location instances   12 314  in  12 314 distinct zones  (exactly one per zone)
distinct prefab hashes 176 ;  44 instances have placed = true
y ∈ [15.025, 457.678] ; 1 474 instances below 30 m ; none at exactly 0
|x - 64·zx|, |z - 64·zy| ≤ 28.8513  (always < 32)
radius from origin ∈ [70.6, 10292.7]
```

### 3.2 Why the positions are a *bit-exact* oracle for `GetHeight`

In `ZoneSystem.GenerateLocationsTimeSliced` the candidate point is built as

```csharp
Vector3 randomPointInZone = GetRandomPointInZone(zoneID, maxRadius);   // zonePos + Random.Range(±(32-r)) in x and z, y = 0
...
randomPointInZone.y = WorldGenerator.instance.GetHeight(randomPointInZone.x, randomPointInZone.z, out var mask);
...
RegisterLocation(location, randomPointInZone, generated: false);
```
*(ZoneSystem.GetRandomPointInZone, GenerateLocationsTimeSliced lines 1954/1975/2073, RegisterLocation line 2596)*

`GetRandomPointInZone` is
`Random.Range(-32f + locationRadius, 32f - locationRadius)` for x and z — the **float** overload, which
is inclusive at both ends, unlike the int overload used in the `WorldGenerator` constructor
*(ZoneSystem.GetRandomPointInZone line 2166)*.

`RegisterLocation` stores the `Vector3` unchanged. `PlaceLocations` later copies it into a local
(`Vector3 p = value.m_position; GetGroundData(ref p, …)`) and only sets `value.m_placed = true` — it
**never writes back a new position** *(ZoneSystem.PlaceLocations lines 2198-2229)*. `m_snapToWater`
and the slope rotation likewise affect only the local `p`.

So every one of the 12 314 stored `(x, y, z)` satisfies **`y == WorldGenerator.GetHeight(x, z)`
exactly, as float32**, at generation time, including the river and stream passes. This is the single
most valuable oracle in the save: 12 314 full-precision samples spread over the whole world
(radius 70.6 → 10 292.7 m, heights 15.025 → 457.678 m), and it needs **no** reproduction of the
placement RNG. (`rv_saves.py` re-measured all of these figures from `_main.3.db2`.)

Two caveats that do not affect T5 but do affect T8: the filter chain *after* `randomPointInZone.y` is
assigned still consumes RNG — `WorldGenerator.GetTerrainDelta` draws `UnityEngine.Random.insideUnitCircle`
**ten times** per surviving candidate *(WorldGenerator.GetTerrainDelta)*, and the `m_surroundCheckVegetation`
branch calls `GetHeight` 6 × `m_surroundCheckLayers` more times (no RNG). And the per-type seed uses
`location.m_prefab.Name`, while the save writes `location.m_prefabName.GetStableHashCode()`; those are
the same string — `ZoneSystem` assigns `location.m_prefabName = location.m_prefab.Name` *(ZoneSystem
line 917)*.

### 3.3 What the save cannot validate

- **It is not the complete candidate set.** When a `m_unique` location is placed, `RemoveUnplacedLocations`
  deletes every remaining unplaced instance of that type *(ZoneSystem.PlaceLocations line 2234 →
  RemoveUnplacedLocations line 2243)*. Those candidates are gone from the file.
- **Instances whose hash is unknown to the running game are dropped on load** — `ZoneSystem.Load`
  calls `GetLocation(hash)` and skips with "Failed to find location" when it is null. A save that has
  been opened by a modded or updated game has already lost them.
- **A `m_locationVersion` bump discards and regenerates every unplaced instance**
  (`Load` sets `m_locationsGenerated = false` on mismatch; `GenerateLocationsTimeSliced` begins with
  `ClearNonPlacedLocations()`), while already-placed ones survive. Our file is at version 32.
- **Placement order is state-dependent**: candidate zones come from
  `ZNet.World.m_biomeData.GetRandomPointByBiomes[AboveSeaLevel](location.m_biome)` (the AltBiome grid),
  zones already in `m_locationInstances` are skipped, `IsZoneGenerated(zoneID)` excludes the 112 zones
  the players had already visited, and `HaveLocationInRange` consults previously registered instances.
  Each type is seeded independently with `worldSeed + prefabName.GetStableHashCode()`
  *(GenerateLocationsTimeSliced line 1880)*, but the *list and its order* come from prefab data.
  The order is not simply `m_locations`: the outer pass does
  `m_locations.OrderByDescending(a => a.m_prioritized).ToList()` (a **stable** LINQ sort, so prioritized
  types keep their relative order and come first), then removes every entry with `!m_enable ||
  m_quantity == 0`, and iterates that list forward *(ZoneSystem.GenerateLocationsTimeSliced lines
  1715-1742)*. The candidate zone for each attempt is
  `GetZone(AltBiomeWorldData.MapSpaceToWorldSpace(ZNet.World.m_biomeData.GetRandomPointByBiomesAboveSeaLevel(location.m_biome)))`
  when `m_minAltitude >= 0f`, `…GetRandomPointByBiomes(…)` otherwise, and `GetRandomZone(maxRange)` when
  `m_centerFirst` (with `maxRange` starting at `m_minDistance` and incremented by 1 per attempt)
  *(line 1919)*.
- **`m_placed` tells you where players have been, not where things are**: only 44 of 12 314.
- Terrain modifications, built pieces and every other runtime change live in the `.chunk` files, not
  here, and are irrelevant to generation.

### 3.4 Prefab names are recoverable offline — all 176 of them

The `.db2` stores only hashes, but the names can be recovered without the game running:
harvest candidate strings from `valheim_Data\StreamingAssets\SoftRef\manifest` and `manifest_extended`
(YAML-ish text with `path in bundle: Assets/world/Locations/<Biome>/<Name>.prefab` entries — 256 such
location prefabs) plus ASCII tokens from the 799 files in `SoftRef\Bundles\`, hash each with
`GetStableHashCode`, and look the save's hashes up. Result on `asdasdasd`:
**176 of 176 distinct hashes resolved, each by exactly one candidate string** (`names2.py`).
Cross-checks that the mapping is real, not coincidence: `StartTemple = -1544986047` and
`Eikthyrnir = -316818231` match the knowledge base's independently recorded values, and 175 of the 176
names are themselves files under `Assets/world/Locations/`.

Examples (hash → name, instances, placed): `-1544986047` StartTemple 1/1; `-316818231` Eikthyrnir 3/1;
`-146537656` Bonemass 5/0; `-221799126` GoblinKing 4/0; `1828406738` GDKing 4/0; `1225607547` Dragonqueen 3/0;
`-201398907` FaderLocation 3/0; `-1918492477` DN_Bossroom 3/0; `304861589` InfestedTree01 700/0;
`950643877` Mistlands_RoadPost1 500/0; `1937790547` VoltureNest 350/0; `-1678967404` Crypt2 200/0;
`-516167990` Crypt4 175/0; `-1309228862` SunkenCrypt4 175/0; `-1965863430` TrollCave02 200/0;
`-547648914` Runestone_Meadows 100/6; `259975600` Dolmen01 100/6; `663260127` Dolmen02 100/8;
`-902823814` Dolmen03 50/4; `1705714113` ShipSetting01 100/6; `-1587608451` StoneCircle 25/2;
`-119798395` Vendor_BlackForest 10/0; `1221023754` Hildir_camp 10/0; `103120399` BogWitch_Camp 10/0.
The full table is `…\scratchpad\probe\names2.txt`.

The shipped tool should still prefer the **dumper plugin's** `ZoneSystem.m_locations` table (name,
hash, biome mask, quantity, all the filters), because that is what location *reproduction* needs
anyway; the manifest scan is the offline fallback and a good cross-check of the dump.

---

## 4. Acceptance tests, ranked

Ranking is by (diagnostic power) × (cost to run) × (independence from things the tool cannot yet do).
T0–T5 are the release gate; T6–T9 are stretch. Every test runs against **both** cached worlds —
`asdasdasd` (development) and `testworldclaude` (hold-out, never used while debugging). A test that
passes on the development seed and fails on the hold-out means the implementation was fitted, not
ported.

---

**T0 — Seed hash.** `"MWd8eV6svz" → -1772362158`, `"hnBd9gJf2G" → 319486907`, `"j" → 372029384`,
**`"" → 371857150`**. Also assert `unchecked` 32-bit wraparound on a long random string.
*Pass:* exact, all cases. *Isolates:* `StringExtensionMethods.GetStableHashCode`.
*If it fails:* nothing else can pass; stop.
**Correction:** the empty string does **not** hash to 0. `GetStableHashCode("")` runs no loop iteration
and returns `unchecked(5381 + 5381 * 1566083941)` = **371 857 150** (computed in `hashchk.py`, a direct
port of *StringExtensionMethods.GetStableHashCode / assembly_utils*). The 0 is a *separate* rule one
level up, in the world constructor:
`m_seed = ((!(m_seedName == "")) ? m_seedName.GetStableHashCode() : 0);` *(World..ctor, line 73)*.
Test both: `GetStableHashCode("") == 371857150` **and** `SeedFromSeedText("") == 0`. Conflating them
gives every empty-seed world the wrong map.

---

**T1 — The seven RNG draws.** `Random.InitState(m_world.m_seed)` then
`m_offset0..3 = Range(-10000,10000)`, `m_riverSeed = Range(int.MinValue,int.MaxValue)`,
`m_streamSeed = Range(int.MinValue,int.MaxValue)`, `m_offset4 = Range(-10000,10000)`, in that order
*(WorldGenerator..ctor, draws at lines 223-229; the ctor begins at 205)*.
*Pass:* all seven values equal the dumper's reflected values, for both seeds.
*Isolates:* the port of `UnityEngine.Random`. Precisely: **one** overload,
`public static int Range(int minInclusive, int maxExclusive) => RandomRangeInt(min, max);`
*(UnityEngine.Random / UnityEngine.CoreModule.dll / decompiled)* — `maxExclusive`, so the offsets are
drawn from [-10000, 9999] and the two seeds from [int.MinValue, int.MaxValue-1]; `int.MaxValue` is
never produced. The **float** overload `Range(float minInclusive, float maxInclusive)` is a different
extern and is not used here — it first appears in `FindStreamStartPoint`/`FindStreamEndPoint` and
`GetRandomPointInZone`, so T3 and T8 exercise it, not T1.
**Unverified:** the underlying generator. `InitState`, `Range(int,int)` (via `RandomRangeInt`) and
`Range(float,float)` are all `[MethodImpl(InternalCall)] [FreeFunction]` externs, and `Random.state` is
an opaque 4×int struct `{s0,s1,s2,s3}` — consistent with a 128-bit xorshift, but the algorithm and the
seeding schedule cannot be read from managed code. The dumper must emit both a `state` dump and long
draw sequences from both overloads, not just the seven values.
Context the port needs: the ctor **saves and restores** `Random.state` around itself, and calls
`Pregenerate()` (which reseeds from `m_riverSeed`/`m_streamSeed` and restores) between the seventh draw
and the restore, so reflecting the seven fields after construction is safe.
*Status:* **Unverified** until the dumper runs — the expected values are not on disk anywhere.
*Diagnostic value:* enormous. Without it, a T2 failure is ambiguous between "Random wrong" and
"Perlin wrong", and those need completely different fixes. Run this first.

---

**T1b — `Mathf.PerlinNoise` conformance.** (Added by review; the spec previously did not list this as a
risk at all.) Every biome boundary and every terrain height in the game goes through
```csharp
[MethodImpl(MethodImplOptions.InternalCall)]
[FreeFunction("PerlinNoise::NoiseNormalized", IsThreadSafe = true)]
public static extern float PerlinNoise(float x, float y);
```
*(UnityEngine.Mathf.PerlinNoise, line 57, UnityEngine.CoreModule.dll / decompiled;
`[NativeHeader("Runtime/Math/PerlinNoise.h")]` on the class, line 12)*. It is **native, exactly like
`Mathf.FloatToHalf`, and it is the single largest unverified dependency in the whole port** — a
mis-ported gradient table, a different hash, a different fade curve or a different normalisation to
[0,1] makes every T2/T3/T4b/T5 number wrong, and the failure will look like "the offsets are wrong".
`DUtils.PerlinNoise(double x, double y)` merely casts to float and calls it *(DUtils.PerlinNoise)*, so
there is no managed implementation anywhere to copy.
*Test:* the dumper emits `Mathf.PerlinNoise(x, y)` for a table of at least a few thousand pairs —
including the exact argument values `GetBaseHeight` produces (`(wx + 100000 + m_offset0) * 0.002 * 0.5`
and friends, which are large and therefore coarsely spaced), negative arguments, integer and
half-integer lattice points, arguments beyond ±10⁵, and 0 — and the port must reproduce every one
bit-exactly.
*Pass:* exact, all pairs. *Run it immediately after T1 and before T2.*
*If it fails:* nothing downstream of `GetBaseHeight` can pass, so stop and fix the noise first.

---

**T2 — Biome colour agreement over all 4 194 304 minimap pixels.**
For each `(i,j)` compute `wx, wy` as in §1.4, then `GetBiome(wx,wy)` → colour table → `Color32`;
compare all three bytes against `cacheMinimapBiome`.
*Pass:* **4 194 304 / 4 194 304 = 100.000000 %.** Nothing less ships.
*Reading the failure rate:*

| agreement | almost certainly |
|---|---|
| ~39 % (everything white) | `GetBiome` throwing/short-circuiting, or the colour table wrong |
| 32–33 % | only the geometric Ashlands branch is right → Perlin or the offsets are broken |
| 60–95 % | one or more of `m_offset0/1/2/4` wrong (T1), or a wrong distance constant |
| 99.9 – 99.999 % | Perlin structurally right, arithmetic differs by ≲1 ULP |
| 100 % | ship it |

*Boundary rule for the 99.9 % case:* classify each mismatching pixel as *boundary* (at least one
4-neighbour in the cache has a different colour) or *interior*. Boundary-only mismatches are
float-ordering noise in `DUtils.PerlinNoise`/`GetBaseHeight` and are still a defect, but a bounded
one; **any interior mismatch is a logic bug** (wrong constant, wrong test order, wrong operator).
Record both counts.
*Sub-tests, each isolating one thing:*
- **T2a — Ashlands region (no noise, no RNG).** The 1 361 539 pixels where `IsAshlands(wx,wy)` is true
  must be exactly the Ashlands-coloured set, and the count must be **1 361 539 for any seed** (verified
  identical across both worlds; re-derived from scratch in `rv_geom.py` with 0 disagreements).
  *Isolates:* `DUtils.Length` (**double** accumulation), `WorldAngle`
  (`(float)Math.Sin((float)((double)(float)Math.Atan2(wx,wy)*20.0))` — note the *two* separate narrowings
  to float, one after `Atan2` and one after the `* 20.0`), and the float/double cast discipline. A
  failure here means the port's float semantics are wrong and every other test is meaningless.
  It does **not** exercise `Vector2.magnitude` — that is T2b's job.
- **T2b — DeepNorth geometry.** `IsDeepnorth(wx,wy)` must be true for exactly **1 361 539** pixels, of
  which exactly **999 263** are white and exactly **362 276** are Ashlands-coloured, and **none** carry
  any other colour (`asdasdasd`, `rv_geom.py`). Check all three numbers, not just the first: the
  Ashlands overlap is what proves the branch order, since `IsAshlands` is tested *before* `IsDeepnorth`
  and `Ocean` (`baseHeight <= oceanLevel`) is tested *between* them, so a DeepNorth-geometry pixel can
  legitimately be AshLands-coloured or white-as-Ocean.
  *Isolates, in addition to T2a:* `UnityEngine.Vector2.magnitude` — **float** accumulation of `x*x+y*y`,
  unlike `DUtils.Length`. A port that used `DUtils.Length` here passes T2a and fails only on the thin
  boundary ring, which is exactly the kind of error the 100 % gate is for.
- **T2c — per-offset regions.** Each biome-selection branch reads exactly one offset, so the per-colour
  counts localise a wrong draw: Swamp 62 619 → `m_offset0`; Plains 327 479 → `m_offset1`;
  BlackForest 307 927 → `m_offset2`; Mistlands 402 651 → `m_offset4`. Caveat: `GetBaseHeight` also
  reads `m_offset0` and `m_offset1`, so a wrong `m_offset0`/`m_offset1` moves *every* colour, while a
  wrong `m_offset2`/`m_offset4` moves only its own. Use that asymmetry to tell the two cases apart.
  (`m_offset3` is not used by `GetBiome` at all — it is exercised only by T3/T5.)
*Note:* `GetBiome` never consults rivers — `GetBaseHeight` (lines 884-936) does not call `AddRivers`
(`AddRivers` is defined at line 937 and called only from the per-biome height functions, at lines
1084, 1109, 1126, 1144, 1180, 1212, 1293, 1325, 1350 and **1375**). `GetBiome` also never calls
`GetHeight`: the only such call sits behind `if (waterAlwaysOcean && …)` (line 792), and
`GenerateWorldMap` calls `GetBiome(wx, wy)` with the defaults `oceanLevel = 0.02f,
waterAlwaysOcean = false`. So T2 is a **river-free, height-free** test of Perlin + the offsets. That is
precisely what makes it the right test to run before T3.

---

**T3 — Height agreement over all 4 194 304 pixels.**
Compute `GetBiomeHeight(biome, wx, wy, out _)`, convert with the same half conversion, compare the
16-bit pattern to `cacheMinimapHeight`.
*Pass (tier A):* 4 194 304 / 4 194 304 bit-identical.
*Conditional pass (tier B):* ≥ 99.99 % bit-identical **and** every mismatch is exactly ±1 half-ULP.
Acceptable only while `Mathf.FloatToHalf`'s rounding is still Unverified (§1.3); once the dumper
settles it, tier A is the gate.
*Fail:* any mismatch > 1 half-ULP. Half ULPs here: 0.015625 on [16,32), 0.03125 on [32,64),
0.0625 on [64,128), 0.125 on [128,256), 0.25 on [256,512) — so a 1-ULP slop at a 400 m peak is 0.25 m
and around the 30 m branch it is 0.015625 m. (**Correction:** *at* sea level it is far smaller, not
0.015 m — binary16 ULP is 2^(e-10) for a value in [2^e, 2^(e+1)), so at |h| < 2⁻¹⁴ the spacing is the
subnormal step 2⁻²⁴ ≈ 6·10⁻⁸ m.) Report the error histogram in ULPs, and separately in metres.
*Decoder requirement:* compare the raw 16-bit patterns and implement the full binary16 decode. The
claim that subnormals never occur is false: `asdasdasd` holds **304 distinct subnormal codes over 363
pixels** and one `0x8000` (`-0.0`); `testworldclaude` holds 330 codes over 392 pixels and two `-0.0`
(`rv_cache.py`). A decoder that assumes normalised halves, or that compares decoded floats rather than
bit patterns, silently mis-scores those pixels and cannot see a `+0.0`/`-0.0` sign error at all.
*Sub-tests:*
- **T3a — the outside-world constant.** Exactly **1 788 980** pixels must be `-400.0`, and they must be
  exactly the pixels with `DUtils.Length(wx,wy) > 10500f`. Seed-independent (identical in both worlds).
  *Isolates:* the `GetBiomeHeight` early-out and `DUtils.Length`.
- **T3b — river-free land.** Restrict to pixels ≥ 300 m from any generated river/stream segment. If
  T3b passes and full T3 fails, the defect is in `Pregenerate`/`AddRivers` (i.e. `m_riverSeed`,
  `m_streamSeed`, `FindLakes`, `PlaceRivers`, `PlaceStreams`, `RenderRivers`), not in the base terrain.
- **T3c — max/min sanity.** max 457.0, min -400.0 on `asdasdasd`; max 410.5 on `testworldclaude`.
*Diagnostic value:* T3 is the first test that exercises the river pass, and the river pass is the most
fragile part of the whole port: `FindStreamStartPoint`/`FindStreamEndPoint` consume `Random` inside
loops that **exit early on a height comparison** *(WorldGenerator: `FindStreamEndPoint` at line 377,
`FindStreamStartPoint` at line 397, both ending by 414)*, so a single
1-ULP difference in `GetPregenerationHeight` desynchronises the RNG and produces a *completely
different* set of streams. Expect T3 to be all-or-nothing around rivers: a "97 % agreement, errors
clustered in ribbons" result means the stream RNG desynchronised, not that the maths is slightly off.

---

**T4 — Mask agreement over all 4 194 304 pixels.**
Compute `GetMaskColor(wx, wy, height, biome)` using the tool's **own float32 height** (not the cached
half) to choose the `height < 30f` branch, convert with round-to-nearest, compare all four bytes.
*Pass:* 4 194 304 / 4 194 304 exact.
*Known trap:* if you instead branch on the cached half, ~53 BlackForest pixels (and a similar handful
elsewhere) mis-branch, because a true height of e.g. 29.995 m stores as the half `30.0` — the half grid
near 30 has a spacing of 0.015625 and values in (29.9921875, 30.0078125) round to exactly 30. Measured:
53 of 134 631 "land" BlackForest pixels have mask R = 0. This is a property of the *test harness*, not
of the game, and it is why T4 must not be driven from the cache's height.
*Sub-tests:*
- **T4a — B channel below sea level (pure closed form).** 1 201 819 pixels exactly `(0,0,255,0)`,
  2 475 151 exactly `(0,0,0,0)`, and every intermediate value must match byte-for-byte.
  *Isolates:* `GetAshlandsOceanGradient` (including its **offset** `WorldAngle(x, y + ashlandsYOffset)`
  argument, §2.4), `Mathf.Clamp01`, and the `Color`→`Color32` rounding.
  This is the cheapest possible check that the rounding rule in §1.3 was implemented.
  *Already verified against a reference implementation:* restricting to the pixels that provably took
  this branch (not Ashlands-coloured, `R == 0`, `G == 0`), the 10 363 with `0 < B < 255` all match and
  the 2 437 750 with `B == 0` all match (`rv_mask.py`). So a failure of T4a is a port bug, not a
  disagreement about what the cache contains.
- **T4b — R channel.** *Isolates:* `GetForestFactor` and `InForest` (`< 1.15f`), plus the Plains
  `< 0.8f` cut. 186 552 pixels are `(255,0,0,0)`. Exact source — the scale is applied as **two**
  per-component float multiplies, so do not fold it into `pos * 0.004f`:
  ```csharp
  public static float GetForestFactor(Vector3 pos) { float num = 0.4f; return DUtils.Fbm(pos * 0.01f * num, 3, 1.6f, 0.7f); }
  public static bool  InForest(Vector3 pos)        { return GetForestFactor(pos) < 1.15f; }
  // DUtils.Fbm(Vector3 p, ...) => Fbm(new Vector2(p.x, p.z), ...)   -- y is dropped
  // the float overload: num = (float)((double)num + (double)num2 * (double)PerlinNoise(v.x, v.y));
  //                     num2 = (float)((double)num2 * (double)gain);  v *= lacunarity;   <- float
  ```
  *(WorldGenerator.GetForestFactor line 1412, WorldGenerator.InForest line 1407, DUtils.Fbm /
  decompiled)*. The `double` overload of `DUtils.Fbm` exists and is **not** the one selected here.
- **T4c — G channel.** *Isolates:* `Utils.SmoothStep(1.1f, 1.3f, forestFactor)` — the all-float
  `Utils` one, not the double `DUtils` one (§2.4) — on Mistlands land;
  142 747 pixels are `(0,255,0,0)`.
- **T4d — B channel on Ashlands land.** 21 681 pixels. *Isolates:* `GetAshlandsHeight(..., cheap:true)`'s
  mask alpha — a code path nothing else in this suite touches.

---

**T5 — Location-instance heights: 12 314 full-precision samples.**
For each instance in the `.db2`, compute `WorldGenerator.GetHeight(x, z)` and compare the **float32 bit
pattern** to the stored `y`.
*Pass:* 12 314 / 12 314 exact. *Report:* the ULP histogram of any mismatches; treat > 1 ULP as a bug and
> 1e-3 m as a serious bug.
*Diagnostic value:* this is the only oracle with full float precision (the minimap is quantised to
half), it samples every biome and every radius out to 10 292 m, and it is independent of the placement
RNG. If T3 (half) passes and T5 (float) fails, the error is **below half precision** — almost always
double-vs-float ordering inside `AddRivers`/`GetBaseHeight` or a `(float)` cast dropped from a
`(double)` expression. If T5 fails only for instances near a river or a lake, it is the river pass.
*Cross-check:* 1 474 instances sit below 30 m; those are ocean/shore locations and exercise
`GetOceanHeight`, which the minimap barely constrains because the values are small.

---

**T6 — Biome membership of location instances.** For each instance, `GetBiome(x,z)` must be a member
of that location's `m_biome` mask (`(location.m_biome & biome) != 0`, *GenerateLocationsTimeSliced
line 1969*). Requires the dumper's location table.
*Pass:* 12 314 / 12 314. *Diagnostic value:* low on its own (T2 already covers `GetBiome`), but it is
the cheapest end-to-end smoke test that the location table, the hash→name map and the biome enum flag
values (`Meadows=1, Swamp=2, Mountain=4, BlackForest=8, Plains=0x10, AshLands=0x20, DeepNorth=0x40,
Ocean=0x100, Mistlands=0x200` — note the gap at 0x80) all line up.

---

**T7 — Half-conversion conformance.** Dumper emits `Mathf.FloatToHalf(f)` for a table of ~200
adversarial floats; the tool must reproduce every one. *Pass:* exact. *Diagnostic value:* converts
T3's tier-B escape hatch into a hard tier-A gate. Cheap; do it as soon as the dumper exists.

---

**T8 — Location placement reproduction (stretch).** Reproduce
`ZoneSystem.GenerateLocationsTimeSliced` for the whole location list and compare the set of
`(prefabHash, zone)` pairs, and each position bit-exactly, against the `.db2`.
*Realistic pass criterion, for `asdasdasd`:* ≥ 99 % of the 12 314 instances match on
`(hash, zone, x, y, z)` bit-exactly, with the **known, enumerated exclusions**: (a) unplaced candidates
of any `m_unique` type that has already been placed were pruned from the file; (b) the 112 zones listed
as generated in the save were excluded from candidacy *at the time locations were generated*, which is
not necessarily the same set as the 112 in the file today; (c) any type whose `m_locationVersion`-driven
regeneration has already run.
*Prerequisites the tool does not have yet:* the full `ZoneLocation` list **in iteration order** with
every filter field, and a port of `AltBiomeWorldData` (candidate zones come from
`m_biomeData.GetRandomPointByBiomes*`, and two of the filters consult `GetBiomeSector(...).AltBiomes`).
*Diagnostic value:* this is the only test of the placement algorithm itself, but it is also the test
most likely to fail for reasons that are not the tool's fault. **Rank it last and never gate a release
on it.** For the seed-search feature, prefer to expose "candidate zones for prefab X" as a
best-effort, clearly-labelled estimate rather than a promise.

---

**T9 — Parser fidelity and read-only discipline.**
(a) Round-trip: re-serialise the parsed `.fwl2` and `.chunks` and require byte-identical output
(verified achievable: our walk of `_main.3.fwl2` consumed 220 of 220 package bytes with 0 left over,
and `_main.3.chunks` 54 of 54). The `.db2` round-trip is checked only up to the end of the ZoneSystem
block plus the two event blocks (§5.2), which together account for all 146 725 bytes.
(b) Static + runtime assertion that no `FileStream` is ever opened for write under any path containing
`worlds`, `worlds_local`, `characters` or `characters_local`.
(c) Golden-histogram regression: the colour counts in §1.6 and the half-value counts, stored as a
fixture, so a refactor that silently changes the decoder is caught in one second.

---

### Test matrix — what each test isolates

| Sub-system | T0 | T1 | T2a | T2 | T3a | T3b | T3 | T4a | T4b/c | T5 | T8 |
|---|---|---|---|---|---|---|---|---|---|---|---|
| string hash | ● | | | | | | | | | | ● |
| `UnityEngine.Random` | | ● | | ◐ | | | ◐ | | | ◐ | ● |
| float/double cast discipline | | | ● | ● | ● | ● | ● | ● | ● | ● | ● |
| `Mathf.PerlinNoise` | | | | ● | | ● | ● | | ● | ● | ● |
| `DUtils.Fbm` | | | | | | | | | ● | | ● |
| river/stream pass | | | | | | ○ | ● | | | ● | ● |
| half conversion | | | | | ● | ● | ● | | | | |
| `Color`→`Color32` rounding | | | | ● | | | | ● | ● | | |
| AltBiome grid | | | | | | | | | | | ● |
| location filters/order | | | | | | | | | | | ● |

● exercised · ◐ exercised indirectly (through the offsets) · ○ deliberately excluded

*Added by review:* the matrix has no column for **T1b**, which is the only direct test of
`Mathf.PerlinNoise`; every ● in the `Mathf.PerlinNoise` row above is an *indirect* test that cannot
localise a noise error. Add a T1b column with ● on `Mathf.PerlinNoise` and run it before T2. Likewise
`UnityEngine.Vector2.magnitude` deserves its own row (exercised by T2b, and by T3 through `FindLakes`),
distinct from the `DUtils.Length` usages covered under "float/double cast discipline", and
`Utils.RoundToInt`/`Utils.FloorToInt` deserve one (exercised by T8 and by the `.fch` overlay, §5.4 —
nothing in T0–T5 touches them, which is why they are a risk).

---

## 5. Parser specifications (C#, .NET 10)

### 5.0 Common conventions

Everything except the minimap cache is a **ZPackage**: a `MemoryStream` driven by
`System.IO.BinaryWriter`/`BinaryReader` *(ZPackage / decompiled, assembly_valheim)*. Use .NET's own
`BinaryReader` and the semantics match exactly:

| ZPackage call | bytes |
|---|---|
| `Write(bool)` / `ReadBool` | 1 (0 or 1) |
| `Write(byte/sbyte)` | 1 |
| `Write(short/ushort)` | 2, little-endian |
| `Write(int/uint)` | 4, LE |
| `Write(long/ulong)` | 8, LE |
| `Write(float)` | 4, IEEE LE |
| `Write(double)` | 8, IEEE LE |
| `Write(string)` | `BinaryWriter.Write(string)`: a **7-bit-encoded `int`** (LEB128, 1–5 bytes) giving the UTF-8 **byte** count, then those bytes. It is one byte only while the string is < 128 bytes — the `preset …` global key in `asdasdasd` is 87 bytes and fits, a longer one would not |
| `Write(char)` | **UTF-8, 1–3 bytes** (`BinaryWriter.Write(char)`) — never assume 2 |
| `Write(Vector3)` | 3 floats: x, y, z |
| `Write(Vector2i)` | 2 ints |
| `Write(Vector2s)` | 2 shorts |
| `Write(Quaternion)` | 4 floats |
| `Write(byte[])` | `int length` then the bytes |
| `ReadByteArray(int n)` | n raw bytes, **no length prefix** |
| `Write(ZPackage)` | `int length` then the bytes |
| `WriteCompressed(ZPackage)` / `ReadCompressedPackage` | `int gzipLength` then gzip(`Utils.Compress`) bytes |
| `WriteNumItems` / `ReadNumItems` | write: 1 byte if `n < 128`, else `(byte)((n>>8)\|0x80)` then `(byte)n`. read: `b = ReadByte(); if ((b & 0x80) != 0) b = ((b & 0x7F) << 8) \| ReadByte();` — max 32767 |
| `GenerateHash` | SHA-512 of the whole package |

All files are little-endian. Open with
`new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)` — the
game may hold the file open, and Steam Cloud may replace it mid-read; treat an `IOException` as
"retry once, then report", never as corruption.

**Save-set resolution** (`SaveCollection.KeepOnlyNewest` semantics, read-only mirror):
enumerate files whose name starts with `_main.` in the world folder, parse `N` with
`int.Parse(Path.GetExtension(Path.GetFileNameWithoutExtension(f)).Substring(1))`, sort **descending**,
and take the highest `N` whose group has all four members. If any `int.Parse` throws, the game logs
"Failed to parse files for save: &lt;dir&gt;" and drops the whole world from its list — the tool should
surface that as a warning and still try the highest complete integer set. One special case the game
accepts: a lone `_main.0.fwl2` with nothing else (a world created but never saved). Everything outside
the chosen group — **including higher-numbered incomplete sets** — is deleted by the game, which is the
mechanism behind the read-only rule at the top of this document. Re-resolve on every open.
*(SaveCollection.KeepOnlyNewest / decompiled; World.LoadWorld also re-parses the number with
`Regex.Match(pathPrimary, "_main.(\\d+).fwl2")` + `uint.Parse`, line 295.)*

```csharp
public interface IValheimReader { }                      // marker: read-only by construction
public sealed record WorldMeta(int FileVersion, string Name, string SeedName, int Seed, long Uid,
                               int WorldGenVersion, bool NeedsDb,
                               IReadOnlyList<string> StartingGlobalKeys,
                               IReadOnlyList<CrossNetworkUser> PlayerHistory);
public sealed record CrossNetworkUser(string PlatformUserId, string DisplayName,
                                      string ServerAssignedName, string PlayFabId);
public sealed record LocationInstance(int PrefabHash, float X, float Y, float Z, bool Placed) {
    // ZoneSystem.GetZone (line 2973). NOT MathF.Floor, and NOT a float divide.
    //   public static Vector2s GetZone(Vector3 p) {
    //       int x = Utils.FloorToInt((float)(((double)p.x + 32.0) / 64.0));
    //       int y = Utils.FloorToInt((float)(((double)p.z + 32.0) / 64.0));
    //       return new Vector2s(x, y);            // Vector2s(int,int) narrows with (short)
    //   }
    public (short zx, short zy) Zone => ((short)FloorToInt((float)(((double)X + 32.0) / 64.0)),
                                         (short)FloorToInt((float)(((double)Z + 32.0) / 64.0)));
    static int FloorToInt(float f) => (int)(f + 64000f) - 64000;   // Utils.FloorToInt, line 1222
}
public sealed record ZoneSystemData(IReadOnlyList<(short x, short y)> GeneratedZones, int LocationVersion,
                                    IReadOnlyList<string> GlobalKeys, bool LocationsGenerated,
                                    IReadOnlyList<LocationInstance> Locations);
```

**Why the `Zone` formula above is spelled out.** `Utils.FloorToInt(f) = (int)(f + 64000f) - 64000`
quantises the fraction to 1/512 at this magnitude, so it is *not* `floor`. Worked example: for
`x ∈ (31.875, 32)` the exact `(x+32)/64` is in `(0.998046875, 1)`, `+64000f` rounds up to exactly
`64001f`, and `FloorToInt` returns **1** where `MathF.Floor` returns **0** — a 0.125 m band at every
zone boundary in which the two disagree, i.e. a location assigned to the wrong 64 m zone. It happens
that none of the 12 314 instances in `_main.3.db2` fall in such a band (`max |pos − 64·zone| =
28.8513 < 31.875`, `rv_saves.py`), which is exactly why a wrong implementation would pass the current
fixtures and fail silently on arbitrary query points and on T8. Port `FloorToInt`/`RoundToInt`
literally (§1.4).

### 5.1 `_main.<N>.fwl2`

```
int32  byteLength
byte[byteLength]  ZPackage:
    int32  version            // 41 = Version.World.DeepNorth
    string m_name
    string m_seedName
    int32  m_seed
    int64  m_uid
    int32  m_worldGenVersion  // present if version >= 26 (WorldGenVersion)
    bool   m_needsDB          // present if version >= 30 (NeedsDB)
    int32  nKeys              // present if version >= 32 (GlobalKeys)
      string × nKeys
    int32  nUsers             // present if version >= 41 (DeepNorth)
      { string platformUserId; string displayName; string serverAssignedName; string playFabId } × nUsers
```
*(World.SaveWorldFWLData lines 218-247, World.LoadWorld lines 249-360 / decompiled)*

Verified byte-exact on `asdasdasd\_main.3.fwl2` (224 bytes on disk, inner package 220, **0 bytes left
over**): version 41, name `asdasdasd`, seedName `MWd8eV6svz`, seed -1772362158, uid `<uid>`,
worldGenVersion 2, needsDB true, keys `["resourcerate 300", "preset combat_default:deathpenalty_default:resources_most:raids_default:portals_default"]`,
1 player-history entry `["Steam_<SteamID64>", "<displayName>", "<serverAssignedName>", "<playFabId>"]`
(4 strings, as the format above describes; the identifiers are withheld here).
And on `testworldclaude\_main.1.fwl2`: seedName `hnBd9gJf2G`, seed 319486907, uid `<uid>`, no keys.

Accept versions 9…41 (`Version.IsWorldVersionCompatible`); reject > 41 loudly rather than guessing.
**Unverified:** the pre-41 `CrossNetworkUserInfo` absence is handled by the version gate above, but no
pre-41 file exists on this machine to test against.

### 5.2 `_main.<N>.db2`

```
int32   version        // 41
double  m_netTime
--- ZoneSystem block ---
int32   gzipLength
byte[gzipLength]  gzip -> ZPackage:
    int32 nZones ; { int16 x; int16 y } × nZones
    int32 locationVersion
    int32 nKeys  ; string × nKeys
    bool  locationsGenerated
    int32 nLoc   ; { int32 prefabHash; float x; float y; float z; bool placed } × nLoc
--- RandEventSystem block ---
float  eventTimer ; string eventName ; float eventTime ; float posX, posY, posZ
--- PersistentEventSystem block ---
int32  compressedLength ; byte[compressedLength]
```
*(ZoneSystem.Save/Load lines 987-1074; RandEventSystem.Save/Load; PersistentEventSystem.Save / decompiled)*

Verified on `asdasdasd\_main.3.db2` (146 725 bytes): the ZoneSystem blob is 146 669 bytes compressed →
209 803 inflated, and exactly **40 bytes** remain, which decode as
`a5ae3643` (eventTimer = 182.682), `00` (empty event name), `00000000` (time 0), `000000000000000000000000`
(pos 0,0,0) = 21 bytes, then `0f000000` (compressedLength 15) + 15 bytes
`0b 05 80 7b 22 6c 69 73 74 22 3a 5b 5d 7d 03` — total 40, nothing left over. The inner 15 bytes
contain the ASCII `{"list":[]}`; **Unverified:** their compression framing (it is neither gzip nor a
ZPackage). The tool never needs them: it must stop after the ZoneSystem block and simply record the
remaining bytes verbatim.

The layout is confirmed from source, not only from the byte walk:
```csharp
// ZNet.SaveWorldThread
binary.Write(41); binary.Write(m_netTime);
ZoneSystem.instance.Save(binary); RandEventSystem.instance.Save(binary); PersistentEventSystem.instance.Save(binary);
// RandEventSystem.Save(BinaryWriter w)
w.Write(m_tempSaveEventTimer); w.Write(m_tempSaveRandomEvent);          // float, string
w.Write(m_tempSaveRandomEventTime);                                     // float
w.Write(m_tempSaveRandomEventPos.x); w.Write(...y); w.Write(...z);      // 3 floats, NOT a Vector3 helper
// PersistentEventSystem.Save(BinaryWriter w)
w.Write(compressedData.Length); w.Write(compressedData);                // int + raw bytes
// ZoneSystem.Save(BinaryWriter w): byte[] c = zPackage.GetCompressed(); w.Write(c.Length); w.Write(c);
```
*(ZNet.SaveWorldThread, RandEventSystem.Save, PersistentEventSystem.Save, ZoneSystem.Save line 1016 /
decompiled)*. Note the top level is a bare `BinaryWriter`, **not** a ZPackage — but `Write(string)`
and the primitives have identical semantics, so the §5.0 table applies unchanged.

Independently re-walked in `rv_saves.py`, on both worlds, with **0 bytes left over** in every block:
`asdasdasd/_main.3.db2` as above, and `testworldclaude/_main.1.db2` = 146 546 bytes, version 41,
netTime 2086.899981, ZoneSystem blob 146 490 → 209 300 inflated, 101 zones, `m_locationVersion` **32**,
0 global keys, `locationsGenerated` true, **12 287** instances in 12 287 distinct zones, 177 distinct
hashes, 49 placed, y ∈ [15.062, 409.420], 1 394 below 30 m, `max |pos − 64·zone| = 28.8960`, radius ∈
[48.6, 10 307.0], then the same 40-byte tail (eventTimer 46.900883, empty name, the identical 15-byte
`0b 05 80 7b 22 6c 69 73 74 22 3a 5b 5d 7d 03` payload).

This closes the knowledge base's "**Unverified:** the `RandEventSystem.Save` and
`PersistentEventSystem.Save` layouts inside `.db2`".

### 5.3 `_main.<N>.chunks` and `*.chunk`

```
// _main.<N>.chunks  (ChunkSaveMapping.Save) -- a bare ZPackage payload, NO int32 length prefix
int16  version        // 41   (written as (short)41, read back with ReadUShort)
int32  totalZDOs      // sum of m_numZDOs over ALL chunks, including the ones omitted below
int32  count          // only chunks with SaveChunk == !m_sizeChanged
{ uint16 chunk; byte size; uint32 version; int32 numZDOs } × count     // 11 bytes each
```
*(ChunkSaveMapping.Save / Load / decompiled)* — so `totalZDOs` can exceed the sum of the listed
`numZDOs` if a chunk changed size during the session; do not assert equality.
Verified byte-exact: `_main.3.chunks` is 54 bytes = 10 + 4×11, and parses to version 41,
totalZDOs 12 907, count 4 (re-parsed in `rv_saves.py`; `testworldclaude/_main.1.chunks` is also 54
bytes, totalZDOs 15 507, count 4, all four at version 1):

| chunk | cy | cx | size | ver | ZDOs | file |
|---|---|---|---|---|---|---|
| 0x1e1e | 30 | 30 | 1 | 1 | 176 | `1e_1e__1_1.chunk` |
| 0x1e20 | 30 | 32 | 1 | 3 | 6 507 | `1e_20__1_3.chunk` |
| 0x201e | 32 | 30 | 1 | 1 | 151 | `20_1e__1_1.chunk` |
| 0x2020 | 32 | 32 | 1 | 3 | 6 073 | `20_20__1_3.chunk` |

`chunk = cx + (cy << 8)` *(ZoneSystem.ChunkIndexFromXY line 3277)*; the file name is
`(Chunk>>8).ToString("x2") + "_" + (Chunk & 0xFF).ToString("x2") + "__" + size + "_" + version + ".chunk"`
*(ChunkSaveMapping.GetChunkFilename)*, i.e. `{cy:x2}_{cx:x2}__{size}_{version}.chunk`; the chunk's
corner zone is `((Chunk & 0xFF)*8 - 256, (Chunk >> 8)*8 - 256)` *(ZoneSystem.GetZoneFromChunk line
3079)* with `public const int c_ZonesPerChunk = 8` *(ZoneSystem line 426)* on a 512×512 zone grid
*(ZoneSystem.GetZonesChunk line 3072: `sector % 512 / 8 | (sector / 512 / 8) << 8`)*.
`_main.<N>.ok` is a single `int32 41` (observed `29 00 00 00`, both worlds).

`*.chunk` is `int16 41`, `int32 zdoCount`, then `zdo.Save(pkg)` per ZDO.
**Unverified:** the ZDO body layout (`ZDO.Save`/`ZDO.Load` was not decompiled). The tool does **not**
need it: nothing in world generation lives in ZDOs. Parse the header, expose `zdoCount`, and stop.
The one exception, for a future feature, is the cartography-table shared map, which lives in a
`MapTable` ZDO under `ZDOVars.s_data` — that would require the ZDO layout and should be a separate
work item.

### 5.4 `<name>.fch` (character profile)

```
int32  dataLength
byte[dataLength]  ZPackage:
    int32 46          // Version.Player.DeepNorth
    int32 205         // stat count
    int32 10          // stat categories
    repeat 10 times:
        float × 205                                    // m_stats[PlayerStatType]
        int32 n; { string; float } × n                 // m_knownWorlds
        int32 n; { string; float } × n                 // m_knownWorldKeys
        int32 n; { string; float } × n                 // m_knownCommands
        int32 5                                        // enemy-stat table count
          repeat 5: int32 n; { string; float } × n     // m_enemyStats[k]
        int32 n; { string; float } × n                 // m_itemPickupStats
        int32 n; { string; float } × n                 // m_itemCraftStats
        int32 n; { string; float } × n                 // m_pickableStats
        int32 n; { string; float } × n                 // m_foodEatenStats
        int32 n; { string; float } × n                 // m_piecesPlacedStats
    bool  m_firstSpawn
    int32 worldCount
    repeat worldCount:
        int64   worldUID
        bool    haveCustomSpawn ; Vector3 spawn
        bool    haveLogout      ; Vector3 logout
        bool    haveDeath       ; Vector3 death
        Vector3 home
        bool    hasMap ; if (hasMap) { int32 len; byte[len] mapData }
    string m_playerName
    int64  m_playerID
    string m_startSeed
    bool   usedCheats
    int64  dateCreated            // unix seconds
    bool   hasPlayerData ; if so { int32 len; byte[len] }   // Player.Save blob, PlayerData version 33
int32  hashLength   // 64
byte[64]  SHA-512 of the data package   // read and DISCARDED on load
```
The hash is real and usable as an integrity check even though the game ignores it: `SHA512(dataPackage)`
reproduces the stored 64 bytes exactly for `asda.fch` (`rv_fch.py`). `ZPackage.GenerateHash()` is
`SHA512.Create().ComputeHash(GetArray())` *(ZPackage.GenerateHash / decompiled)*.
*(PlayerProfile.SavePlayerToDisk / decompiled; verified on disk: `asda.fch`, profile v46, name "Asda",
playerID `<int64>`, 2 world entries)*

Note the `5` before the enemy tables is written as a literal and must be read, not assumed. The inner
counts of the ten stat categories vary; the parser must be driven by them, not skipped by a fixed size.

**`mapData` blob** *(Minimap.GetMapData / SetMapData / decompiled)*:

```
int32  8                        // Version.Map.PinsAuthor
int32  gzipLength ; gzip ->     // WriteCompressed
    int32 textureSize           // 2048  -> throws "Error: minimap mismatch" on any other value
    byte[textureSize²] explored          // 1 byte per pixel, 0/1
    byte[textureSize²] exploredOthers    // 1 byte per pixel, 0/1
    int32 nPins
      repeat: string name; Vector3 pos; int32 pinType; bool checked; int64 ownerID; string author
    bool publicReferencePosition
```
Version gates on read: pins at ≥ `Pins`(2), `checked` at ≥ `PinsChecked`(3), the byte-array explore
layers at ≥ `NewExplore`(5), `ownerID` at ≥ `PinsOwnerID`(6), the whole blob compressed at ≥
`Compressed`(7), `author` at ≥ `PinsAuthor`(8), `publicReferencePosition` at ≥ `VisibleOnMap`(4)
*(Version.Map: Pins=2, PinsChecked=3, VisibleOnMap=4, NewExplore=5, PinsOwnerID=6, Compressed=7,
PinsAuthor=8)*. Only pins with `m_save == true` are stored.

`PinType`: `Icon0=0, Icon1, Icon2, Icon3, Death=4, Bed=5, Icon4=6, Shout=7, None=8, Boss=9, Player=10,
RandomEvent=11, Ping=12, EventArea=13, Hildir1=14, Hildir2=15, Hildir3=16, Memorial=17`.

Verified on disk: `asda.fch` holds two world entries — uid `<uid>` (`asdasdasd`): mapVersion 8,
textureSize 2048, 277 explored pixels, 2 pins (`$enemy_eikthyr` type 9 Boss at (110,-71),
`$hud_mapday 1` type 4 Death at (119,-9)); uid `<uid>` (`testworldclaude`): 81 explored pixels,
0 pins.

**For the "overlay my exploration" feature**, this is everything that is needed and everything that
exists: a character holds, **per world UID**, the two explored bitmaps on the same 2048×12 grid as the
minimap cache, plus its saved pins and its spawn/logout/death/home points. Pixel `k = py*2048 + px`
with `px = Utils.RoundToInt(x/12f + 1024f)`, `py = Utils.RoundToInt(z/12f + 1024f)` — the exact
`(int)(f + 64000.5f) - 64000` form of §1.4, **not** `Math.Round` — *(Minimap.WorldToPixel lines
1817-1819; Minimap.Explore(int,int) indexes `m_explored[y * m_textureSize + x]`, line 1859)*. That is
the identical indexing as the cache, so the overlay is a direct per-pixel AND with no resampling.
The map is keyed by the **UID**, not the name or the seed, and the UID is random per world creation
(`name.GetStableHashCode() + Utils.GenerateUID()`), so the tool must read the UID from the `.fwl2` to
find the right entry. Shared (cartography-table) exploration is **not** here — it is in a world ZDO.

### 5.5 Minimap cache

```csharp
public sealed record MinimapCache(int Seed, int CacheVersion, int TextureSize, float PixelSize,
                                  byte[] BiomeRgba, byte[] MaskRgba, ushort[] HeightHalf);

// cacheMinimapMeta : int32 seed, int32 version(1)              -- raw, not a ZPackage, not compressed
// cacheMinimapBiome: gzip -> byte[N*N*4] , R,G,B,A per pixel
// cacheMinimapMask : gzip -> byte[N*N*4] , R,G,B,A per pixel
// cacheMinimapHeight: gzip -> ushort[N*N] little-endian IEEE binary16
// N = isqrt(len(biome)/4) ; assert N*N*4 == len(biome) == len(mask) and N*N*2 == len(height)
// PixelSize is not in the file: use 12.0f (measured, §1.5) and assert N == 2048
```

Index → world: `wx = (float)(j - N/2)*P + P/2`, `wy = (float)(i - N/2)*P + P/2`, `k = i*N + j`,
row 0 south. World → index: `j = Utils.RoundToInt(wx/P + N/2)`, `i = Utils.RoundToInt(wz/P + N/2)` —
the `(int)(f + 64000.5f) - 64000` form of §1.4.
Decode a biome from `BiomeRgba` through the measured table in §2.1, returning
`Ocean|Mountain|DeepNorth` as an explicit *ambiguous* value for pure white rather than guessing.
**Correction:** subnormal halves *do* occur, so implement the full binary16 → float conversion
(subnormal branch, signed zero, and the Inf/NaN branch for completeness). Measured:
`asdasdasd` has 304 distinct subnormal codes over 363 pixels plus one `0x8000` (`-0.0`);
`testworldclaude` has 330 codes over 392 pixels plus two `-0.0`; neither has any Inf/NaN code
(`rv_cache.py`). The overall value range is -400.0 … 457.0, but that says nothing about the codes near
zero. Keep and compare the raw `ushort`; decode only for display.

Reject the cache if `meta.seed != fwl2.seed` or `meta.version != 1`; those are exactly the game's own
conditions *(Minimap.TryLoadMinimapTextureData)* and a mismatch means the cache is stale.

---

## 6. Open items and risks

1. **Unverified:** the seven `UnityEngine.Random` draws for a given seed, **and the generator itself**.
   Nothing on disk pins them; `InitState`, `RandomRangeInt` and `Range(float,float)` are all native
   externs and `Random.state` is an opaque `{s0,s1,s2,s3}`. Resolution: dumper plugin, reflecting
   `WorldGenerator.m_offset0..4`, `m_riverSeed`, `m_streamSeed`, **plus** a `Random.state` dump and long
   draw sequences from *both* `Range` overloads (T1). Until then T2/T3 failures are ambiguous.
2. **Unverified — and the biggest one in the document:** `Mathf.PerlinNoise` is a native extern
   (`[FreeFunction("PerlinNoise::NoiseNormalized")]`, `[NativeHeader("Runtime/Math/PerlinNoise.h")]`).
   Every biome boundary and every height depends on it and there is no managed implementation to copy.
   Resolution: T1b — a dumped conformance table, run before T2. *(Added by review; the original spec
   did not list this.)*
3. **Unverified:** `Mathf.FloatToHalf`'s rounding mode (native extern). Resolution: T7.
4. **Settled, was wrongly listed as unverified:** `Color`→`Color32` is
   `(byte)Mathf.Round(Mathf.Clamp01(c.x) * 255f)` and `Mathf.Round(float f) => (float)Math.Round(f)`,
   i.e. **round-half-to-even**, not half-up. Both are managed code in UnityEngine.CoreModule
   *(UnityEngine.Color32.op_Implicit, UnityEngine.Mathf.Round / decompiled)* — no dumper needed. See
   §1.3; the previously recommended `+ 0.5f`-then-truncate implementation was wrong at exact ties.
5. **Unverified:** the `PersistentEventSystem` 15-byte payload framing (contains `{"list":[]}`).
   Not needed; recorded verbatim. Byte-identical in both worlds, which is consistent with it being a
   constant "empty list" encoding.
6. **Unverified:** the `.chunk` ZDO body layout. Not needed for generation; blocks only the
   cartography-table overlay.
7. **Risk — `Utils.RoundToInt` / `Utils.FloorToInt` are not `Math.Round` / `MathF.Floor`.** They add a
   64000 bias **in float**, which quantises the fraction (1/512 at zone scale, 1/128 at pixel scale) and
   makes them disagree with the obvious implementations near every boundary. They drive
   `Minimap.WorldToPixel`, `Minimap.Explore` and `ZoneSystem.GetZone`, so getting them wrong corrupts
   zone assignment, the explore overlay and T8 — and the current fixtures would not catch it, because no
   instance in `_main.3.db2` sits in a disagreement band. See §1.4 and §5.0.
8. **Risk — the biome colours are prefab data.** A game update that re-tints the map breaks T2
   silently-looking (it will fail 100 %, which is loud, but the cause will look like a generation bug).
   Mitigate by dumping the live colour fields and by failing with a dedicated "colour table changed"
   message when the *pattern* of agreement is "every pixel wrong but the region shapes are right".
9. **Risk — the white collision.** Ocean/Mountain/DeepNorth confusion inside the white set is not
   detectable by T2 alone. T3 and T2b cover it in practice, but a reimplementation that got the
   Ocean-before-DeepNorth test order wrong *and* produced similar heights would slip through. Add an
   explicit unit test for `GetBiome`'s branch order against the decompiled source.
10. **Risk — the river pass is chaotic.** `FindStreamStartPoint`/`FindStreamEndPoint` exit their loops
    on height comparisons, so RNG consumption is data-dependent. There is no partial credit: either the
    port is bit-exact through `GetPregenerationHeight` or the stream set is unrelated. Budget for this
    and keep T3b (river-free) as the bisection tool.
11. **Risk — location reproduction depends on `AltBiomeWorldData` and on prefab data.** T8 cannot be a
    release gate. The seed-search feature should be scoped to what T2/T3/T5 prove (biomes, heights,
    rivers, distances, "is there a mountain within N m of spawn") and treat "where is the Elder altar"
    as best-effort.
12. **Risk — the ground truth moves.** `asdasdasd` advanced from save 2 to save 3 while this document
    was being written, and the `testworldclaude` cache appeared during the session. Snapshot both
    minimap caches and the current `.db2` into the repo's test fixtures (they are derived data, safe to
    copy, ~4.5 MB each world) so the suite is reproducible; never read the live save during CI.
13. **Risk — two worlds is a small hold-out.** Both have `worldGenVersion 2` and no world modifiers.
    Generation ignores modifiers, so that is fine, but neither exercises `VersionSetup` for
    versions ≤ 1. If the tool claims to support older worlds, generate a `worldGenVersion 0/1` world in
    game and cache it before making that claim.

---

## 7. Open questions (added by review)

These are things this review could not settle from evidence. They are separate from the **Unverified:**
items already listed in §6, which are known-native or known-missing dependencies.

1. **Where did §1.3's original rounding sample come from?** The figures "375 742 sampled mask pixels",
   "6 504 wrong", "3 847 samples with a fractional part in (0.35, 0.65)", "1 918 wrong" could not be
   reproduced from the caches. The provably-gradient-branch set (not Ashlands-coloured, `R == 0`,
   `G == 0`) has **10 363** pixels with `0 < B < 255` and **2 437 750** with `B == 0`, and on that set
   round-to-nearest is wrong 0 times and truncation 5 302 times (`rv_mask.py`). The conclusion is
   unchanged and the rule is now settled from source, so this is a provenance question only — but if
   that number is reused anywhere else in the project it should be re-derived.
2. **Are `m_textureSize = 2048` and `m_pixelSize = 12` stable across platforms and future builds?** They
   are prefab overrides of serialized fields whose code defaults are 256 and 64. Both are pinned on this
   machine four independent ways (§1.5, plus `AltBiomeWorldData.c_pixelSize = 12f` and the `.fch` map
   blob's `textureSize = 2048`), but nothing read here proves a console or a future build uses the same
   values. The dumper should emit `Minimap.m_textureSize`/`m_pixelSize` alongside the colour fields, and
   the parser should keep deriving `N` from the buffer length rather than hard-coding it.
3. **Do the subnormal halves and the `-0.0` come from the generator or from the half conversion?** 363
   pixels in `asdasdasd` decode to subnormal halves and one to `-0.0`. Whether `-0.0` arises because
   `GetBiomeHeight` returned a small negative float that rounds to zero, or because `Mathf.FloatToHalf`
   preserves the sign of a negative-zero input, is not established — and the two hypotheses differ in
   what T3 should expect if `FloatToHalf` turns out not to be IEEE. Resolve alongside T7.
4. **The `.db2` global-key filter.** `ZoneSystem.Save` removes keys for which
   `GetKeyValue(x, out _, out gk)` gives `gk < GlobalKeys.NonServerOption`, before writing. Both
   ground-truth worlds save **0** keys, so the filter is untested against real data, and the `.fwl2`'s
   two `m_startingGlobalKeys` are a different list that is *not* subject to it. A tool that reports
   "world keys" must say which of the two lists it is reading.
5. **§3.4's manifest scan was not re-run.** `names2.txt` was re-read and does contain 176 rows, each
   with a collision count of 1, and its header figures (45 585 manifest candidates, 256 location prefab
   paths, 799 bundles, 8 373 486 total candidates) are as quoted, with 175 of the 176 names under
   `Assets/world/Locations/`. The scan itself was not repeated, so a change to the `SoftRef\manifest`
   format would not have been caught here.

---

## Verification

**Checked, and found correct** — re-derived from the decompiled sources and the live files rather than
taken from the spec's own quotes:

- **Formats, byte-exact, 0 bytes left over in every block** (`rv_saves.py`, `rv_fch.py`): `_main.3.fwl2`
  224/220/0 and `_main.1.fwl2` 125/121/0; `_main.3.db2` 146 725 with every §3.1 figure (112 zones,
  `m_locationVersion` 32, 0 keys, 12 314 instances in 12 314 distinct zones, 176 hashes, 44 placed,
  y in [15.025, 457.678], 1 474 below 30 m, none at 0, max |pos - 64*zone| = 28.8513, radius in
  [70.6, 10 292.7], netTime 2222.679928, blob 146 669 -> 209 803) and the 40-byte tail **byte for
  byte**, including the 15-byte `{"list":[]}` payload; `_main.3.chunks` 54 bytes with all four rows and
  filenames; `_main.3.ok` = `29 00 00 00`; `asda.fch` v46/205/10 with 2 world entries, 277 and 81
  explored pixels, both pins with their positions and types, name and playerID, map blob v8 with
  `textureSize 2048` and the inflated size 8 388 617 = 2*2048^2 + 9 — **and the stored SHA-512 verifies**.
- **Cache format and content** (`rv_cache.py`, `rv_stats.py`): gzip headers, the three inflated sizes,
  N = 2048, all seven biome colours and counts, 512 mask colours with alpha 0 everywhere and R in
  {0,255}, 32 978 half codes, min -400.0 max 457.0, `-400` count 1 788 980, the P bracket
  (11.999988, 12.000004), and every per-biome radius and height figure in §2.2 including BlackForest's
  134 578 of 134 631. All of the hold-out world's figures likewise.
- **Geometry, with float32 semantics** (`rv_geom.py`): `IsAshlands` true for 1 361 539 pixels = exactly
  the Ashlands-coloured set, **0 disagreements** — which also independently proves the §1.4 orientation,
  P = 12 and the pixel-centre convention; `IsDeepnorth` true for 1 361 539, of which 999 263 are white.
- **Closed-form mask** (`rv_mask.py`): `GetAshlandsOceanGradient` + `Clamp01` + round-to-nearest
  reproduces the stored B byte for every one of the 10 363 non-clamped non-Ashlands pixels and for all
  2 437 750 zero ones.
- **Source-level**: `Version.World.DeepNorth = 41`, `Version.Player.DeepNorth = 46`,
  `Version.PlayerData.ChunkedNorth = 33`, `Version.CachedMinimap.Original = 1`, `c_WorldGenVersion = 2`,
  `c_networkVersion = 40u`, `CurrentVersion = 1.0.15`; the whole `Version.Map` ladder; `Heightmap.Biome`
  flag values including the 0x80 gap; the whole `Minimap.PinType` enum; `ZPackage`'s entire primitive
  table; `ZoneSystem.Save`/`Load` field order; `ZNet.SaveWorldThread`'s `41` + `netTime` prefix;
  `RandEventSystem.Save` and `PersistentEventSystem.Save`; `ChunkSaveMapping.Save`, `GetChunkFilename`,
  `GetZoneFromChunk`, `c_ZonesPerChunk`; `PlayerProfile.SavePlayerToDisk`/`LoadPlayerFromDisk`;
  `SaveCollection.KeepOnlyNewest`; `World.SaveWorldFWLData`/`LoadWorld`; `Minimap.GenerateWorldMap`,
  `SaveMapTextureDataToDisk`, `TryLoadMinimapTextureData`, `GetPixelColor`, `GetMaskColor`, `GetMapData`,
  `SetMapData`, and every colour field's access modifier; `Utils.Compress`, `ColorsToCompressedBuffer`,
  `FloatsToCompressedHalfBuffer`; `WorldGenerator`'s constructor draw order, `VersionSetup`, `GetBiome`,
  `GetBiomeHeight`'s `-2f * 200f` early-out, `GetForestFactor`, `InForest`, `GetAshlandsOceanGradient`,
  `IsAshlands`, `IsDeepnorth`, `WorldAngle`, `GetBaseHeight`'s offset usage, and that `AddRivers` is
  never reached from `GetBaseHeight`; and every distance constant quoted in §2.2. Every line citation in
  the document was checked against the file, and the wrong ones corrected.

**Corrected in place** — each correction carries its evidence in the text above:

1. **§4 T0** — `"" -> 0` was wrong for `GetStableHashCode`; it is **371 857 150**. The 0 is
   `World..ctor`'s separate empty-seed-name rule. Both are now tested.
2. **§1.3** — `Color` -> `Color32` uses `Mathf.Round` = `(float)Math.Round(f)` = **half-to-even**, and
   the conversion is **managed code**, not something the dumper must settle. The recommended
   `+ 0.5f`-then-truncate implementation (half-*up*) was wrong at exact ties and has been replaced.
   §6's corresponding "Unverified" item is now marked settled. Consequence inside the document itself:
   §2.1's "as Color32" value for the `m_blackforestColor` code default was **(0,179,0)** and is
   **(0,178,0)** — `0.7f * 255f` is exactly `178.5f`, a true tie, and 178 is the even one.
3. **§2.2 / §4 T2b** — `IsAshlands` uses `DUtils.Length` (**double** accumulation) but `IsDeepnorth`
   uses `Vector2.magnitude` (**float** accumulation). The spec treated them as the same function. Both
   forms are now quoted, together with the other call sites of each.
4. **§5.0 `LocationInstance.Zone`** — `MathF.Floor((X + 32f) / 64f)` was wrong twice over: the divide is
   done in double, and the floor is `Utils.FloorToInt(f) = (int)(f + 64000f) - 64000`, which disagrees
   with `floor` in a 0.125 m band at every zone boundary. Replaced with the exact form plus a worked
   counter-example. §1.4 gained the same treatment for `Utils.RoundToInt`, which `Minimap.WorldToPixel`
   and the `.fch` overlay in §5.4/§5.5 depend on.
5. **§5.5 / §4 T3** — "no denormal/NaN cases occur in practice" is false: 304 distinct subnormal codes
   over 363 pixels plus one `-0.0` in `asdasdasd`, 330/392 plus two in the hold-out. The decoder must
   implement the subnormal branch and the suite must compare bit patterns.
6. **§4 T1** — "the two `Range` overloads" is wrong: the constructor uses only
   `Range(int minInclusive, int maxExclusive)`; the float overload first appears in the stream pass and
   in `GetRandomPointInZone`. The exclusivity of `maxExclusive` is now stated, and the "Xorshift128"
   assertion is downgraded to Unverified with a note on what the dumper must capture.
7. **§1.6** — `0.0` occurs 263 724 times *as the bit pattern `0x0000`*; a 263 725th pixel holds
   `0x8000`. Also, "identical sizes" for the hold-out is true only of the *inflated* sizes.
8. **§1.5** — 42.656 % / 42.657 % corrected to 42.6526 % / 42.6536 %.
9. **§4 T3** — "a 1-ULP slop ... at sea level is 0.015 m" corrected: 0.015625 m is the ULP on [16, 32),
   and below 2^-14 the spacing is the subnormal step 2^-24.
10. **§2.4** — the mask's B ramp is *not* measured from the `IsAshlands` boundary:
    `GetAshlandsOceanGradient` passes the **offset** y to `WorldAngle` while `IsAshlands` passes the
    un-offset y. Also flagged `Utils.SmoothStep` (float) vs `DUtils.SmoothStep` (double), and
    `GetForestFactor`'s two-step scale `pos * 0.01f * 0.4f`, which must not be folded into `* 0.004f`.
11. **§2.2 / §2.3 / §4 T2, T2a, T2b** — added the exact `GetBiome` branch table, the complete
    `IsDeepnorth` partition (999 263 white + 362 276 AshLands = 1 361 539, none elsewhere), the second
    (bounded) BlackForest branch, the corrected `AddRivers` call-site range (...1375, not ...1350), and
    the fact that `GetBiome` reaches `GetHeight` only under `waterAlwaysOcean`.
12. **§3.2 / §3.3** — added `GetTerrainDelta`'s ten `insideUnitCircle` draws per surviving candidate,
    the `m_prefab.Name` == `m_prefabName` identity, `GetRandomPointInZone`'s inclusive float `Range`,
    and the real iteration order (`OrderByDescending(m_prioritized)` plus the `m_enable`/`m_quantity`
    filter) with the exact candidate-zone expression.
13. **§5.0 / §5.2 / §5.3 / §5.4** — `Write(string)`'s prefix is a 7-bit-encoded **int**, not a byte;
    `ReadNumItems`' masks; `KeepOnlyNewest`'s real algorithm including the lone-`_main.0.fwl2` case and
    the fact that it also deletes *higher*-numbered incomplete sets; `.chunks` `totalZDOs` counts chunks
    that are not listed; the `.fch` SHA-512 verifies. Line citations fixed where wrong (ctor 212-234 ->
    223-229; FindStream 384-414 -> 377 and 397).

**Material additions** — missing rather than wrong:

- **T1b, `Mathf.PerlinNoise` conformance.** `Mathf.PerlinNoise` is
  `[MethodImpl(InternalCall)] [FreeFunction("PerlinNoise::NoiseNormalized")] extern` — native, exactly
  like `FloatToHalf` — and the original document did not list it as an open item anywhere, although
  every biome boundary and every height flows through it. Added as a test to be run before T2, and as
  §6 item 2.
- `Utils.RoundToInt` / `Utils.FloorToInt` as a first-class risk (§6 item 7), because nothing in T0-T5
  exercises them and the current fixtures cannot catch a wrong implementation.
- The `GetBiome` branch table in §2.3, which is what §6's "explicit unit test for `GetBiome`'s branch
  order" needs in order to be writable at all.

**Still unverified after this review:** §6 items 1, 2, 3, 5 and 6 (the `UnityEngine.Random` generator
and its seven draws; `Mathf.PerlinNoise`; `Mathf.FloatToHalf`'s rounding; the `PersistentEventSystem`
framing; the `.chunk` ZDO body layout), everything in §7, and §6 items 8-13, which are risks rather
than facts. §6 item 4 (`Color` -> `Color32` ties) is now **settled** and no longer needs the dumper.

*checked by an independent reviewer*
