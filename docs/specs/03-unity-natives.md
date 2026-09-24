# 03 — Unity native functions: `Mathf.PerlinNoise` and `UnityEngine.Random`

Target: Valheim 1.0.15 (network 40, Steam build 25390630), Unity **6000.0.75f1**.
Scope: the two pieces of world generation that are not managed code. Everything below was read out of
`UnityPlayer.dll` by static disassembly and then **checked against real game output** (the user's
minimap cache for world `asdasdasd`, seed −1772362158).

## 0. Status — what is settled and what is not

| Item | Status |
|---|---|
| `Mathf.PerlinNoise(float,float)` — algorithm, constants, normalisation | **SETTLED.** Disassembled in full; agrees with 398 816 real game samples with 0 mismatches (§6.1) — 180 857 threshold comparisons and 217 959 byte-exact numeric comparisons. Not yet compared at full 32-bit precision; see §8.6. |
| `Mathf.PerlinNoise1D(float)` | **SETTLED** by disassembly: identical to `PerlinNoise(x, 0f)`. Valheim never calls it (0 occurrences of the name in `assembly_valheim.dll` and `assembly_utils.dll`). |
| `Random.InitState`, the xorshift128 step, `Random.state` layout | **SETTLED.** Disassembled; the seven world-gen draws for seed −1772362158 were confirmed by brute-force recovery from the biome map (§6.1, Test B/C). |
| `Random.Range(int,int)`, incl. the full-range case | **SETTLED** (same evidence; the full-range form is draws 5 and 6 of the ctor). |
| `Random.Range(a,a)` for ints (no draw) | **Disassembly only** — corrected (reviewer): no world-gen call site passes `min == max` to the int overload, so §6.1 does not touch it. The code path is unambiguous (`0x54940`→`0x54980`, state untouched); dumper item **D7** confirms it at runtime. |
| `Random.value`, `Random.Range(float,float)` | **Read from the disassembly, not yet executed.** Both are a handful of instructions on the RNG core, which *is* proven. No contradicting evidence, but no runtime sample exists yet. Dumper items **D5–D7**. |
| `Random.insideUnitCircle` | **Structure settled** (2 draws, `cos`/`sin`, `sqrt`). Open: whether the host CRT's `cosf`/`sinf` are bit-identical to .NET's `(float)Math.Cos(double)`. Dumper item **D8**. Variant list in §5.2. |
| CPU-dependence of `cosf`/`sinf` (FMA/AVX2 path) | **Unverified:** both functions branch on a runtime CPU-feature flag at `.data` rva `0x1FA9F70`. Only affects `insideUnitCircle`. |

Nothing else in world generation calls a **Unity** native function: `Mathf.Sin/Cos/Floor/…` are managed
wrappers over `System.Math` (`UnityEngine.Mathf.Sin` = `(float)Math.Sin(f)`, `Mathf.Cos`, `Mathf.Sqrt`,
`Mathf.Pow` = `(float)Math.Pow(f,p)` — decompiled `UnityEngine.Mathf`), and `FastNoise` is pure C#.

**Corrected (reviewer):** that is not the same as "no native maths outside these two". `System.Math.Sin`,
`Cos`, `Atan2`, `Pow` and `Exp` are themselves runtime-provided (Mono's libm inside the game, CoreCLR's
on the tool side); only `Math.Sqrt` is IEEE-exact and therefore safe. Valheim's world generation calls
them on the **biome** path, not just on the river path:

* `WorldGenerator.WorldAngle` (decomp line 881) — `(float)Math.Sin((float)((double)(float)Math.Atan2(wx, wy) * 20.0))`;
  `GetBiome` uses `WorldAngle(wx,wy)*100` in the Mistlands / Plains / BlackForest / Meadows distance bands
  (decomp lines 791, 816–830).
* `WorldGenerator.GetBaseHeight` line 1142 `(float)Math.Pow(num3, 1.5)`, and lines 1261/1264/1269/1273
  `Math.Pow(x, 4.0 / 1.4 / 2.0 / 2.0)` on the Ashlands path.
* `WorldGenerator.RenderRivers` line 558 — three `Math.Sin` calls per river point;
  `FindStreamEndPoint` line 385 — `Mathf.Sin`/`Mathf.Cos` per candidate.

**Unverified:** whether Mono's `Math.Sin/Cos/Atan2/Pow` and .NET 10's agree in the last bit. See §7 and
the Open questions. This is outside the two functions this document specifies but it is *not* covered by
anything else, and it reaches biomes.

---

## 1. Provenance

| File | SHA-256 | Notes |
|---|---|---|
| `…\Valheim\UnityPlayer.dll` | `4D161E15D8CCDB32EB73262E7A3E0A66F8C175B50A38E22AE5B0E8FB9AEA98F3` | 34 413 480 bytes, FileVersion `6000.0.75.2503836`, ProductVersion `6000.0.75f1 (26349cd2a5c8)` |
| `…\valheim_Data\Managed\UnityEngine.CoreModule.dll` | `FBA3821AFB6867FE4D471DC50BF04BA21E6982B33AB6B5336B08A5C55119D990` | 1 801 128 bytes |

PE layout of `UnityPlayer.dll` (needed to convert the offsets below): image base `0x180000000`,
`.text` vaddr `0x00001000` rawptr `0x00000400`; `.rdata` vaddr `0x01A8B000` rawptr `0x01A89E00`.
**rva = fileoff + 0xC00** inside `.text`, **rva = fileoff + 0x1200** inside `.rdata`.

**If any of these hashes change, re-run §6 before trusting the tool.** Unity's `PerlinNoise.cpp` and
`Random.h` are stable across Unity versions in practice, but this spec is only evidence for this build.

### 1.1 How the managed name reaches the native code (not inferred from proximity)

`UnityPlayer.dll` carries the Mono internal-call registration table as two parallel arrays of 3751
entries: names at fileoff `0x1AF7C00` (rva `0x1AF8E00`), function pointers at fileoff `0x1AFF140`
(rva `0x1B00340`). Resolving by index:

| index | icall name | target rva | what is there |
|---|---|---|---|
| 2315 | `UnityEngine.Mathf::PerlinNoise` | `0x000AC7C0` | 30-byte wrapper → `0x0054B170` |
| 2316 | `UnityEngine.Mathf::PerlinNoise1D` | `0x000AC7E0` | 30-byte wrapper → `0x0054B3F0` |
| 2356 | `UnityEngine.Random::InitState` | `0x000B0CA0` | |
| 2357 | `UnityEngine.Random::get_state_Injected` | `0x000B0CD0` | |
| 2358 | `UnityEngine.Random::set_state_Injected` | `0x000B0CE0` | |
| 2359 | `UnityEngine.Random::Range` (float,float) | `0x000B0CF0` | |
| 2360 | `UnityEngine.Random::RandomRangeInt` | `0x000B0D60` → `0x00054900` | |
| 2361 | `UnityEngine.Random::get_value` | `0x000B0D80` | |
| 2363 | `UnityEngine.Random::GetRandomUnitCircle` | `0x000B0E70` | |
| 2368 | `UnityEngine.Random::set_seed` | `0x000B0CA0` | **same function as `InitState`** |

---

## 2. The managed stubs (`UnityEngine.CoreModule.dll`)

### 2.1 `UnityEngine.Mathf`

```csharp
[NativeHeader("Runtime/Math/PerlinNoise.h")]
[NativeHeader("Runtime/Math/FloatConversion.h")]
[Il2CppEagerStaticClassConstruction]
public struct Mathf
{
    [MethodImpl(MethodImplOptions.InternalCall)]
    [FreeFunction("PerlinNoise::NoiseNormalized", IsThreadSafe = true)]
    public static extern float PerlinNoise(float x, float y);

    [MethodImpl(MethodImplOptions.InternalCall)]
    [FreeFunction("PerlinNoise::NoiseNormalized", IsThreadSafe = true)]
    public static extern float PerlinNoise1D(float x);

    public static float Sin(float f) => (float)Math.Sin(f);   // managed, System.Math
    public static float Cos(float f) => (float)Math.Cos(f);   // managed, System.Math
}
```

No managed arithmetic at all on the Perlin path. `IsThreadSafe = true` ⇒ stateless ⇒ the port may be
called from any number of threads. (`PerlinNoise1D` is declared with the same `FreeFunction` name; the
native side resolves it by C++ overload. It is not used by Valheim.)

### 2.2 `UnityEngine.Random`

