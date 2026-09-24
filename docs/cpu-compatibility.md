# CPUs, vector paths and what is proved where

SeedLab reproduces Valheim's world generation bit for bit, and it is meant to do that on any x64 PC a
player owns - an Intel or AMD processor from 2015 or later. This page says which code path SeedLab takes
on which processor, how that choice is made, what has been proved and on what, and what has not.

The short version:

- **Every path gives the same bits.** The choice of vector path changes speed only. A path that
  disagreed with the reference on any machine would stop SeedLab at start-up rather than build a
  different world.
- **The choice is made from what the CPU and the .NET runtime report**, never from a CPU name or
  family. Today there are two paths: the 8-wide AVX2 Perlin noise and the scalar one. AVX-512 is
  detected and reported but has no kernel of its own yet, so an AVX-512 CPU runs the AVX2 path.
- **The machine self-test is re-run whenever the CPU, the path, a runtime switch or the C runtime
  library changes**, and fails closed.
- **The one known class of CPU that SeedLab refuses today** is a CPU without FMA3 (older Pentium,
  Celeron and Atom parts, AMD Jaguar/Puma). The reason is the Windows C runtime's maths, not the
  vector code; see [Correctness traps](#correctness-traps).
- **`vseed selftest --report`** prints everything below for the machine it runs on, compared with the
  reference machine. It is what to run, and send, from a CPU nobody has tested.

## How the path is chosen

`SeedLab.WorldGen.Simd.SimdDispatch` decides once per process, when the generator is first loaded,
and every later Perlin sample takes the path it chose. Four steps, in order:

1. **What the hardware allows** comes only from the runtime's `IsSupported` flags - the CPU's CPUID
   bits, masked by what the operating system saves across a context switch and by the runtime's own
   switches. AVX-512 needs AVX-512 F and BW (or AVX10.1 at 512 bits) and AVX2 as well; AVX2 needs AVX2.
   Anything less is scalar.
2. **What the runtime thinks is fast.** If `Vector512.IsHardwareAccelerated` is false - which .NET
   reports on CPUs where it prefers 256-bit code, and which `DOTNET_PreferredVectorBitWidth=256`
   produces - AVX-512 is not used unless it is asked for by name.
3. **What was asked for**: `--simd auto|scalar|avx2|avx512` (or the `SEEDLAB_SIMD` variable). It is a
   ceiling: `--simd scalar` on an AVX2 machine runs scalar; `--simd avx512` on an AVX2 machine runs AVX2.
   An unknown value on the command line is an error (exit 2); an unknown value in the variable is
   treated as `auto` and said so in the reason.
4. **What is built.** The widest kernel today is AVX2, so the level stops there.

The scalar path is always present. It is the reference the start-up self-test compares every vector
path with, and it is what runs when nothing above it is allowed.

**Where to see it.** The machine block (`vseed search`'s, the session log's header, and the
`hardware` lines the web page reads from `/api/runtime`) has a `simd path` line, for example
`avx2 (hardware avx512/vbmi, request auto)`. `vseed selftest` names it in its `D1` row, `vseed profile`
in its machine block, and `vseed selftest --isa-json` prints the full state as JSON. It is never
written into a search result or a checkpoint, because results are identical on every path.

**The self-test stamp** is filed under the ISA flags (`sse2 avx avx2 avx512f avx512bw avx512vbmi fma
v32 v512acc`), the processor (`cpu=<vendor>/<family>/<model>/<stepping>` from CPUID), the version of
the C runtime's `ucrtbase.dll` and the path (`simd=avx2/none hw=avx512/vbmi req=auto`). A runtime
switch, a disk moved to another PC, a Windows update that replaces `ucrtbase.dll` or a different
`--simd` each re-run the self-test rather than trusting a pass earned under other conditions.

## What the self-test checks at start-up

| suite | what | fails when |
|---|---|---|
| Perlin (module initialiser) | the byte-table scalar and the active vector path against the literal transcription of Unity's `Mathf.PerlinNoise`, every case rotated through all 8 lanes | any bit differs; the process refuses to load the generator |
| `numerics` | 271 recorded `Math.*` results and float evaluations | any bit differs |
| `libm-dense` | `Math.Atan2/Sin/Cos/Pow` over 65,536 arguments at each of the generator's five call-site shapes (world angle, river curve, float sine/cosine, the Mistlands' power, the Ashlands' powers), as two SHA-256 digests per site: the raw doubles and the values as the generator consumes them | either digest differs from the reference machine's |
| `denormals` | subnormal float and double results, on the calling thread and a new one | a thread flushes subnormals to zero (FTZ/DAZ set by native code in the process) |
| `seedlab/natives` | the 263,780 values the game itself recorded (Perlin, `Random`, libm, hashes) | any differs; registered only when `groundtruth\natives` is present |

`vseed selftest --simd-all` proves every path the hardware has, not only the active one.

## Proof per level on the reference machine

The reference machine is the one whose output the gates compare against the game itself: an AMD
Ryzen 7 9800X3D (Zen 5; AVX-512 with VBMI, 512-bit vectors accelerated by .NET), Windows 10 22H2
(10.0.19045), .NET 10.0.12, `ucrtbase.dll` 10.0.19041.3636. Every level below is reached on that
one CPU by switching the runtime's own ISA support off, and each process asserts that the switch took
effect (`SEEDLAB_SIMD_EXPECT`, or the knob matrix's check of its `--isa-json`) - a switch that did
nothing fails as "knob had no effect", never passes quietly.

What each switch does in .NET 10.0.12, measured 2026-09-24 (knob matrix,
`dotnet run -c Release --project tests\SeedLab.Runtime.Tests -- --knob-matrix`):

| level | forced by | ISA state in the process | path |
|---|---|---|---|
| K0 | nothing | everything through AVX-512 F/BW/CD/DQ/VBMI; `Vector512` accelerated | avx2 (hardware avx512/vbmi) |
| K2 | `SEEDLAB_SIMD=avx2` | as K0 | avx2 |
| K9 | `SEEDLAB_SIMD=avx512` | as K0 | avx2 (no AVX-512 kernel is built) |
| K3 | `DOTNET_EnableAVX512=0` | AVX-512 F, BW, CD, DQ and VBMI off, `Vector512` not accelerated; AVX2 and FMA on | avx2 |
| K4 | `DOTNET_EnableAVX512v2=0` | VBMI off only; AVX-512 F/BW kept - the Skylake-X shape | avx2 (hardware avx512/two-gathers) |
| K5 | `DOTNET_PreferredVectorBitWidth=256` | every ISA on, `Vector512` not accelerated - the state .NET is reported to choose on throttling CPUs | avx2 |
| K6 | `DOTNET_EnableAVX2=0` | AVX2, FMA and all of AVX-512 off; AVX (1) and SSE4.2 on; `Vector256` not accelerated | scalar |
| K7 | `DOTNET_EnableHWIntrinsic=0` | every intrinsic off, including `X86Base` and SSE2; no vector type accelerated | scalar |
| K8 | `SEEDLAB_SIMD=scalar` | as K0 | scalar |

There is no `DOTNET_EnableAVX512F` in .NET 10.0.12: the switch is `DOTNET_EnableAVX512`. That is why the
matrix asserts effects and never trusts a switch's name.

At K0, K3, K6, K7 and K8 - every distinct path and every distinct way the JIT compiles the generator -
the following were run on 2026-09-24 and gave the same results as at K0 (`tests\level-matrix.ps1` for
the gates, `SeedLab.Acceptance.Tests -- --level-matrix` for the fingerprints):

- the world fingerprints of the 64 reference seeds, all five layers (the lattice, pre-generation with
  the river cache, the 2048 x 2048 point grid, all 183 placements, the location oracle), equal to the
  recording made before any of this work;
- the acceptance suite (32 checks against the game's own map caches), the location gate (12,228
  instances), the natives goldens (11 checks), GoldenCheck (the generator's private state against the
  game's) and `vseed selftest`.

K3 and K7 test more than the Perlin kernel: the JIT compiles ordinary scalar code differently when
AVX-512 or every intrinsic is off (other instruction encodings, other float-to-integer sequences), and
the worlds are the same.

## CPUs from 2015 to 2026 and later

What SeedLab does on each family **by what the runtime reports**. The ISA facts come from vendor
documentation and public instruction tables and were not verified on these CPUs - each row is
**Unverified:** unless it names this machine. The dispatch does not depend on them: it asks the CPU.

| family (years) | AVX2 / FMA3 | AVX-512 | path today | known traps |
|---|---|---|---|---|
| Intel Broadwell, Skylake, Kaby/Coffee/Comet Lake Core i3-i9 (2015-2020); Xeon E3 v5/v6, E-2100/2200, W-1200 | yes / yes | no | avx2 | the 2023 "Downfall" (GDS) microcode slows gathers, so the AVX2 path may be slower than scalar there; `--simd scalar` is the override |
| Pentium Gold / Celeron of those generations (G4400, G5400, G6400, 4415U, 5405U...) | no / no | no | scalar | **no FMA3: the self-test fails closed today** (below) |
| Intel Atom-class: Cherry Trail, Apollo Lake, Gemini Lake, Jasper/Elkhart Lake (Celeron N/J, Pentium N/Silver; 2015-2021) | no / no | no | scalar | no FMA3: fails closed today |
| Intel Alder Lake-N and Twin Lake (N100, N200, N305, N150, N250; 2023-2025) | yes / yes | no | avx2 | 256-bit operations on 128-bit units; gather speed unknown |
| Intel Ice Lake, Tiger Lake, Rocket Lake (2019-2021); Xeon E-2300, W-1300 | yes / yes | yes, with VBMI | avx2 (a future AVX-512 kernel would take its VBMI variant) | GDS microcode on gathers |
| Intel Alder Lake, Raptor Lake and its refresh (12th-14th gen, 2021-2023), Core 3/5/7 re-brands | yes / yes | fused off | avx2 | **hybrid P/E cores**: work is claimed block by block, so a slower core claims fewer blocks; 13th/14th-gen Vmin degradation can cause silent wrong results, which only the self-test and the fingerprints can catch - re-run `vseed selftest --report` after a BIOS or microcode update |
| Intel Meteor Lake, Lunar Lake, Arrow Lake, Panther Lake (2023-2026) | yes / yes | no | avx2 | hybrid; Lunar and Arrow Lake have no SMT, so "half the logical cores" is half the physical cores there |
| Intel Nova Lake (announced for 2026) | yes / yes | reportedly AVX10.2 including 512-bit | avx2 until an AVX-512 kernel exists | the dispatch accepts AVX10.1 at 512 bits as AVX-512-capable |
| Intel Skylake-X/SP, Cascade Lake, Cooper Lake (Core X 7xxx-10xxx, Xeon W-21xx/22xx/32xx; 2017-2020) | yes / yes | F/BW/CD/DQ/VL, **no VBMI** | avx2 (a future AVX-512 kernel would take its two-gathers variant) | AVX-512 licence down-clocking; .NET is reported to leave `Vector512` unaccelerated there, which K5 reproduces; GDS on gathers |
| Intel Ice Lake-SP, Sapphire/Emerald/Granite Rapids (Xeon W-3300/2400/3400/2500/3500, Xeon 6 P; 2021-2025) | yes / yes | yes, with VBMI | avx2 | mild licence effects |
| Intel Xeon E-2400, Xeon 6 E-core (2023-2024) | yes / yes | no | avx2 | - |
| AMD Excavator: Carrizo, Bristol Ridge, Stoney Ridge; Athlon X4 845/950 (2015-2016) | yes / yes | no | avx2 | 256-bit operations on 128-bit units; slow gathers |
| AMD Steamroller "Godavari" (A10-7870K and kin, 2015) | **no AVX2** / yes | no | scalar | FMA3 without AVX2: which maths path the Windows C runtime picks there is not known |
| AMD Jaguar/Puma (Beema, Mullins, Carrizo-L, AM1 Athlon 5x50; 2014-2015) | no / no | no | scalar | no FMA3: fails closed today |
| AMD Zen 1 / Zen+ (Ryzen 1000/2000, Athlon 200GE/3000G, Threadripper 1000/2000; 2017-2019) | yes / yes | no | avx2 | 128-bit datapath and microcoded gathers: the AVX2 path may be slower than scalar; `--simd scalar` |
| AMD Zen 2 (Ryzen 3000, 4000/5000U, 7x20; Threadripper 3000; 2019-2022) | yes / yes | no | avx2 | gathers slower than Intel's |
| AMD Zen 3 / 3+ (Ryzen 5000, 6000 mobile; Threadripper PRO 5000WX; 2020-2022) | yes / yes | no | avx2 | - |
| AMD Zen 4 (Ryzen 7000/8000G, 7040/8040 mobile; Threadripper 7000; 2022-2024) | yes / yes | yes, with VBMI, executed as 2 x 256 bits | avx2 | a 512-bit kernel's gain would be smaller than on Zen 5 desktop |
| **AMD Zen 5 desktop and server** (Ryzen 9000 including the 9800X3D - the reference machine; Threadripper 9000; EPYC 9005; 2024-2025) | yes / yes | yes, with VBMI, full 512-bit datapath (verified here: .NET accelerates `Vector512`) | avx2 | dual-CCD X3D parts have asymmetric caches |
| AMD Zen 5 mobile (Ryzen AI 300 "Strix Point", Ryzen AI Max "Strix Halo"; 2024-2025) | yes / yes | yes; Strix Point executes 512-bit as 2 x 256 | avx2 | Zen 5 and Zen 5c cores mixed: same ISA, different clocks |
| AMD Zen 6 (expected 2026 and later) | unknown | unknown | whatever the runtime reports | - |

Out of scope, though the dispatch still runs there because it asks rather than assumes: Hygon,
Zhaoxin/VIA, 32-bit x86, arm64 (including Windows-on-Arm's x64 emulation, where the self-test is the
only safeguard; see [limits](limits.md)).

## Performance traps (a proved path can be slow, never wrong)

- **Gather cliffs.** The AVX2 path does 10 gathers per 8 samples. Zen 1/Zen+/Zen 2 and Excavator
  gather slowly, and Intel's 2023 GDS mitigation slows gathers from Skylake to Tiger Lake/Rocket Lake.
  There, `--simd scalar` may be faster. No automatic timing chooses between the paths yet
  (**Unverified:** whether any of these CPUs is actually faster on scalar).
- **AVX-512 licence down-clocking** on Skylake-X/Cascade Lake. Irrelevant until an AVX-512 kernel
  exists; the dispatch then uses 512-bit code only where .NET accelerates it.
- **Hybrid P/E cores** (Intel 12th gen and later; AMD Zen 5 + Zen 5c). The ISA is identical on every
  core, so one process-wide choice is correct; work is claimed in blocks, so slow cores simply do less.
  A one-thread estimate lands on a random core type.
- **AVX-SSE transitions** around C runtime calls on older Intel cores (**Unverified:** whether they
  cost anything measurable here).

## Correctness traps

- **CPUs without FMA3 fail the self-test today, by design.** `Math.Sin/Cos/Atan2/Pow` in .NET on
  Windows are the C runtime's (`ucrtbase.dll`), which uses an FMA3 implementation on CPUs that have
  FMA3 and a different one on CPUs that do not. The game's recorded values match the FMA3 path; with
  it switched off, `Cos(1.0)` and `Cos(20.0)` come out one bit different, which the `numerics` suite
  catches. So SeedLab refuses to run on such a CPU (unless `--skip-self-test`, which then says so on
  every result). Whether the game itself computes those same different values on such a CPU is not
  known (**Unverified:** the game's C runtime is statically linked, and whether it switches paths the
  same way needs a run of the dumper's natives mode on a non-FMA3 machine). Until that is known, the
  refusal stays.
- **The C runtime can change under SeedLab.** A Windows update can replace `ucrtbase.dll`, and Windows
  11 ships a newer one than the reference machine's. Its version is in the stamp, so the self-test
  re-runs, and `libm-dense` sweeps the generator's argument ranges densely, so a library that differs
  anywhere a world would feel it fails closed (**Unverified:** whether Windows 11's differs at all).
- **Flush-to-zero** set in a thread by native code loaded into the process: the `denormals` suite
  checks the calling thread and a new one. The per-worker check exists
  (`DenormalProbe.CheckCurrentThread`) but is not yet called by the search workers.
- **No runtime switch simulates another CPU's C runtime.** K6 and K7 switch the runtime's vector code
  off; `Math.Sin` still goes to the same `ucrtbase.dll` on the same CPU. The per-level proof above is a
  proof of SeedLab's and the JIT's code paths, not of another machine's maths library.

## What was tested, and what needs other hardware

| question | tested? | how |
|---|---|---|
| scalar and AVX2 paths give the game's bits | **yes, here** | K0-K9 above; fingerprints and the five gates at K0, K3, K6, K7, K8 |
| the JIT's code with AVX-512 off, AVX2 off, all intrinsics off | **yes, here** | K3, K6, K7 |
| the runtime state of a throttling Intel part (AVX-512 present, 512-bit not accelerated) | **yes, here** | K5 (ISA state only; the path is avx2 either way) |
| the ISA shape of Skylake-X/Cascade Lake (F/BW, no VBMI) | **yes, here** | K4 (ISA state only) |
| a CPU without AVX2 (Celeron, Atom, Jaguar) | code path **yes** (K6/K7), speed no | - |
| a CPU without FMA3, whose C runtime computes differently | **no** | needs such a machine; a test-only study through the C runtime's own FMA3 switch is designed, not built |
| real gather costs, licence clocks, Zen 4's 2 x 256 execution, hybrid scheduling | no | machine reports from those CPUs |
| Windows 11's `ucrtbase.dll` | no | `libm-dense` in a machine report from Windows 11 |
| Linux, macOS, arm64 | no | see [limits](limits.md) |
| any CPU under Intel SDE's emulation | no | approved, not yet run |

## For testers on other CPUs

```powershell
vseed selftest --report          # text to send; hardware and version numbers only
vseed selftest --report --json   # the same with every digest in full
```

It needs neither `groundtruth\` nor `data\` (a clone of the public repository has neither): the
terrain fingerprints need only the seed, and the location layers say they were not computed. It
contains no machine name, user name or path. Nothing is sent anywhere by SeedLab; the person running
it decides whether to send the text.
