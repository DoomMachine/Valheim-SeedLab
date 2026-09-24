using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
using System.Text;

namespace SeedLab.WorldGen.Simd
{
    /// <summary>A vector width the generator can run at. Ordered: a higher value is a wider path.</summary>
    public enum SimdLevel : byte
    {
        /// <summary>No vector kernel: every Perlin sample through <c>PerlinFast.NoiseScalar</c>.</summary>
        Scalar = 0,

        /// <summary>The 8-wide AVX2 kernels (<c>PerlinFast.Noise8</c>).</summary>
        Avx2 = 1,

        /// <summary>16-wide AVX-512 kernels. None is built yet; the level exists so the hardware can be named.</summary>
        Avx512 = 2,
    }

    /// <summary>What <c>--simd</c> / <c>SEEDLAB_SIMD</c> asked for. A ceiling, never a floor.</summary>
    public enum SimdRequest : byte
    {
        Auto = 0,
        Scalar = 1,
        Avx2 = 2,
        Avx512 = 3,
    }

    /// <summary>
    /// The 16-lane Perlin lookup a CPU could use (design C 3.3). No <c>Noise16</c> is built yet, so the
    /// active variant is always <see cref="None"/>; the hardware's is reported so a machine report
    /// already says which one a future kernel would take there.
    /// </summary>
    public enum Noise16Kind : byte
    {
        None = 0,

        /// <summary>AVX-512 VBMI byte permutes (<c>vpermi2b</c>): Zen 4/5, Ice Lake and later.</summary>
        Vbmi = 1,

        /// <summary>Two AVX2 gathers per lookup: AVX-512 without VBMI (Skylake-X, Cascade Lake).</summary>
        TwoGathers = 2,
    }

    /// <summary>
    /// The one place that decides which vector path the generator runs, from what the CPU and the
    /// runtime report - never from a CPU family or model name.
    ///
    /// <list type="number">
    /// <item><b>What the hardware allows</b> (<see cref="Hardware"/>) comes only from
    /// <c>IsSupported</c>, which is the JIT's own answer to "may I emit this instruction here": the CPU's
    /// CPUID bits, masked by the operating system's saved-state support and by the runtime's
    /// <c>DOTNET_Enable*</c> knobs. AVX-512 needs F and BW (or AVX10.1 at 512 bits) and AVX2 as well,
    /// because every AVX-512 variant planned also uses AVX2 gathers; AVX2 needs AVX2.</item>
    /// <item><b>What the runtime thinks is fast</b>: <c>Vector512.IsHardwareAccelerated</c> is false on
    /// CPUs where the runtime prefers 256-bit code (it is what <c>DOTNET_PreferredVectorBitWidth=256</c>
    /// produces), and then AVX-512 is not used unless it is asked for by name. It is an opinion about
    /// speed, not about correctness: every path gives the same bits.</item>
    /// <item><b>What was asked for</b> (<see cref="Requested"/>): <c>SEEDLAB_SIMD</c>, which
    /// <c>vseed --simd</c> sets as the first statement of <c>Main</c>. It is a ceiling: asking for
    /// scalar on an AVX2 machine runs scalar; asking for AVX-512 on an AVX2 machine runs AVX2.</item>
    /// <item><b>What is built</b> (<see cref="WidestKernel"/>): today the AVX2 8-wide Perlin. A 9800X3D
    /// has AVX-512, and its report must still say the bytes ran through AVX2 - naming a level no
    /// kernel exists for would be false evidence.</item>
    /// </list>
    ///
    /// <para><b>The scalar path is always present</b> and is the reference: it is what runs when no
    /// level above it is allowed, and the startup self-test compares every vector path with it bit
    /// for bit. Choosing a path can therefore change only speed, which is why the level is printed in
    /// plans, reports, the self-test stamp and <c>/api/runtime</c>, and never written into a result.</para>
    ///
    /// <para>Everything here is read once, in the static constructor, into <c>static readonly</c>
    /// fields, so the JIT folds <c>PerlinFast.Use8Wide</c> to a constant exactly as it folded
    /// <c>Avx2.IsSupported</c> before. BCL only, no I/O: the environment variables are the only input
    /// besides the ISA flags.</para>
    /// </summary>
    public static class SimdDispatch
    {
        /// <summary>The request: auto (default), scalar, avx2 or avx512. Read once per process.</summary>
        public const string EnvironmentVariable = "SEEDLAB_SIMD";