```csharp
[NativeHeader("Runtime/Export/Random/Random.bindings.h")]
public static class Random
{
    [Serializable] public struct State { [SerializeField] int s0, s1, s2, s3; }   // exactly 4 ints

    [StaticAccessor("GetScriptingRand()", StaticAccessorType.Dot)]
    public static State state { get { get_state_Injected(out var r); return r; } set { set_state_Injected(ref value); } }

    public static extern float value { [MethodImpl(InternalCall)] [FreeFunction] get; }

    public static Vector2 insideUnitCircle { get { GetRandomUnitCircle(out var o); return o; } }  // native, no managed maths

    [MethodImpl(InternalCall)] [NativeMethod("SetSeed")]
    [StaticAccessor("GetScriptingRand()", StaticAccessorType.Dot)]
    public static extern void InitState(int seed);

    [MethodImpl(InternalCall)] [FreeFunction]
    public static extern float Range(float minInclusive, float maxInclusive);

    public static int Range(int minInclusive, int maxExclusive) => RandomRangeInt(minInclusive, maxExclusive);

    [MethodImpl(InternalCall)] [FreeFunction] private static extern int RandomRangeInt(int minInclusive, int maxExclusive);
    [MethodImpl(InternalCall)] [FreeFunction] private static extern void GetRandomUnitCircle(out Vector2 output);

    [MethodImpl(InternalCall)] private static extern void get_state_Injected(out State ret);
    [MethodImpl(InternalCall)] private static extern void set_state_Injected([In] ref State value);
}
```

Answers to the questions this was meant to settle:

* **`insideUnitCircle` is NOT implemented in managed code.** The property is a one-line wrapper over the
  native `GetRandomUnitCircle`. There is no managed rejection loop — see §3.5 for the real algorithm
  (2 draws, no rejection).
* **`Range(int,int)` is a pure pass-through** to `RandomRangeInt`; no managed clamping, no swap.
* **`Range(float,float)` is fully native.** Both parameters are named `*Inclusive`.
* **`Random.state` is 4 `int`s and the accessors are raw 16-byte copies** (§3.4 — the `movups` pair at
  rva `0x000B0CD0`/`0x000B0CE0`; the reference to §3.2 was wrong), so the struct's field order is the
  generator's memory order.
* `ColorHSV()` draws 4 values via `value`; irrelevant to world generation but a trap if a mod calls it.

---

## 3. Binary evidence

### 3.1 The permutation table — confirmed

At **file offset `0x1BE0BC0`** (rva `0x1BE1DC0`, `.rdata`) there are **512 `int32`**. Verified
programmatically: every value is in `0..255`, the first 256 are a permutation of `0..255`, and entries
256..511 repeat entries 0..255 exactly. It is Ken Perlin's reference table, starting
`151, 160, 137, 91, 90, 15, 131, 13, …` and ending `… 215, 61, 156, 180`.
**Corrected (reviewer):** two different hashes were being conflated. Recomputed from the file:

* SHA-256 of the **first 1024 bytes** (the 256 distinct entries, little-endian `int32`):
  `ea3cf748ef2eed46c8d386654894efb4a0aca29e79789c2d3a4a27b57352cecd`
* SHA-256 of the **full 2048 bytes** (all 512 entries):
  `0e2324949e99f3333c190a2d72dc1c4d3d195a1d6f548fa73962188ff3868b07`

The 256 values printed in §4.1 were compared entry by entry with the file: identical, 0 differences.

**It is the table `NoiseNormalized` uses.** `PerlinNoise::Noise(float,float)` at rva `0x0054B170` loads
it with `lea r11, [rip+0x1696C40]` at rva `0x0054B179` (→ `0x1BE1DC0`), and index 2315 of the icall
table points at the wrapper that calls that function (§1.1). This closes the "Unverified: whether this
table is the one NoiseNormalized uses" item in the knowledge base.

### 3.2 `PerlinNoise::NoiseNormalized` — the normalisation constants

`0x000AC7C0`, 30 bytes, complete:

```
000AC7C0  4883ec28          sub  rsp, 0x28
000AC7C4  e8a7e94900        call 0x54B170                    ; PerlinNoise::Noise(float,float)
000AC7C9  f30f58052f89cd01  addss xmm0, [rip+0x1CD892F]      ; rva 0x1D85100 = 0x3F30A3D7 = 0.69f
000AC7D1  f30f5e05378acd01  divss xmm0, [rip+0x1CD8A37]      ; rva 0x1D85210 = 0x3FBDD2F2 = 1.483f
000AC7D9  4883c428          add  rsp, 0x28
000AC7DD  c3                ret
```

So `Mathf.PerlinNoise(x,y) = (Noise(x,y) + 0.69f) / 1.483f`. The 1-D wrapper at `0x000AC7E0` is
identical instruction for instruction and loads the same two constants (`0x1D85100`, `0x1D85210`); only
the `call` target (`0x0054B3F0`) and the two rip-relative displacements differ, so it is not literally
byte-identical.

Two details that matter for bit-exactness:

* it is a **`divss`**, not a multiply by a precomputed reciprocal. Replacing `/1.483f` with
  `*(1f/1.483f)` produces a different result — measurably (§6.3, 2 pixels out of 217 959).
* `0.69f` = `0x3F30A3D7`, `1.483f` = `0x3FBDD2F2`. Write them as C# `float` literals, not doubles.

Corollary: at every integer lattice point the raw noise is exactly `0f`, so
`Mathf.PerlinNoise(n, m) == 0.69f/1.483f == 0.46527308f` (`0x3EEE3846`) for all integers.

### 3.3 `PerlinNoise::Noise(float,float)` — rva `0x0054B170` .. `0x0054B3ED` (637 bytes)

Constants referenced, all read out of `.rdata`:

| rva of the instruction | constant rva | bits | value | role |
|---|---|---|---|---|
| `0x0054B179` | `0x1BE1DC0` | — | permutation table | |
| `0x0054B191` | `0x1D85198` | `0x3F800000` | `1.0f` | clamp and `x−1` |
| `0x0054B19A` | `0x1D87A40` | `0x7FFFFFFF` | abs mask | **`andps` on both inputs** |
| `0x0054B217`, `0x0054B22C` | `0x1D854D0` | `0x40C00000` | `6.0f` | fade |
| `0x0054B239`, `0x0054B242` | `0x1D85558` | `0x41700000` | `15.0f` | fade |
| `0x0054B261`, `0x0054B26A` | `0x1D85530` | `0x41200000` | `10.0f` | fade |
| `0x0054B2B4` | `0x1D87A80` | `0x80000000` | sign mask | gradient sign flips |

Order of operations, exactly as executed:

1. `x = |x|`, `y = |y|` (`andps` with `0x7FFFFFFF`). **This is load-bearing for Valheim** — see §7.
2. `ix = (int)x`, `iy = (int)y` via `cvttss2si` (truncate toward zero; the operands are non-negative
   after step 1, so this is `floor`). `X = ix & 0xFF` (`movzx …, cl`), `Y = iy & 0xFF`.
3. `fx = x − (float)ix`, `fy = y − (float)iy` (`cvtdq2ps` of the **full** int, then `subss`).
4. `u = fade(min(1f, fx))`, `v = fade(min(1f, fy))`, where the clamp is `MINSS(1.0f, frac)`, i.e.
   `(1f < frac) ? 1f : frac`. **The clamped values are used only for the fade**; the gradients get the
   unclamped `fx`, `fy`.
   `fade(t) = (((t*6f) − 15f) * t + 10f) * ((t*t)*t)` — computed in that exact grouping.
5. `A = p[X] + Y`, `B = p[X+1] + Y`; `AA = p[A]`, `AB = p[A+1]`, `BA = p[B]`, `BB = p[B+1]`
   (this is the reference 3-D indexing with `Z = 0`).
6. Four gradients, each `grad(hash, gx, gy)` with the classic 12-gradient switch and `z = 0`:
   `g00 = grad(p[AA], fx, fy)`, `g10 = grad(p[BA], fx−1, fy)`,
   `g01 = grad(p[AB], fx, fy−1)`, `g11 = grad(p[BB], fx−1, fy−1)`.
   The compiled branch structure is literally `h<8 ? x : y` for `u` and
   `h<4 ? y : (h==12||h==14 ? x : 0)` for `v`, then `xorps` with `0x80000000` on bit 0 / bit 1 of the
   **full hash** (not of `h`), then `v + u`.
7. `l1 = (g11 − g01)*u + g01` (computed first), `l0 = (g10 − g00)*u + g00`,
   result `= (l1 − l0)*v + l0`. All `subss/mulss/addss`, i.e. `lerp` written as `(b−a)*t + a`.

`PerlinNoise::Noise(float)` at `0x0054B3F0` (leaf, 254 bytes) is the same code with `Y` fixed to 0 and
the y-gradient argument fixed to `0f`; because `fade(0f) == 0f` exactly, it is bit-identical to
`Noise(x, 0f)`.

**Measured output range** (9·10⁶ grid points over one full period `[0,256)²`, using the port below):
raw ∈ `[−0.891581, 0.9995063]`, so `Mathf.PerlinNoise` ∈ **`[−0.1359, 1.1392]`** — it is *not* clamped
to `[0,1]`. The `1.483` divisor implies the author assumed raw ∈ `[−0.69, 0.793]`. Do not clamp.
(The swing is larger than a textbook 2-D Perlin because the 3-D gradient set projected with `z = 0`
still contains the four `(±1,±1)` vectors, of length `√2`.)

