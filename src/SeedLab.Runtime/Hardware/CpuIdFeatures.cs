using System;
using System.Collections.Generic;
using System.Runtime.Intrinsics.X86;

namespace SeedLab.Runtime.Hardware
{
    /// <summary>
    /// The processor's own feature bits, read with the CPUID instruction - a second opinion on the
    /// runtime's <c>IsSupported</c> flags that no runtime switch can change.
    ///
    /// <para><b>What it is for.</b> The per-level proofs take their "default" level from what the runtime
    /// reports in an unswitched process, and derive from it which other levels exist here. If a switch
    /// had leaked into that process (<c>COMPlus_EnableAVX512=0</c> left in a shell), the default would
    /// itself be a lowered level, the AVX-512 levels would read "this CPU lacks it", and the proof would
    /// pass without ever running the real default. Comparing the runtime's flags with these bits catches
    /// that: <see cref="ExpectedFacts"/> says what an unswitched runtime must report on this CPU.</para>
    ///
    /// <para><b>How the runtime groups them.</b> .NET 10 reports a level only when the whole group it
    /// belongs to is present, and switches groups, not single features (measured here with the knob
    /// matrix: <c>DOTNET_EnableAVX2=0</c> also clears FMA, and <c>DOTNET_EnableAVX512=0</c> clears F, BW,
    /// CD, DQ and VBMI). The groups used below are the x86-64-v3 set for AVX2 (AVX, AVX2, BMI1, BMI2,
    /// FMA, LZCNT, MOVBE), the x86-64-v4 set for AVX-512 (F, BW, CD, DQ, VL, on top of v3), and VBMI with
    /// IFMA on top of v4 (taken to be the runtime's <c>EnableAVX512v2</c> group, the Cannon Lake
    /// additions). <b>Unverified:</b> the exact membership on a CPU that has some of a group and not the rest (no
    /// Intel or AMD x64 part known to the author is one); such a CPU would fail the comparison and say
    /// which bit, rather than pass wrongly. The operating system's saved-state support (XCR0) is not
    /// read - managed code cannot execute XGETBV - so only the OSXSAVE bit stands for it; an operating
    /// system that has AVX-512 state switched off would also fail the comparison, with its reason.</para>
    /// </summary>
    public sealed class CpuIdFeatures
    {
        private CpuIdFeatures() { }

        public bool Sse2 { get; private set; }
        public bool Sse42 { get; private set; }
        public bool OsXsave { get; private set; }
        public bool Avx { get; private set; }
        public bool Fma { get; private set; }
        public bool Movbe { get; private set; }
        public bool F16c { get; private set; }
        public bool Avx2 { get; private set; }
        public bool Bmi1 { get; private set; }
        public bool Bmi2 { get; private set; }
        public bool Lzcnt { get; private set; }
        public bool Avx512F { get; private set; }
        public bool Avx512BW { get; private set; }
        public bool Avx512CD { get; private set; }
        public bool Avx512DQ { get; private set; }
        public bool Avx512VL { get; private set; }
        public bool Avx512Vbmi { get; private set; }
        public bool Avx512Ifma { get; private set; }

        /// <summary>CPUID.(7,1):EDX[19]: the CPU implements AVX10 (its version is <see cref="Avx10Version"/>).</summary>
        public bool Avx10 { get; private set; }

        /// <summary>CPUID.(0x24,0):EBX[7:0] when <see cref="Avx10"/> is set, else 0.</summary>
        public int Avx10Version { get; private set; }