        /// <summary>
        /// A test hook for the per-level proof: comma-separated <c>name=value</c> assertions about this
        /// process's ISA state and dispatch (<c>avx512f=0,avx2=1,active=avx2</c>). When it is set, the
        /// WorldGen module initialiser refuses to load unless every one holds, so a gate run under a
        /// runtime knob proves in its own process that the knob took effect - an assertion about the
        /// effect, never about a knob's name. Unknown names fail too, so a typo cannot pass quietly.
        /// </summary>
        public const string ExpectVariable = "SEEDLAB_SIMD_EXPECT";

        /// <summary>The widest level a kernel exists for today. Raise it only with the kernel and its proof.</summary>
        public const SimdLevel WidestKernel = SimdLevel.Avx2;

        /// <summary>The runtime's ISA answers for this process, as read once at start-up.</summary>
        public static readonly IsaSnapshot Isa;

        /// <summary>The widest level the CPU and runtime allow (correctness, from <c>IsSupported</c>).</summary>
        public static readonly SimdLevel Hardware;

        /// <summary>What <see cref="EnvironmentVariable"/> asked for.</summary>
        public static readonly SimdRequest Requested;

        /// <summary>The variable's text as found ("" when unset), for reports.</summary>
        public static readonly string RequestText;

        /// <summary>The path the generator runs: min(hardware, speed opinion, request, widest kernel).</summary>
        public static readonly SimdLevel Active;

        /// <summary>The 16-lane lookup in use. Always <see cref="Noise16Kind.None"/> until one is built.</summary>
        public static readonly Noise16Kind Variant;

        /// <summary>The 16-lane lookup this hardware would take, for reports.</summary>
        public static readonly Noise16Kind HardwareVariant;

        /// <summary>Why <see cref="Active"/> is what it is, in words. Printed wherever the level is.</summary>
        public static readonly string Reason;

        static SimdDispatch()
        {
            Isa = IsaSnapshot.Read();

            SimdLevel hw = SimdLevel.Scalar;
            if (Isa.Avx2) hw = SimdLevel.Avx2;
            if (Isa.Avx2 && ((Isa.Avx512F && Isa.Avx512BW) || Isa.Avx10v1V512)) hw = SimdLevel.Avx512;
            Hardware = hw;
            HardwareVariant = hw != SimdLevel.Avx512 ? Noise16Kind.None
                            : Isa.Avx512Vbmi ? Noise16Kind.Vbmi : Noise16Kind.TwoGathers;
            Variant = Noise16Kind.None;

            string? raw = null;
            try { raw = Environment.GetEnvironmentVariable(EnvironmentVariable); }
            catch (Exception) { /* a host that forbids reading the environment gets auto */ }
            RequestText = (raw ?? "").Trim();

            List<string> why = new List<string>();
            SimdRequest req;
            if (!TryParseRequest(RequestText, out req))
            {
                // Never throw from a type initialiser over a setting: the CLI validates --simd before it
                // sets the variable, so only a hand-set variable can be wrong, and auto is safe.
                why.Add(EnvironmentVariable + "=\"" + RequestText + "\" is not auto, scalar, avx2 or avx512 - treated as auto");
                req = SimdRequest.Auto;
            }

            Requested = req;

            SimdLevel level = hw;
            why.Add(Describe(hw, Isa));

            if (level == SimdLevel.Avx512 && !Isa.Vector512Accelerated && req != SimdRequest.Avx512)
            {
                level = SimdLevel.Avx2;
                why.Add("the runtime does not accelerate 512-bit vectors here (Vector512.IsHardwareAccelerated is false), "
                        + "so AVX-512 is used only if asked for with --simd avx512");
            }

            if (req != SimdRequest.Auto)
            {
                SimdLevel asked = ToLevel(req);
                if (asked < level)
                {
                    level = asked;
                    why.Add(EnvironmentVariable + "=" + Name(req) + " caps it at " + Name(asked));
                }
                else if (asked > hw)
                {
                    why.Add(EnvironmentVariable + "=" + Name(req) + " asks for more than this CPU and runtime allow");
                }
            }

            if (level > WidestKernel)
            {
                level = WidestKernel;
                why.Add("no " + Name(SimdLevel.Avx512) + " kernel is built yet; the widest is " + Name(WidestKernel));
            }

            Active = level;
            Reason = "active path " + Name(level) + ": " + string.Join("; ", why);
        }