The function is **even** in each axis (from the `andps`): `P(−a, b) == P(a, b) == P(a, −b)` —
verified, 0 violations in 900 000 random triples. It is also **periodic with period 256** on each axis
as a direct consequence of `X = ix & 0xFF`; note that this is only observable when the fractional part
survives the `+256` in `float` (it does not for arbitrary coordinates, so do not test it with random
values).

### 3.4 `UnityEngine.Random` — the generator

Every entry point starts with `mov r?, qword ptr [rip+…]` resolving to **`.data` rva `0x1FEF8B0`** — a
pointer to the process-wide `Rand` object whose first 16 bytes are `s0, s1, s2, s3`. `InitState`,
`state`, `value`, both `Range` overloads and `GetRandomUnitCircle` all use that same pointer, so there
is exactly one RNG and it is **not** thread-safe.

**`InitState(int seed)` / `set_seed`** — rva `0x000B0CA0`, complete:

```
000B0CA0  488b1509ecf301    mov   rdx, [rip+0x1F3EC09]   ; &state
000B0CA7  69c16589076c      imul  eax, ecx, 0x6C078965   ; 1812433253
000B0CAD  890a              mov   [rdx], ecx             ; s0 = seed
000B0CAF  ffc0              inc   eax
000B0CB1  894204            mov   [rdx+4], eax           ; s1 = seed*1812433253 + 1
000B0CB4  69c06589076c      imul  eax, eax, 0x6C078965
000B0CBA  ffc0              inc   eax
000B0CBC  69c86589076c      imul  ecx, eax, 0x6C078965
000B0CC2  894208            mov   [rdx+8], eax           ; s2 = s1*1812433253 + 1
000B0CC5  ffc1              inc   ecx
000B0CC7  894a0c            mov   [rdx+0xC], ecx         ; s3 = s2*1812433253 + 1
000B0CCA  c3                ret
```

`1812433253` = `0x6C078965` — the usual Unity/MT seeding multiplier, confirmed present and used here.
Note `+1` after each multiply (not `+i`), and `s0 = seed` verbatim. All arithmetic is unchecked int32.

**`get_state_Injected` / `set_state_Injected`** — `movups xmm0, [src]; movups [dst], xmm0`: a raw
16-byte copy, no transformation. `Random.state` therefore *is* `(s0,s1,s2,s3)` in that order.

**The step** (identical inline code in `Range(float,float)`, `get_value`, `RandomRangeInt`,
`GetRandomUnitCircle`), from rva `0x000B0D08`:

```
t      = (s0 << 11) ^ s0
s0 = s1 ; s1 = s2 ; s2 = s3
s3_new = s3 ^ t ^ (((s3 >> 11) ^ t) >> 8)      ; all shifts logical (shr)
return s3_new
```

`(((s3>>11) ^ t) >> 8)` expands to `(s3>>19) ^ (t>>8)`, so this is Marsaglia's **xorshift128 with
shifts (11, 8, 19)**. Implement it in the literal form above to avoid any doubt.

**`Random.value`** — rva `0x000B0D80`:

```
r = step() & 0x7FFFFF
return (float)(long)r * 1.1920930376163765E-07f     ; 0x34000001 == 1f/8388607f
```

The multiplier is the float **above** `2^-23`; it is exactly `1f/8388607f`, so `value ∈ [0, 1]`
inclusive at both ends, in steps of 1/8388607. `cvtsi2ss` is from a 64-bit register, exact for
`0..0x7FFFFF`.

**`Range(float minInclusive, float maxInclusive)`** — rva `0x000B0CF0`. After the same step:

```
f = (float)(long)(new_s3 & 0x7FFFFF) * 1.1920930376163765E-07f
return (1f - f) * maxInclusive + f * minInclusive
```

Three consequences, all of which were open questions in the knowledge base:

1. The interpolation is **reversed**: `f == 0` yields `max`, `f == 1` yields `min`. The naive
   `min + f*(max-min)` is a different float expression and will differ in the last bits.
2. There is **no `min == max` check** — `Range(20f, 20f)` **does consume a draw**, and it does **not**
   always return exactly `20f`. **Corrected (reviewer):** enumerating all 2²³ possible draw values and
   evaluating `(1f−f)*20f + f*20f` in float32, **209 715 of 8 388 608 (2.50 %) give `0x41A00001`
   (20.000002) instead of `0x41A00000`** — e.g. the draw `r = 2` (`f = 2·0x34000001`). The first draw of
   `InitState(744350289)`'s third call happens to land on 20f exactly (§6.2 D6), which is what the
   earlier claim generalised from. Never special-case `min == max`: consume the draw and evaluate the
   expression.
3. There is no swap or clamp for `min > max`.

**`RandomRangeInt(int minInclusive, int maxExclusive)`** — thunk at `0x000B0D60`, core at `0x00054900`:

```
if (min <  max) { r = step(); return (int)((uint)min + (r % (uint)(max - min))); }   // div r8d, unsigned
if (min >  max) { r = step(); return (int)((uint)min - (r % (uint)(min - max))); }
                 return min;                                                          // NO DRAW
```

* the divide is `DIV` (unsigned) with `EDX = 0`, so the numerator is the raw `uint32` — plain modulo,
  with the usual modulo bias.
* `max - min` is computed as a wrapping 32-bit subtract. `Range(int.MinValue, int.MaxValue)` therefore
  uses modulus `0xFFFFFFFF` (= 4294967295, **not** 2³²) and can never return `int.MaxValue`.
* `Range(a, a)` returns `a` **without advancing the state**. This differs from the float overload.

### 3.5 `GetRandomUnitCircle` — rva `0x000B0E70`, 312 bytes

```
a = Range(0f, 6.28318548f)     ; inlined; 2π as float = 0x40C90FDB, min = 0f   -> draw 1
cx = cosf(a)                   ; call rva 0x01A531A0
sy = sinf(a)                   ; call rva 0x01A54570
t = Range(0f, 1f)              ; inlined, min = 0f, max = 1f                   -> draw 2
r = sqrtss(t)                  ; sqrtss when t >= 0; the negative path calls the sqrtf error helper
out = Vector2(cx * r, sy * r)  ; unpcklps xmm9(cos), xmm6(sin) -> [x=cos*r, y=sin*r]
```

**Exactly 2 draws, no rejection loop.** The two CRT functions were identified, not guessed: their
IEEE-special-case handlers pass the strings `"cosf"` (rva `0x1AF0E28`, reached from `0x01A531A0`) and
`"sinf"` (rva `0x1AF0E20`, reached from `0x01A54570`) to the libm error reporter. So `x` uses **cos**
and `y` uses **sin**.

Both functions convert to `double`, evaluate a Cody–Waite / Payne–Hanek reduction plus the standard
`sin`/`cos` minimax polynomials (coefficients `−1/6, 1/120, −1/5040, …` and `−1/2, 1/24, −1/720, …`
present in `.rdata`), then `cvtsd2ss`. Both branch on a CPU-feature flag at `.data` rva `0x1FA9F70`
and have an AVX2/FMA variant. **Unverified:** whether the two code paths are bit-identical, and whether
`(float)Math.Cos((double)a)` in .NET matches the MSVC CRT in the last bit.

---

## 4. Reference implementation (C#, complete, compilable)

Drop these two files into the tool. They have no dependencies beyond `System`. **Both blocks were
extracted from this document verbatim, compiled against .NET 10 and run**: they reproduce every
reference value in §6.2 and every ground-truth result in §6.1.

### 4.1 `UnityPerlin.cs`