        /// <summary>
        /// Reads the bits, or returns null where CPUID cannot be asked (not x86, or the runtime's
        /// intrinsics are switched off, which makes <see cref="X86Base.CpuId"/> unavailable).
        /// </summary>
        public static CpuIdFeatures? Read()
        {
            try
            {
                if (!X86Base.IsSupported) return null;
                CpuIdFeatures f = new CpuIdFeatures();
                (int maxLeaf, _, _, _) = X86Base.CpuId(0, 0);
                if (maxLeaf >= 1)
                {
                    (_, _, int c1, int d1) = X86Base.CpuId(1, 0);
                    f.Sse2 = Bit(d1, 26);
                    f.Sse42 = Bit(c1, 20);
                    f.Fma = Bit(c1, 12);
                    f.Movbe = Bit(c1, 22);
                    f.OsXsave = Bit(c1, 27);
                    f.Avx = Bit(c1, 28);
                    f.F16c = Bit(c1, 29);
                }

                if (maxLeaf >= 7)
                {
                    (int a7, int b7, int c7, _) = X86Base.CpuId(7, 0);
                    f.Bmi1 = Bit(b7, 3);
                    f.Avx2 = Bit(b7, 5);
                    f.Bmi2 = Bit(b7, 8);
                    f.Avx512F = Bit(b7, 16);
                    f.Avx512DQ = Bit(b7, 17);
                    f.Avx512Ifma = Bit(b7, 21);
                    f.Avx512CD = Bit(b7, 28);
                    f.Avx512BW = Bit(b7, 30);
                    f.Avx512VL = Bit(b7, 31);
                    f.Avx512Vbmi = Bit(c7, 1);
                    if (a7 >= 1)
                    {
                        (_, _, _, int d71) = X86Base.CpuId(7, 1);
                        f.Avx10 = Bit(d71, 19);
                    }

                    if (f.Avx10 && maxLeaf >= 0x24)
                    {
                        (_, int b24, _, _) = X86Base.CpuId(0x24, 0);
                        f.Avx10Version = b24 & 0xFF;
                    }
                }

                (int maxExt, _, _, _) = X86Base.CpuId(unchecked((int)0x80000000), 0);
                if (unchecked((uint)maxExt) >= 0x80000001u)
                {
                    (_, _, int ce, _) = X86Base.CpuId(unchecked((int)0x80000001), 0);
                    f.Lzcnt = Bit(ce, 5);
                }

                return f;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// What an unswitched .NET 10 runtime must report here, under the names of the generator's ISA
        /// facts (<c>SimdDispatch.Facts</c>): avx, fma, avx2, avx512f, avx512bw, avx512vbmi, with "1" or
        /// "0". Only the facts the per-level proofs derive their levels from.
        /// </summary>
        public IReadOnlyDictionary<string, string> ExpectedFacts()
        {
            bool avx = Avx && OsXsave;
            bool v3 = avx && Avx2 && Fma && Bmi1 && Bmi2 && Lzcnt && Movbe;
            bool v4 = v3 && Avx512F && Avx512BW && Avx512CD && Avx512DQ && Avx512VL;
            return new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["avx"] = avx ? "1" : "0",
                ["fma"] = v3 ? "1" : "0",
                ["avx2"] = v3 ? "1" : "0",
                ["avx512f"] = v4 ? "1" : "0",
                ["avx512bw"] = v4 ? "1" : "0",
                ["avx512vbmi"] = v4 && Avx512Vbmi && Avx512Ifma ? "1" : "0",
            };
        }

        /// <summary>The bits as "name=0/1" pairs, in a fixed order, for reports and JSON.</summary>
        public IEnumerable<(string Name, bool Value)> Bits()
        {
            yield return ("sse2", Sse2);
            yield return ("sse42", Sse42);
            yield return ("osxsave", OsXsave);
            yield return ("avx", Avx);
            yield return ("fma", Fma);
            yield return ("movbe", Movbe);
            yield return ("f16c", F16c);
            yield return ("avx2", Avx2);
            yield return ("bmi1", Bmi1);
            yield return ("bmi2", Bmi2);
            yield return ("lzcnt", Lzcnt);
            yield return ("avx512f", Avx512F);
            yield return ("avx512bw", Avx512BW);
            yield return ("avx512cd", Avx512CD);
            yield return ("avx512dq", Avx512DQ);
            yield return ("avx512vl", Avx512VL);
            yield return ("avx512vbmi", Avx512Vbmi);
            yield return ("avx512ifma", Avx512Ifma);
            yield return ("avx10", Avx10);
        }

        /// <summary>
        /// Every fact where <paramref name="runtimeFacts"/> (an unswitched process's
        /// <c>SimdDispatch.Facts</c>) disagrees with <see cref="ExpectedFacts"/>, as sentences. Empty
        /// when the runtime reports exactly what this CPU implies.
        /// </summary>
        public List<string> Disagreements(IReadOnlyDictionary<string, string> runtimeFacts)
        {
            List<string> r = new List<string>();
            foreach (KeyValuePair<string, string> kv in ExpectedFacts())
            {
                string have = runtimeFacts.TryGetValue(kv.Key, out string? v) ? v : "missing";
                if (have == kv.Value) continue;
                r.Add(kv.Value == "1"
                    ? "CPUID reports " + kv.Key + " (and its group) but the runtime does not - a runtime switch is set "
                      + "somewhere (DOTNET_ or COMPlus_ Enable*, PreferredVectorBitWidth, a runtimeconfig), or the operating "
                      + "system has that register state switched off"
                    : "the runtime reports " + kv.Key + "=" + have + " but CPUID does not imply it");
            }

            return r;
        }

        private static bool Bit(int reg, int bit) => ((reg >> bit) & 1) != 0;
    }
}