        /// <summary>
        /// The dispatch as one token list for the self-test stamp and reports:
        /// <c>simd=avx2/none hw=avx512/vbmi req=auto</c>. A different request, knob or CPU gives a
        /// different key, so a stamp earned on one path is never trusted on another.
        /// </summary>
        public static string Key =>
            "simd=" + Name(Active) + "/" + Name(Variant)
            + " hw=" + Name(Hardware) + "/" + Name(HardwareVariant)
            + " req=" + Name(Requested);

        /// <summary>One line for plans and machine blocks: "avx2 (hardware avx512/vbmi, request auto)".</summary>
        public static string Summary =>
            Name(Active) + " (hardware " + Name(Hardware)
            + (HardwareVariant != Noise16Kind.None ? "/" + Name(HardwareVariant) : "")
            + ", request " + Name(Requested) + ")";

        public static string Name(SimdLevel l) => l switch
        {
            SimdLevel.Scalar => "scalar",
            SimdLevel.Avx2 => "avx2",
            SimdLevel.Avx512 => "avx512",
            _ => "level" + ((int)l).ToString(CultureInfo.InvariantCulture),
        };

        public static string Name(SimdRequest r) => r switch
        {
            SimdRequest.Auto => "auto",
            SimdRequest.Scalar => "scalar",
            SimdRequest.Avx2 => "avx2",
            SimdRequest.Avx512 => "avx512",
            _ => "request" + ((int)r).ToString(CultureInfo.InvariantCulture),
        };

        public static string Name(Noise16Kind k) => k switch
        {
            Noise16Kind.None => "none",
            Noise16Kind.Vbmi => "vbmi",
            Noise16Kind.TwoGathers => "two-gathers",
            _ => "variant" + ((int)k).ToString(CultureInfo.InvariantCulture),
        };

        /// <summary>
        /// Parses a request: empty or "auto" is auto; otherwise scalar, avx2 or avx512, any case. The
        /// CLI calls this before it sets the variable, so a bad <c>--simd</c> is an error line, not a
        /// silent auto.
        /// </summary>
        public static bool TryParseRequest(string? text, out SimdRequest request)
        {
            switch ((text ?? "").Trim().ToLowerInvariant())
            {
                case "":
                case "auto":
                    request = SimdRequest.Auto;
                    return true;
                case "scalar":
                    request = SimdRequest.Scalar;
                    return true;
                case "avx2":
                    request = SimdRequest.Avx2;
                    return true;
                case "avx512":
                    request = SimdRequest.Avx512;
                    return true;
                default:
                    request = SimdRequest.Auto;
                    return false;
            }
        }

        private static SimdLevel ToLevel(SimdRequest r) => r switch
        {
            SimdRequest.Scalar => SimdLevel.Scalar,
            SimdRequest.Avx2 => SimdLevel.Avx2,
            _ => SimdLevel.Avx512,
        };

        private static string Describe(SimdLevel hw, IsaSnapshot isa)
        {
            if (hw == SimdLevel.Avx512)
            {
                return "the CPU and runtime allow AVX-512 (F+BW" + (isa.Avx512Vbmi ? ", VBMI" : ", no VBMI")
                       + (isa.Vector512Accelerated ? ", 512-bit vectors accelerated)" : ")");
            }

            if (hw == SimdLevel.Avx2) return "the CPU and runtime allow AVX2, not AVX-512";
            return isa.X86Base
                ? "the runtime reports no AVX2 here, so every sample takes the scalar path"
                : "the runtime reports no x86 intrinsics here, so every sample takes the scalar path";
        }

        // ---- the per-level proof's in-process assertion ---------------------------------------------