```csharp
using System;

/// UnityEngine.Mathf.PerlinNoise, transcribed from UnityPlayer.dll 6000.0.75f1
///   PerlinNoise::Noise(float,float)           rva 0x0054B170
///   PerlinNoise::NoiseNormalized(float,float) rva 0x000AC7C0
/// Stateless and thread-safe, like the original.
public static class UnityPerlin
{
    public const float NormAdd = 0.69f;    // 0x3F30A3D7
    public const float NormDiv = 1.483f;   // 0x3FBDD2F2

    /// UnityEngine.Mathf.PerlinNoise(float, float)
    public static float PerlinNoise(float x, float y) => (Noise(x, y) + NormAdd) / NormDiv;

    /// UnityEngine.Mathf.PerlinNoise1D(float)   (bit-identical to PerlinNoise(x, 0f))
    public static float PerlinNoise1D(float x) => PerlinNoise(x, 0f);

    /// PerlinNoise::Noise(float,float) — raw, measured range [-0.891581, 0.9995063]
    public static float Noise(float x, float y)
    {
        x = Abs(x);                        // andps 0x7FFFFFFF
        y = Abs(y);
        int ix = (int)x;                   // cvttss2si; operands are >= 0 so this is floor
        int iy = (int)y;
        int X  = ix & 0xFF;
        int Y  = iy & 0xFF;
        float fx = x - (float)ix;
        float fy = y - (float)iy;
        float u = Fade(MinSS(1f, fx));     // clamp feeds the fade only
        float v = Fade(MinSS(1f, fy));

        int A = P[X]     + Y;
        int B = P[X + 1] + Y;
        int AA = P[A], AB = P[A + 1], BA = P[B], BB = P[B + 1];

        float g00 = Grad(P[AA], fx,      fy);
        float g10 = Grad(P[BA], fx - 1f, fy);
        float g01 = Grad(P[AB], fx,      fy - 1f);
        float g11 = Grad(P[BB], fx - 1f, fy - 1f);

        float l1 = (g11 - g01) * u + g01;
        float l0 = (g10 - g00) * u + g00;
        return (l1 - l0) * v + l0;
    }

    // MINSS(dst, src) == (dst < src) ? dst : src   — matters only for NaN inputs
    static float MinSS(float dst, float src) => (dst < src) ? dst : src;

    static float Fade(float t)
    {
        float a = t * 6f;
        a = a - 15f;
        a = a * t;
        a = a + 10f;
        float t3 = (t * t) * t;
        return a * t3;
    }

    static float Grad(int hash, float x, float y)
    {
        int h = hash & 15;
        float u, v;
        if (h < 8) { u = x; v = (h >= 4) ? 0f : y; }
        else       { u = y; v = (h == 12 || h == 14) ? x : 0f; }
        if ((hash & 1) != 0) u = -u;       // xorps 0x80000000 on the FULL hash's bit 0
        if ((hash & 2) != 0) v = -v;
        return v + u;
    }

    static float Abs(float f) =>
        BitConverter.Int32BitsToSingle(BitConverter.SingleToInt32Bits(f) & 0x7FFFFFFF);

    // UnityPlayer.dll file offset 0x1BE0BC0 (rva 0x1BE1DC0) holds 512 int32 = this table twice.
    // NOTE: Perm256 must be declared BEFORE P - C# runs static field initialisers in declaration
    // order, so the other way round Build() reads a null Perm256.
    static readonly int[] Perm256 =
    {
        151, 160, 137,  91,  90,  15, 131,  13, 201,  95,  96,  53, 194, 233,   7, 225,
        140,  36, 103,  30,  69, 142,   8,  99,  37, 240,  21,  10,  23, 190,   6, 148,
        247, 120, 234,  75,   0,  26, 197,  62,  94, 252, 219, 203, 117,  35,  11,  32,
         57, 177,  33,  88, 237, 149,  56,  87, 174,  20, 125, 136, 171, 168,  68, 175,
         74, 165,  71, 134, 139,  48,  27, 166,  77, 146, 158, 231,  83, 111, 229, 122,
         60, 211, 133, 230, 220, 105,  92,  41,  55,  46, 245,  40, 244, 102, 143,  54,
         65,  25,  63, 161,   1, 216,  80,  73, 209,  76, 132, 187, 208,  89,  18, 169,
        200, 196, 135, 130, 116, 188, 159,  86, 164, 100, 109, 198, 173, 186,   3,  64,
         52, 217, 226, 250, 124, 123,   5, 202,  38, 147, 118, 126, 255,  82,  85, 212,
        207, 206,  59, 227,  47,  16,  58,  17, 182, 189,  28,  42, 223, 183, 170, 213,
        119, 248, 152,   2,  44, 154, 163,  70, 221, 153, 101, 155, 167,  43, 172,   9,
        129,  22,  39, 253,  19,  98, 108, 110,  79, 113, 224, 232, 178, 185, 112, 104,
        218, 246,  97, 228, 251,  34, 242, 193, 238, 210, 144,  12, 191, 179, 162, 241,
         81,  51, 145, 235, 249,  14, 239, 107,  49, 192, 214,  31, 181, 199, 106, 157,
        184,  84, 204, 176, 115, 121,  50,  45, 127,   4, 150, 254, 138, 236, 205,  93,
        222, 114,  67,  29,  24,  72, 243, 141, 128, 195,  78,  66, 215,  61, 156, 180,
    };

    static readonly int[] P = Build();
    static int[] Build()
    {
        int[] q = new int[512];
        for (int i = 0; i < 256; i++) { q[i] = Perm256[i]; q[i + 256] = Perm256[i]; }
        return q;
    }
}
```

Sanity check for the table (**corrected (reviewer)** — the two hashes were the wrong way round; both
recomputed from `UnityPlayer.dll` and from this listing): `Perm256` written back as 256 little-endian
`int32` (1024 bytes) hashes to `ea3cf748ef2eed46c8d386654894efb4a0aca29e79789c2d3a4a27b57352cecd`, and
the doubled 512-entry form (2048 bytes, i.e. the array `P` this code builds, and the full blob at file
offset `0x1BE0BC0`) hashes to `0e2324949e99f3333c190a2d72dc1c4d3d195a1d6f548fa73962188ff3868b07`.

### 4.2 `UnityRandom.cs`

```csharp
using System;

/// UnityEngine.Random, transcribed from UnityPlayer.dll 6000.0.75f1.
/// The game has ONE global instance (rva 0x1FEF8B0) and it is not thread-safe.
/// Give the tool one instance per worker instead of a static.
public sealed class UnityRandom
{
    public int s0, s1, s2, s3;                       // == UnityEngine.Random.State field order

    /// Random.InitState(int)  (rva 0x000B0CA0; Random.seed setter is the same function)
    public void InitState(int seed)
    {
        unchecked
        {
            s0 = seed;
            s1 = s0 * 1812433253 + 1;                // 0x6C078965
            s2 = s1 * 1812433253 + 1;
            s3 = s2 * 1812433253 + 1;
        }
    }

    /// Random.state get/set — a raw copy of the four ints, nothing else.
    public (int, int, int, int) GetState() => (s0, s1, s2, s3);
    public void SetState((int, int, int, int) st) => (s0, s1, s2, s3) = st;

    /// one xorshift128 step (shifts 11, 8, 19); returns the new s3
    public uint Next()
    {
        unchecked
        {
            uint x0 = (uint)s0, x1 = (uint)s1, x2 = (uint)s2, x3 = (uint)s3;
            uint t = (x0 << 11) ^ x0;
            s0 = (int)x1; s1 = (int)x2; s2 = (int)x3;
            uint w = x3 ^ t ^ (((x3 >> 11) ^ t) >> 8);
            s3 = (int)w;
            return w;
        }
    }

    public const float Scale = 1.1920930376163765E-07f;   // 0x34000001 == 1f/8388607f

    /// Random.value  (rva 0x000B0D80)
    public float Value() => (float)(long)(Next() & 0x7FFFFFu) * Scale;

    /// Random.Range(float, float)  (rva 0x000B0CF0) — ALWAYS consumes one draw
    public float Range(float minInclusive, float maxInclusive)
    {
        float f = (float)(long)(Next() & 0x7FFFFFu) * Scale;
        return (1f - f) * maxInclusive + f * minInclusive;      // note: reversed
    }

    /// Random.Range(int, int)  (rva 0x00054900) — consumes a draw ONLY when min != max
    public int Range(int minInclusive, int maxExclusive)
    {
        unchecked
        {
            if (minInclusive < maxExclusive)
                return (int)((uint)minInclusive + (Next() % (uint)(maxExclusive - minInclusive)));
            if (minInclusive > maxExclusive)
                return (int)((uint)minInclusive - (Next() % (uint)(minInclusive - maxExclusive)));
            return minInclusive;
        }
    }

    /// Random.insideUnitCircle  (rva 0x000B0E70) — exactly 2 draws, no rejection
    public (float x, float y) InsideUnitCircle()
    {
        float a = Range(0f, 6.28318548f);            // 2π as float, 0x40C90FDB
        float t = Range(0f, 1f);
        float r = MathF.Sqrt(t);                     // sqrtss, IEEE-exact
        return ((float)Math.Cos((double)a) * r,      // VARIANT — see §5.2
                (float)Math.Sin((double)a) * r);
    }
}
```

---

## 5. Variants

### 5.1 Perlin — all settled, listed so nobody re-opens them

| # | Variant | Verdict |
|---|---|---|
| P1 | `(n + 0.69f) / 1.483f` | **CORRECT** — 0 errors on 398 816 samples |
| P2 | `n * 0.5f + 0.5f` | rejected: 21 064 / 180 857 threshold errors |
| P3 | no normalisation | rejected: 129 228 / 180 857 |
| P4 | `(n + 0.69f) * (1f/1.483f)` | rejected: 0 threshold errors but **2 / 217 959** byte errors — the `divss` is real |
| P5 | `+0.70f` instead of `+0.69f` | rejected: 2 997 / 180 857 |
| P6 | `/1.48f` instead of `/1.483f` | rejected: 371 / 180 857 |
| P7 | `floor()` instead of the abs fold | rejected: 35 970 / 180 857 |
| P8 | smoothstep fade `3t²−2t³` | rejected: 4 925 / 180 857 |
| P9 | single permutation lookup (`p[A]` instead of `p[p[A]]`) | rejected: 54 350 / 180 857 |

### 5.2 Random — open variants for the dumper to close

| # | Item | Variants |
|---|---|---|
| R1 | `Range(float,float)` interpolation | **(a)** `(1f−f)*max + f*min` ← read from the binary · (b) `min + f*(max−min)` · (c) `max + f*(min−max)` · (d) `Mathf.Lerp(min,max,f)`. Discriminate with `D6`: (a) and (b) differ in the last bits for `Range(60f, 100f)`. |
| R2 | `Range(a,a)` float | **(a)** draws ← read from the binary · (b) no draw. `D7` settles it by comparing `Random.state` before/after. |
| R3 | `insideUnitCircle` trig | **(a)** MSVC CRT `cosf`/`sinf` · (b) .NET `MathF.Cos/Sin` · (c) `(float)Math.Cos((double)a)` ← the port's current choice. `D8` settles it; if (c) fails, try (b), then tabulate. |
| R4 | `insideUnitCircle` component order | **(a)** `x = cos, y = sin` ← from `unpcklps` + the `cosf`/`sinf` error strings · (b) swapped. `D8` settles it trivially. |
| R5 | `cosf`/`sinf` CPU path | the binary picks an AVX2/FMA variant at run time. If `D8` disagrees between two machines, the tool must ship a table or accept a 1-ULP error in `GetTerrainDelta` only. |

Already closed by the **ground truth in §6.1** (not §6.2, which is the dumper that has not run yet) and
needing no dumper: the `InitState` recurrence, the xorshift shifts and order, `Range(int,int)`'s modulo
mapping, `Range(int,int)` on the full `int` range, and the draw ordering in `WorldGenerator..ctor`.
**Corrected (reviewer):** the **no-draw case of `Range(a,a)` for ints is not** closed by §6.1 — no
world-generation call site passes `min == max` to the int overload, so no runtime artefact depends on
it. It rests on the disassembly alone (`jge 0x54940` / `jle 0x54980` → `mov eax, r10d; ret` without
touching the state; §3.4), which is unambiguous, and on dumper item **D7**.

---

## 6. The discriminating test

### 6.1 Ground truth A — the user's minimap cache. **Already executed; both natives passed.**

`Minimap.GenerateWorldMap` samples `WorldGenerator.GetBiome` and `GetBiomeHeight` at every pixel centre
and writes three gzip-compressed buffers next to the world. For world `asdasdasd`:

* `…\worlds_local\asdasdasd\cacheMinimapMeta` — 8 bytes: `int32 seed = -1772362158`, `int32 version = 1`.
* `cacheMinimapBiome` — gzip → 16 777 216 bytes = **2048 × 2048 `Color32`** (`Minimap.GetPixelColor(biome)`).
* `cacheMinimapMask` — gzip → 2048 × 2048 `Color32` (`Minimap.GetMaskColor`).
* `cacheMinimapHeight` — gzip → 8 388 608 bytes = 2048 × 2048 **half-floats** (`Mathf.FloatToHalf(biomeHeight)`).

**Newly determined prefab constants** (the knowledge base lists these as unverified; note the
*decompiled* field initialisers are `m_textureSize = 256` and `m_pixelSize = 64f` — the prefab overrides
both, so do not take the values from the source):
`Minimap.m_textureSize = 2048` (from the buffer sizes) and `Minimap.m_pixelSize = 12f` — derived from
the height buffer and `WorldGenerator.waterEdge = 10500f` (decomp line 174).
**Corrected (reviewer):** the floor is *not* at 875 px on all four axes. Re-measured on the row/column
through the centre: the `−400 m` floor (the buffer minimum is exactly `−400.0`) first appears at
`j−1024 = +875` and `i−1024 = +875`, but at `j−1024 = −876` and `i−1024 = −876`. That asymmetry *is* the
half-pixel offset, and it is what pins the mapping: `|k·p + p/2| > 10500` first at `k = +875` and
`k = −876` forces `p ∈ (11.9931, 12.0069]`, i.e. `p = 12`. Pixel centres are therefore

```
wx = (j - 1024) * 12f + 6f        wy = (i - 1024) * 12f + 6f        index = i*2048 + j
```

Cross-checks that confirm this mapping: the Meadows colour reaches a maximum radius of 5 098 m
(`GetBiome` falls back to BlackForest beyond `5000 + WorldAngle*100`), Swamp 6 000 m
(`maxMarshDistance` for worldGenVersion 2), Plains 8 000 m, Mistlands 10 000 m.

Biome colours found in this cache (`Color32` as stored, RGBA bytes):
`FF FF FF FF` = Ocean **and** Mountain **and** DeepNorth (they share white — do not use white pixels),
`92 A7 5C FF` Meadows, `6B 74 3F FF` BlackForest, `A3 72 58 FF` Swamp, `E7 AB 78 FF` Plains,
`33 33 33 FF` Mistlands, `7B 20 20 FF` AshLands.

**Test A — `Mathf.PerlinNoise` alone, no RNG involved.** `WorldGenerator.GetForestFactor` is static and
seed-independent: `DUtils.Fbm(pos * 0.01f * 0.4f, 3, 1.6f, 0.7f)`, three chained Perlin calls.
`Minimap.GetMaskColor` turns it into the mask texture for pixels whose height ≥ 30:
Meadows → `r = 255` iff `forestFactor < 1.15f`; Plains → `r = 255` iff `forestFactor < 0.8f`.

> Result: **180 857 pixels, 0 mismatches.** (226 899 pixels skipped because their stored half-height was
> below 30.5 m, where the `height < 30f` branch or half-rounding could interfere.)

**Test D — a numeric, byte-level check.** For Mistlands pixels with height ≥ 30 the mask's green channel
is `(byte)Mathf.Round(Clamp01(1f − Utils.SmoothStep(1.1f, 1.3f, forestFactor)) * 255f)`
(`Utils.SmoothStep(lo,hi,x) = n*n*(3f−2f*n)`, `n = Clamp01((x−lo)/(hi−lo))`; `Color`→`Color32` is
`(byte)Mathf.Round(Clamp01(c)*255f)`, round-half-to-even).

> Result: **217 959 pixels, 0 byte mismatches**, and this test is sharp enough to reject the
> reciprocal-multiply variant P4 (§5.1).

**Test B/C — `Random` via the biome map.** `InitState(-1772362158)` then seven `Range(-10000,10000)` /
`Range(int.MinValue,int.MaxValue)` draws must give `WorldGenerator`'s offsets. `GetBiome`'s mask terms
give a one- and two-sided constraint per pixel, e.g. every Swamp pixel implies
`PerlinNoise((float)((double)off0+wx)*0.0010000000474974513, …) > 0.6f`, and every Plains / BlackForest /
Meadows pixel inside the Mistlands distance band implies the off4 mask is `<= 0.4f`. Brute-forcing all
20 000 candidate offsets:

| offset | predicted by the port | survivors of the brute force | violations over all candidate pixels |
|---|---|---|---|
| `m_offset0` (Swamp, `>0.6f`) | **−6080** | `{−6080}` — unique | 0 / 62 619 |
| `m_offset4` (Mistlands, two-sided) | **718** | `{718}` — unique | 0 / 612 048 |
| `m_offset2` (BlackForest, two-sided) | **−7704** | `{−7704}` — unique | 0 / 191 347 |
| `m_offset1` (Plains, two-sided) | **4986** | `{4986, 4987, 4988}` | 0 / 484 433 |

Three offsets recovered **uniquely** out of 20 000 candidates, all equal to what the port predicts.
`m_offset4` is the **seventh** draw, so this also proves that draws 4, 5 and 6 (`m_offset3`,
`m_riverSeed`, `m_streamSeed`) each consumed exactly one step and that `Range(int.MinValue,int.MaxValue)`
consumes exactly one step. The full predicted sequence for this seed is

```
off0=-6080  off1=4986  off2=-7704  off3=-59  riverSeed=744350289  streamSeed=952983356  off4=718
```