        /// <summary>
        /// Checks <see cref="ExpectVariable"/> against this process. Returns null when it is unset or
        /// every assertion holds; otherwise the sentence to fail with. Pure: the caller decides to throw.
        /// </summary>
        public static string? CheckExpectation()
        {
            string? text = null;
            try { text = Environment.GetEnvironmentVariable(ExpectVariable); }
            catch (Exception) { }
            return CheckExpectation(text);
        }

        /// <summary>The same check against a given assertion list, for tests.</summary>
        public static string? CheckExpectation(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            Dictionary<string, string> actual = Facts();
            List<string> wrong = new List<string>();
            foreach (string part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                int eq = part.IndexOf('=');
                if (eq <= 0)
                {
                    wrong.Add("'" + part + "' is not name=value");
                    continue;
                }

                string name = part.Substring(0, eq).Trim().ToLowerInvariant();
                string want = part.Substring(eq + 1).Trim().ToLowerInvariant();
                if (!actual.TryGetValue(name, out string? have))
                {
                    wrong.Add("'" + name + "' is not a fact this process knows (" + string.Join(", ", actual.Keys) + ")");
                    continue;
                }

                if (!string.Equals(have, want, StringComparison.Ordinal)) wrong.Add(name + " is " + have + ", expected " + want);
            }

            if (wrong.Count == 0) return null;
            return "SeedLab: " + ExpectVariable + "=\"" + text + "\" does not hold in this process - the runtime knob or "
                   + "request it was set for had no effect, or a different one: " + string.Join("; ", wrong)
                   + ". This run cannot count as a proof at that level.";
        }

        /// <summary>
        /// Every fact <see cref="ExpectVariable"/> can assert, as lower-case name to "0"/"1" or a level
        /// name. Also what <c>vseed selftest --isa-json</c> prints, so the two can never disagree.
        /// </summary>
        public static Dictionary<string, string> Facts()
        {
            Dictionary<string, string> d = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach ((string name, bool value) in Isa.Flags()) d[name] = value ? "1" : "0";
            d["vector_bytes"] = Isa.VectorByteWidth.ToString(CultureInfo.InvariantCulture);
            d["hardware"] = Name(Hardware);
            d["hardware_variant"] = Name(HardwareVariant);
            d["requested"] = Name(Requested);
            d["active"] = Name(Active);
            d["variant"] = Name(Variant);
            return d;
        }

        /// <summary>The facts as one line, "x86base=1 sse2=1 ... active=avx2", for logs and reports.</summary>
        public static string FactsLine()
        {
            StringBuilder sb = new StringBuilder();
            foreach (KeyValuePair<string, string> kv in Facts())
            {
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(kv.Key).Append('=').Append(kv.Value);
            }

            return sb.ToString();
        }
    }

    /// <summary>
    /// The runtime's <c>IsSupported</c> and <c>IsHardwareAccelerated</c> answers, read once. Each read is
    /// guarded: an ISA class that cannot be asked simply reads as absent, never as a crash.
    /// </summary>
    public sealed class IsaSnapshot
    {
        public bool X86Base { get; private set; }
        public bool Sse { get; private set; }
        public bool Sse2 { get; private set; }
        public bool Sse41 { get; private set; }
        public bool Sse42 { get; private set; }
        public bool Avx { get; private set; }
        public bool Avx2 { get; private set; }
        public bool Fma { get; private set; }
        public bool Avx512F { get; private set; }
        public bool Avx512BW { get; private set; }
        public bool Avx512CD { get; private set; }
        public bool Avx512DQ { get; private set; }
        public bool Avx512Vbmi { get; private set; }
        public bool Avx10v1 { get; private set; }
        public bool Avx10v1V512 { get; private set; }

        /// <summary>
        /// AVX10.2 (Nova Lake, Diamond Rapids and later). No kernel uses it; it is read because the JIT may
        /// compile ordinary code with its instructions (the saturating float-to-integer conversions, for
        /// one), so a report and the self-test stamp must be able to say it was there.
        /// </summary>
        public bool Avx10v2 { get; private set; }
        public bool Avx10v2V512 { get; private set; }
        public bool AdvSimd { get; private set; }
        public bool Vector128Accelerated { get; private set; }
        public bool Vector256Accelerated { get; private set; }
        public bool Vector512Accelerated { get; private set; }
        public int VectorByteWidth { get; private set; }