*Reproducing this:* the harness lives in `…\scratchpad\probe\val\` (`Natives.cs`, `Program.cs`,
`Ref.cs`, `Range.cs`, `TestD.cs`, `Variants.cs`, `Extra.cs`, `val.csproj`;
`dotnet run -c Release -- [<none>|ref|range|d|v|x]`, where no argument runs Tests A/B/C, `d` runs
Test D, `v` runs the variant table, `ref`/`x` print the reference vectors). `…\scratchpad\probe\`
also holds the static-analysis scripts (`pe.py`, `ds.py`, `func.py`, `callers.py`, `riprefs.py`,
`ptrto.py`, `strs.py`; capstone was installed for `ds.py`) and `…\scratchpad\probe\spectest\` holds
§4's code extracted from this document and compiled. Everything opens the game and save files
read-only. **Never write to `worlds_local` or the Steam Cloud save folder — Steam Cloud syncs them.**

### 6.2 Ground truth B — the BepInEx dumper plugin

One launch must settle §5.2 and give a permanent regression corpus. The plugin should run once
(e.g. on `F10`, or from `ZNet.Awake`) on the **main thread**, save `UnityEngine.Random.state` first and
restore it last, and write a single file. **Record every float as its 32-bit pattern**
(`BitConverter.SingleToUInt32Bits(v).ToString("X8")`), never as decimal text, and every state as four
`int`s. Format: one `key=value` per line, values comma-separated.

| id | what to record | count | why |
|---|---|---|---|
| **D1** | `Mathf.PerlinNoise(x,y)` for a fixed probe list, plus `Mathf.PerlinNoise1D(x)` for the same `x` | 24 + 24 | §7 list below; confirms the whole Perlin port and the 1-D identity |
| **D2** | `Mathf.PerlinNoise` on a 512×512 grid `x = i*0.0517f - 60f`, `y = j*0.0517f - 60f` | 262 144 | dense coverage incl. negatives; compare bit-for-bit |
| **D3** | `Mathf.PerlinNoise` at the exact arguments Valheim uses: for `wx, wy` ∈ {−10494, −6000, −12, 0, 6, 4242, 10494} × same, log `(float)((double)off+wx)*0.0010000000474974513` with `off ∈ {-6080, 718, 4986, -7704}` and the base-height pairs `(wx+100000-6080)*0.002f*0.5f` etc. | ~200 | the real operating range, up to ~4.4·10⁴ |
| **D4** | after `Random.InitState(s)` for `s ∈ {0, 1, -1, 12345, int.MinValue, int.MaxValue, -1772362158}`, read `Random.state` | 7 | proves the seeding recurrence directly instead of by inference |
| **D5** | `InitState(-1772362158)`, then 64 × `Random.value`, logging state after each | 64 | proves the step and the `1f/8388607f` scale |
| **D6** | `InitState(744350289)`, then **in exactly this order**: `Range(60f,100f)`, `Range(60f,<result of the previous call>)`, `Range(20f,20f)`, `Range(-10000f,10000f)`, `Range(-10000f,10000f)`, `Range(0f, Mathf.PI*2f)`, then separately from `InitState(744350289)`: `Range(100f,60f)` (min>max) — logging `Random.state` after each | 7 | settles **R1**; the asymmetric pairs separate `(1−f)*max+f*min` from `min+f*(max−min)`. The order matters: the expected values below were produced in it. |
| **D7** | state before and after `Range(20f,20f)` and before/after `Range(5,5)` | 4 states | settles **R2** and re-confirms the int no-draw case |
| **D8** | `InitState(12345)`, then 8 × `Random.insideUnitCircle`, logging both components and the state after each | 8 | settles **R3**, **R4**; state deltas confirm "2 draws per call" |
| **D9** | `InitState(0)`, 100 000 × `Random.value` discarding results, then `Random.state` and one more `value` | 1 | long-run drift: catches any carry/ordering error that only shows after thousands of draws |
| **D10** | `InitState(-1772362158)`, then 16 × `Range(-10000,10000)`; also `Range(int.MinValue,int.MaxValue)` from `InitState(7)` | 17 | regression corpus for the int path |
| **D11** | `WorldGenerator.instance`'s private `m_offset0..4`, `m_riverSeed`, `m_streamSeed` by reflection | 7 | independent confirmation of §6.1's recovered offsets |
| **D12** (added, reviewer) | `Math.Sin`, `Math.Cos`, `Math.Atan2`, `Math.Pow` bit patterns from inside the game: `Sin/Cos` at `{0, 1e-8, 0.5, 1, 2, 3.14159265, 6.2831853, 20.0, 1000.0, −7.5}`, `Atan2(wx,wy)` at the 7×7 `wx,wy` grid of **D3**, `Pow(x,1.5)` and `Pow(x,4.0)`/`Pow(x,1.4)`/`Pow(x,2.0)` at `x ∈ {0.05, 0.28, 0.5, 0.71, 0.9, 1.0}`, plus `WorldGenerator.WorldAngle(wx,wy)` itself over that grid | ~120 | closes risk 7: Mono's libm vs .NET 10's. `WorldAngle` reaches `GetBiome`, so this is a biome-correctness item, not a river-only one |

Edge cases that must be in the **D1** probe list (input → expected, as produced by the port in §4):

| x, y | `Noise` bits | `PerlinNoise` bits | decimal |
|---|---|---|---|
| `0, 0` | `0x00000000` | `0x3EEE3846` | 0.46527308 |
| `0.5, 0.5` | `0xBE800000` | `0x3E97E886` | 0.2966959 |
| `-0.5, 0.5` | `0xBE800000` | `0x3E97E886` | same as `0.5,0.5` → **abs fold** |
| `0.5, -0.5` | `0xBE800000` | `0x3E97E886` | abs fold, other axis |
| `1, 1` / `2, 3` / `5, 7` / `255, 255` / `256, 256` | `0x00000000` | `0x3EEE3846` | exact integers |
| `0.25, 0.75` | `0xBD9F0000` | `0x3ED36A82` | 0.41292197 |
| `123.456, 789.012` | `0x3E8555FB` | `0x3F24108F` | 0.64087766 |
| `-123.456, -789.012` | `0x3E8555FB` | `0x3F24108F` | abs fold, both axes |
| `257, 1.5` | `0x3E800000` | `0x3F224403` | 0.6338503 (wrap past 256) |
| `255.99998, 0` | `0xB7800000` | `0x3EEE36ED` | 0.4652628 (just below the lattice) |
| `1e-7, 1e-7` | `0x33D6BF95` | `0x3EEE3849` | 0.46527317 |
| `44000, -44000` | `0x00000000` | `0x3EEE3846` | largest magnitude Valheim reaches |
| `100000, 100000` | `0x00000000` | `0x3EEE3846` | the ~1e5 case |
| `119999, 0.5` | `0x00000000` | `0x3EEE3846` | float ULP at 1.2e5 is 0.0078 |
| `16777216, 0.5` | `0xBE800000` | `0x3E97E886` | 2²⁴: the fraction is gone |
| `16777218, 3.25` | `0x00000000` | `0x3EEE3846` | beyond 2²⁴ |
| `-16.08, -6.08` | `0x3D99CBA5` | `0x3F0412B8` | 0.5159106 (a real GetBiome mask argument) |
| `-20.5, 20.499` | `0xBE8059F0` | `0x3E97ABE0` | 0.29623318 (extreme mask argument) |
| `110.00001, 90.00001` | `0xA79FFF38` | `0x3EEE3846` | a real base-height argument |
| `4.2, -4.2` | `0x3DADBBE5` | `0x3F05C0F3` | 0.5224754 |

Expected `Random` values from the port, for **D4 – D10**:

```
InitState(0)            -> 0, 1, 1812433254, 1900727103
InitState(1)            -> 1, 1812433254, 1900727103, -603986212
InitState(-1)           -> -1, -1812433252, 1724139405, 110473122
InitState(12345)        -> 12345, 2003863422, 878305975, 684417332
InitState(int.MinValue) -> -2147483648, -2147483647, -335050394, -246756545
InitState(int.MaxValue) -> 2147483647, 335050396, -423344243, -2037010526
InitState(-1772362158)  -> -1772362158, 1690419291, 383686376, 619426185

D10  InitState(-1772362158); 16 x Range(-10000,10000) =
     -6080,4986,-7704,-59,3937,-2996,718,4160,-6254,-5991,-2471,-4984,-9065,-2665,2371,2488
     state after = 679000935,-1675939961,-960914925,903472488
     InitState(7); Range(int.MinValue,int.MaxValue) = -599867598
D7   InitState(7) state = 7,-197869116,-1864477099,1547603082 ; Range(5,5)=5 leaves it UNCHANGED
D5   InitState(-1772362158); value x8 (bits) =
     3F175461 3F44F996 3F538632 3F6E83AC 3F3BC4A3 3F1ABE79 3E3DD1F1 3F1150E1
D6   InitState(744350289):
     Range(60f,100f)        = 0x42BF3B2E (95.615585)
     Range(60f,95.615585f)  = 0x42A84292 (84.13002)
     Range(20f,20f)         = 0x41A00000 (20)      state -> 453124588,-661780553,908674882,-1218116351
     Range(-10000f,10000f)  = 0xC5A7D51C (-5370.6387), then 0xC60BD134 (-8948.301)
     Range(0f, 2pi)         = 0x40A67D69 (5.202809)     [2pi as float = 0x40C90FDB]
     fresh InitState(744350289); Range(100f,60f) = 0x4280C4D3 (64.38442)
     The very first draw is the sharpest R1 discriminator: variant (a) gives 0x42BF3B2E (95.615585)
     for Range(60f,100f); variant (b) `min + f*(max-min)` gives 0x4280C4D2 (64.384415) on the same
     draw. They differ by 31, not by an ULP - one dumped value decides it.
     [corrected (reviewer): variant (b) is 0x4280C4D2, not 0x4280C4D3. 0x4280C4D3 (64.38442) is what
     variant (a) returns for the *reversed* call Range(100f,60f) on that draw; the two are 1 ULP apart
     and the dumper comparison must use the exact bits.]
D8   InitState(12345); insideUnitCircle x4 (x,y bits) =
     (0xBE8AB290,0x3E286F63) (0xBE91BFE2,0x3D71976C)
     (0xBD16DC69,0xBF41E1B3) (0xBF29BE2C,0xBE57ECC2)
     state after 4 calls = 589364589,1622567041,1622784569,-1681782606
D9   InitState(0); 100000 steps -> state = -578533609,1716075846,1054780398,1693974205
     next value bits = 0x3ED2C72E
```

The `insideUnitCircle` numbers above use `(float)Math.Cos((double)a)`; a mismatch there and nowhere else
means **R3** must move to variant (b).

### 6.3 The regression test the tool should keep

Ship `Test A`, `Test D` and the offset recovery of §6.1 as a unit test that runs against the user's
minimap cache (read-only), plus the **D1–D10** corpus as a static table. After any Valheim or Unity
update, re-run `tools\check-game-version.ps1`, re-hash `UnityPlayer.dll`, and re-run all of them.

---

## 7. Consequences for Valheim world generation

* **The abs fold is not cosmetic.** `WorldGenerator.GetBiome` evaluates the four biome masks at
  `(float)((double)m_offsetN + (double)w) * 0.0010000000474974513` with `m_offsetN ∈ [−10000, 9999]`
  and `w ∈ [−10500, 10500]`, so the argument ranges over roughly `[−20.5, 20.5]` and **is routinely
  negative**. Each mask is therefore mirrored about the line `w = −m_offsetN`. `GetForestFactor` and
  `GetDeepNorthHeight` (`wx*0.1f`, `wx*0.4f`, decomp lines 1351–1352) also feed raw, signed world
  coordinates. A `floor()`-based Perlin flips the forest mask on 20 % of the tested pixels
  (35 970 / 180 857, §5.1 P7) — a different-looking but wrong world.
* **`GetForestFactor` is `(pos * 0.01f) * 0.4f`, not `pos * 0.004f`.** **Corrected (reviewer):** the
  earlier shorthand `pos*0.004f` is a real trap. `WorldGenerator.GetForestFactor` (decomp lines
  1412–1415) is `float num = 0.4f; return DUtils.Fbm(pos * 0.01f * num, 3, 1.6f, 0.7f);` — two separate
  float32 multiplies. Folding them into `0.004f` changes the last bit of the argument for 118 031 of
  200 000 sampled world coordinates, and re-running Test D with the folded constant produces
  **106 byte mismatches out of 217 959** (0 with the two-step form). Keep both multiplies, in that
  order.
* Largest magnitude actually fed to `PerlinNoise` is about **4.4–4.8·10⁴**: the per-biome detail noise
  uses `(wx + 100000) * 0.4f` (`GetMeadowsHeight` and friends; `GetAshlandsHeight` also adds
  `m_offset3`) with `|wx| ≤ 10 500`. Well below 2²⁴, so `cvttss2si` and the fraction subtraction are
  exact. Nothing in vanilla reaches the 2³¹ `cvttss2si` indefinite value.
* **Perlin has period 256.** `GetBaseHeight`'s largest argument is about 1 200 (the `×0.01` octave of
  `wx + 100000 + m_offset0`), so the continent shape wraps the table several times; the `×0.001` biome
  masks (`|arg| ≤ 20.5`) stay well inside a single period.
* `Mathf.PerlinNoise` is thread-safe and Valheim calls it from the `HeightmapBuilder` thread. The port
  may be parallelised freely. `UnityRandom` may not — give each worker its own instance.
* `WorldGenerator..ctor` draw order is `off0, off1, off2, off3, riverSeed, streamSeed, off4` — seven
  `Range(int,int)` draws — and the constructor saves/restores `Random.state` around them.
  `PlaceRivers` and `PlaceStreams` re-seed with `InitState(m_riverSeed)` / `InitState(m_streamSeed)`.
* `RenderRivers` calls `Range(widthMin, widthMax)` once per step and `PlaceStreams` calls
  `Range(20f, 20f)`-shaped code per point: **every one of those consumes a draw** (R2/§3.4), so a port
  that skips the degenerate case desynchronises the whole stream pass.
* `GetTerrainDelta` (both `WorldGenerator`'s and `ZoneSystem`'s) calls `Random.insideUnitCircle` ten
  times per invocation and is used by the location-placement filters. It consumes **20 draws** per call.
  Verified: `WorldGenerator.GetTerrainDelta` line 1420 `int num = 10;` and line 1427
  `Random.insideUnitCircle * radius`; `ZoneSystem.GetTerrainDelta` lines 2703/2710 are the same code.
* **Added (reviewer) — a second native-maths surface this document does not cover.** Besides the two
  Unity functions specified here, world generation depends on `System.Math` transcendentals, which are
  runtime code, not managed arithmetic: `WorldGenerator.WorldAngle` (line 881,
  `Math.Atan2` + `Math.Sin`) feeds every `GetBiome` distance band, `GetBaseHeight` line 1142 and the
  Ashlands path lines 1261–1273 use `Math.Pow`, `RenderRivers` line 558 uses three `Math.Sin`, and
  `FindStreamEndPoint` line 385 uses `Mathf.Sin`/`Mathf.Cos`. The port runs these on .NET 10's CRT; the
  game runs them on Mono's. **Unverified:** that the two agree in the last bit. Unlike `insideUnitCircle`
  (§8.4) this one **does** reach biomes, through `WorldAngle`.

---

## 8. Risk

**If a variant cannot be settled, this is what degrades.**

1. **Perlin (settled).** If it were wrong, *everything* would be wrong — biomes, heights, rivers,
   locations — and silently: the output would look like a plausible Valheim world for a different seed.
   This is now closed by 398 816 matching real samples (§6.1) and by the unique recovery of three world
   offsets out of 20 000 candidates; a wrong Perlin cannot produce either. Caveat in item 6 below.
2. **`Random.InitState` / step / `Range(int,int)` (settled).** A wrong seeding recurrence or draw
   mapping shifts every offset, which relocates every biome. Closed by §6.1.
3. **`Range(float,float)` (R1/R2, read but not executed).** These feed *only* `PlaceRivers` (widths),
   `PlaceStreams` (candidate points and angles) and `RenderRivers` (per-step width). If wrong:
   * **Biomes: unaffected.** `GetBiome` does not read rivers. The biome browser and any
     biome-based seed search stay exact.
   * **Heights: wrong wherever a river or stream passes.** `AddRivers` lowers terrain to 24–28 m in
     those cells. Everything else — continent shape, mountains, the ×200 scaling — stays exact.
   * **Locations: wrong in the second order.** Placement filters on height and terrain delta, so a
     changed river bed can move or drop a candidate. Boss and unique locations, which sit on broad
     terrain, are unlikely to move; small dungeon entrances near water may.
   * Mitigation if it cannot be settled: ship the tool with rivers disabled and mark every height it
     reports as "pre-river", or have the dumper export `WorldGenerator`'s private `m_riverPoints`
     once per seed of interest.
4. **`insideUnitCircle` trig (R3/R4/R5, structure known, last bit open).** Used only by
   `GetTerrainDelta`, i.e. only by location placement. A 1-ULP difference in `cos`/`sin` changes a
   sample point by ~10⁻⁵ m, which flips a location's `m_maxTerrainDelta` test only when the delta sits
   exactly on the threshold. Expected effect: a small number of locations differ near thresholds.
   Biomes and heights: unaffected. If R5 turns out to be CPU-dependent, the game itself is not
   reproducible across machines for those locations, and the tool should say so rather than pretend.
5. **Version drift.** Every claim here is tied to the two SHA-256 hashes in §1. A Unity upgrade in a
   future Valheim patch can change `PerlinNoise.cpp` or `Random.h`. The tool must record the
   `UnityPlayer.dll` hash it was validated against and refuse (or warn loudly) when the installed game
   does not match.
6. **Residual risk in what is "settled".** Tests A/D/B/C are threshold and 8-bit comparisons, not
   full 32-bit equality. They are sharp — they reject a reciprocal multiply — but they do not *prove*
   bit-equality at every input. The only cheap way to close that gap completely is **D2** (a dense grid
   of raw `Mathf.PerlinNoise` bit patterns from the dumper), which costs one game launch. Do it.
7. **Added (reviewer) — `System.Math` transcendentals (Mono vs .NET 10).** `Math.Sin`, `Math.Cos`,
   `Math.Atan2`, `Math.Pow` are not part of this spec's two functions but are native in both runtimes
   and are **unproven** to agree bit for bit. Blast radius, by call site (§7):
   * `WorldAngle` (`Atan2` then `Sin`) → the Mistlands / Plains / BlackForest / Meadows **distance
     bands** in `GetBiome`. A last-bit difference moves a band edge by well under a micrometre, so it
     can only flip pixels sitting exactly on a band boundary — rare, but *not* zero, and it breaks the
     "biomes are exact" guarantee that risks 3 and 4 rely on.
   * `Math.Pow` in `GetBaseHeight`/Ashlands → heights, everywhere.
   * `Math.Sin` in `RenderRivers`, `Mathf.Sin/Cos` in `FindStreamEndPoint` → river and stream geometry.
   Cheapest closure: have the dumper log `Math.Sin/Cos/Atan2/Pow` bit patterns for a fixed probe list
   from inside the game (one more block in the D-series) and diff against the tool. Until then the
   biome browser should be described as exact *except* within one float ULP of a distance-band edge.

---

## 9. Open questions (added by the reviewer)

1. **`System.Math` transcendentals.** Nothing proves Mono's `sin`/`cos`/`atan2`/`pow` inside Valheim
   equal .NET 10's for every argument world generation uses. Closed by dumper item **D12**. This is the
   only unresolved item that can move a **biome** boundary (via `WorldGenerator.WorldAngle`, decomp
   line 881).
2. **Test B/C's brute-force counts were only partly re-derived.** The reviewer independently confirmed
   `m_offset0 = −6080`: there are exactly **62 619** Swamp pixels in the cache and all of them satisfy
   `PerlinNoise((float)(−6080+wx)·0.0010000000474974513, …) > 0.6f`, while the neighbouring candidate
   `−6079` already fails on 55 of every third Swamp pixel. The uniqueness counts claimed for
   `m_offset1` (484 433), `m_offset2` (191 347) and `m_offset4` (612 048) were **not** re-derived;
   they are consistent with everything else here but stand on the original run alone.
3. **`(int)float` conversion semantics in the port.** §4.1 relies on `(int)x` matching `cvttss2si`.
   Valheim never feeds `|x| ≥ 2³¹` (max ≈ 4.8·10⁴, §7), so this cannot bite in practice.
   **Unverified:** whether .NET 10 saturates out-of-range float→int conversions instead of producing
   `0x80000000`. If the tool ever accepts arbitrary user coordinates, clamp the input rather than rely
   on the conversion.
4. **`Mathf.PerlinNoise1D` bit-identity.** Confirmed by disassembly as the `y = 0` specialisation
   (`PerlinNoise::Noise(float)` rva `0x0054B3F0`, 254 bytes, leaf, ends `ret` at `0x0054B4ED`);
   equality with `Noise(x, 0f)` additionally needs `(l1−l0)·0f + l0 == l0`, which holds for every finite
   `l0 ≠ −0f`. Valheim never calls it (0 occurrences of the string in `assembly_valheim.dll` and
   `assembly_utils.dll`, re-checked), so it is not worth closing further.
5. **`cosf`/`sinf` CPU-path dependence (R5) is still open** and can only be settled by running **D8**
   on two machines with different CPUs. The reviewer confirmed the branch exists in both functions
   (`cmp dword ptr [rip+…], 0` → `.data` rva `0x1FA9F70` at `0x01A531A4` and `0x01A54574`).

---

## Verification

Checked by an independent reviewer against `UnityPlayer.dll`
SHA-256 `4D161E15D8CCDB32EB73262E7A3E0A66F8C175B50A38E22AE5B0E8FB9AEA98F3` (34 413 480 bytes,
FileVersion `6000.0.75.2503836`, ProductVersion `6000.0.75f1 (26349cd2a5c8)` — all three re-read from
the file), `UnityEngine.CoreModule.dll` SHA-256 `FBA3821A…19D990` (1 801 128 bytes), the decompiled
sources, and the user's `asdasdasd` minimap cache (opened read-only; nothing in the game or save folders
was written).

**Re-derived from the binary, independently of this document's quotes — all confirmed:**
PE layout and both offset↔rva rules; the icall table (names at fileoff `0x1AF7C00`, pointers at
`0x1AFF140`, **exactly 3751** contiguous entries — indices 2315/2316/2356–2363/2368 resolve to the
functions §1.1 lists, including `set_seed` = `InitState`); `NoiseNormalized`'s 30 bytes and its two
constants `0x3F30A3D7`/`0x3FBDD2F2` with a real `divss`; all 637 bytes of `PerlinNoise::Noise(float,float)`
traced instruction by instruction — the `andps 0x7FFFFFFF` abs fold on **both** inputs, `cvttss2si`,
`X = ix & 0xFF`, the unclamped `fx/fy` into the gradients and `MINSS(1f, frac)` into the fade only, the
fade grouping, the 12-gradient branch (`h<8` / `h−12` with `test eax, 0xFFFFFFFD`), the sign flips from
bits 0/1, the gradient evaluation order (g11, g01, g10, g00 — irrelevant, they are pure) and the three
`(b−a)*t+a` lerps; the 512-entry permutation table (compared entry by entry with §4.1: identical);
`InitState`'s 43 bytes; the xorshift step in all four entry points; `Random.value`'s
`0x34000001` (= `1f/8388607f`, and `8388607·scale == 1.0f` exactly, so the range really is closed);
`Range(float,float)`'s `(1−f)*max + f*min` with no `min == max` guard and no swap; `RandomRangeInt`'s
three branches including the no-draw `min == max` path at `0x54980`; `GetRandomUnitCircle`'s two draws,
`unpcklps` order, and the identification of `0x01A531A0` as `cosf` / `0x01A54570` as `sinf` — confirmed
independently of the error strings by the tiny-argument path (`sinf` returns `x·0.99999994f` below
2⁻¹³; `cosf` has no such path).

**Re-computed numerically with a float32-exact reimplementation written from the disassembly (not from
this document's C#):** all 24 **D1** Perlin vectors (24/24 bit-identical, both `Noise` and
`PerlinNoise`, including every abs-fold and wrap case) and their decimals; all **D4** seed states;
**D5**'s eight `value` bit patterns; **D6**'s full `Range(float,float)` sequence and the state after;
**D7**; **D10**'s sixteen ints, the state after, and the full-range draw; **D8**'s four
`insideUnitCircle` pairs and the state after; **D9**'s 100 000-step state and the next value. Also
re-derived the ctor sequence with the *real* call pattern (4×`Range(−10000,10000)`,
2×`Range(int.MinValue,int.MaxValue)`, 1×`Range(−10000,10000)`):
`off0=−6080 off1=4986 off2=−7704 off3=−59 riverSeed=744350289 streamSeed=952983356 off4=718` —
matches §6.1. Perlin evenness (0 violations in 200 000 pairs) and period-256 (0 violations in 8 192
pairs using exactly representable `k/64` fractions; a naive test with arbitrary floats fails, exactly as
§3.3 warns).

**Re-run against the real minimap cache, from scratch:** meta = seed `−1772362158`, version 1; buffers
16 777 216 / 16 777 216 / 8 388 608 bytes; exactly the 7 `Color32` values listed. **Test A: 38 825
Meadows + 142 032 Plains = 180 857 pixels, 0 mismatches. Test D: 217 959 Mistlands pixels, 0 byte
mismatches** (the counts match this document exactly), and the reciprocal-multiply variant P4 produces
**exactly 2** errors on Test D, as claimed. `Color`→`Color32` is round-half-to-even, not truncation
(truncation fails 976 of a 7 516-pixel sample). Of §5.1's variant table only **P1 and P4** were
re-derived; P2, P3, P5–P9 were not re-run and stand on the original run (P7's mechanism — the abs fold —
is however confirmed directly from the `andps` in the disassembly).

**Corrected in place** (each with its evidence, marked "Corrected (reviewer)" at the site):
§0 "nothing else touches native code"; §2.2's wrong cross-reference; §3.1 and §4.1's permutation-table
hashes (the 1024-byte and 2048-byte hashes were conflated and reversed); §3.2's "byte-identical" 1-D
wrapper; §3.4's claim that `Range(20f,20f)` returns exactly `20f` (**false** — 209 715 of the 8 388 608
possible draws give `0x41A00001`); §5.2's "closed by §6.2" attribution and the degenerate-int claim;
§6.1's "875 px on all four axes" (it is 875/−876, and that asymmetry is what proves the half-pixel
offset); §6.2 D6's variant-(b) value (`0x4280C4D2`, not `0x4280C4D3`); §7's `pos*0.004f` shorthand
(**must be `(pos*0.01f)*0.4f`** — 106 byte errors on Test D otherwise).

**Added:** §7's `System.Math` call-site inventory, §8 risk 7, dumper item **D12**, and §9.

**Still unverified after this review:** everything in §9, plus the original R1–R5 items, which need the
dumper. Nothing in §4's reference implementation was found to be wrong.

*— checked by an independent reviewer*