        public static IsaSnapshot Read()
        {
            IsaSnapshot s = new IsaSnapshot();
            s.X86Base = Ask(() => System.Runtime.Intrinsics.X86.X86Base.IsSupported);
            s.Sse = Ask(() => System.Runtime.Intrinsics.X86.Sse.IsSupported);
            s.Sse2 = Ask(() => System.Runtime.Intrinsics.X86.Sse2.IsSupported);
            s.Sse41 = Ask(() => System.Runtime.Intrinsics.X86.Sse41.IsSupported);
            s.Sse42 = Ask(() => System.Runtime.Intrinsics.X86.Sse42.IsSupported);
            s.Avx = Ask(() => System.Runtime.Intrinsics.X86.Avx.IsSupported);
            s.Avx2 = Ask(() => System.Runtime.Intrinsics.X86.Avx2.IsSupported);
            s.Fma = Ask(() => System.Runtime.Intrinsics.X86.Fma.IsSupported);
            s.Avx512F = Ask(() => System.Runtime.Intrinsics.X86.Avx512F.IsSupported);
            s.Avx512BW = Ask(() => System.Runtime.Intrinsics.X86.Avx512BW.IsSupported);
            s.Avx512CD = Ask(() => System.Runtime.Intrinsics.X86.Avx512CD.IsSupported);
            s.Avx512DQ = Ask(() => System.Runtime.Intrinsics.X86.Avx512DQ.IsSupported);
            s.Avx512Vbmi = Ask(() => System.Runtime.Intrinsics.X86.Avx512Vbmi.IsSupported);
            s.Avx10v1 = Ask(() => System.Runtime.Intrinsics.X86.Avx10v1.IsSupported);
            s.Avx10v1V512 = Ask(() => System.Runtime.Intrinsics.X86.Avx10v1.V512.IsSupported);
            s.Avx10v2 = Ask(() => System.Runtime.Intrinsics.X86.Avx10v2.IsSupported);
            s.Avx10v2V512 = Ask(() => System.Runtime.Intrinsics.X86.Avx10v2.V512.IsSupported);
            s.AdvSimd = Ask(() => System.Runtime.Intrinsics.Arm.AdvSimd.IsSupported);
            s.Vector128Accelerated = Ask(() => Vector128.IsHardwareAccelerated);
            s.Vector256Accelerated = Ask(() => Vector256.IsHardwareAccelerated);
            s.Vector512Accelerated = Ask(() => Vector512.IsHardwareAccelerated);
            try { s.VectorByteWidth = System.Numerics.Vector<byte>.Count; }
            catch (Exception) { s.VectorByteWidth = 0; }
            return s;
        }

        /// <summary>The flags in a fixed order under their lower-case names.</summary>
        public IEnumerable<(string Name, bool Value)> Flags()
        {
            yield return ("x86base", X86Base);
            yield return ("sse", Sse);
            yield return ("sse2", Sse2);
            yield return ("sse41", Sse41);
            yield return ("sse42", Sse42);
            yield return ("avx", Avx);
            yield return ("avx2", Avx2);
            yield return ("fma", Fma);
            yield return ("avx512f", Avx512F);
            yield return ("avx512bw", Avx512BW);
            yield return ("avx512cd", Avx512CD);
            yield return ("avx512dq", Avx512DQ);
            yield return ("avx512vbmi", Avx512Vbmi);
            yield return ("avx10v1", Avx10v1);
            yield return ("avx10v1_v512", Avx10v1V512);
            yield return ("avx10v2", Avx10v2);
            yield return ("avx10v2_v512", Avx10v2V512);
            yield return ("advsimd", AdvSimd);
            yield return ("v128acc", Vector128Accelerated);
            yield return ("v256acc", Vector256Accelerated);
            yield return ("v512acc", Vector512Accelerated);
        }

        private static bool Ask(Func<bool> f)
        {
            try { return f(); }
            catch (Exception) { return false; }
        }
    }
}
