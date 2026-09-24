using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using SeedLab.Runtime.Hardware;
using SeedLab.Runtime.SelfTest;
using SeedLab.Runtime.Storage;

namespace SeedLab.RuntimeTests
{
    /// <summary>
    /// The processor's identity, the C runtime's version and the vector path in the self-test stamp;
    /// the dense libm sweep and the subnormal probe among the built-in suites.
    /// </summary>
    public static class CpuChecks
    {
        public static void Run(Action<bool, string, string> check)
        {
            // ---- CPUID leaf 1 decoded as both vendors define it ------------------------------------------
            (int f, int m, int st) = CpuIdentity.Decode(0x00B40F40);
            check(f == 26 && m == 68 && st == 0, "a Zen 5 desktop's leaf 1 decodes to family 26 model 68 (extended family added)",
                  f + "/" + m + "/" + st);
            (f, m, st) = CpuIdentity.Decode(0x000506E3);
            check(f == 6 && m == 94 && st == 3, "a Skylake's decodes to family 6 model 94 (extended model prefixed for family 6)",
                  f + "/" + m + "/" + st);
            (f, m, st) = CpuIdentity.Decode(0x00000F29);
            check(f == 15 && m == 2 && st == 9, "a family-15 part adds a zero extended family", f + "/" + m + "/" + st);
            (f, m, st) = CpuIdentity.Decode(0x00000623);
            check(f == 6 && m == 2 && st == 3, "a family below 15 with no extended bits is left alone", f + "/" + m + "/" + st);

            CpuIdentity? env = CpuIdentity.FromProcessorIdentifier("AMD64 Family 26 Model 68 Stepping 0, AuthenticAMD");
            check(env != null && env.Vendor == "AuthenticAMD" && env.Family == 26 && env.Model == 68 && env.Stepping == 0
                  && env.Key == "cpu=AuthenticAMD/26/68/0",
                  "Windows' PROCESSOR_IDENTIFIER is read the same way (the fallback when CPUID cannot be asked)", env?.Key ?? "null");
            check(CpuIdentity.FromProcessorIdentifier("") == null && CpuIdentity.FromProcessorIdentifier("x86") == null,
                  "an empty or shapeless identifier is no identity, not a guess", "");

            // ---- this machine ----------------------------------------------------------------------------
            CpuIdentity cpu = CpuIdentity.Probe();
            check(cpu.Key.StartsWith("cpu=", StringComparison.Ordinal) && cpu.Vendor.Length > 0,
                  "the probe names this processor", cpu.Describe());
            string? pid = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER");
            CpuIdentity? fromEnv = CpuIdentity.FromProcessorIdentifier(pid);
            if (cpu.Source == "cpuid" && fromEnv != null)
            {
                check(cpu.Family == fromEnv.Family && cpu.Model == fromEnv.Model && cpu.Stepping == fromEnv.Stepping
                      && cpu.Vendor == fromEnv.Vendor,
                      "CPUID's decoding agrees with Windows' own on this machine", cpu.Key + " vs " + fromEnv.Key);
            }
            else
            {
                check(true, "CPUID vs PROCESSOR_IDENTIFIER cross-check not applicable here", "source " + cpu.Source);
            }

            string brand = cpu.Brand;
            bool printable = true;
            foreach (char c in brand) if (c < 0x20 || c > 0x7E) printable = false;
            check(printable && brand == brand.Trim() && !brand.Contains("  ", StringComparison.Ordinal),
                  "the brand string is printable ASCII, trimmed and single-spaced", "\"" + brand + "\"");

            string ucrt = CpuIdentity.UcrtVersion();
            check(OperatingSystem.IsWindows() ? ucrt.Length > 0 && char.IsDigit(ucrt[0]) && ucrt.Split('.').Length == 4 : ucrt == "n/a",
                  "the C runtime's version is read (Windows) or said to be not applicable", ucrt);

            // ---- the stamp key ---------------------------------------------------------------------------
            HardwareProbe.ReportDispatch(null, null);
            HardwareInfo none = HardwareProbe.Probe();
            check(none.StampKey.Contains(" " + none.Cpu.Key + " ", StringComparison.Ordinal)
                  && none.StampKey.Contains("ucrt=" + none.UcrtVersion, StringComparison.Ordinal)
                  && none.StampKey.EndsWith("simd=unreported", StringComparison.Ordinal),
                  "the stamp key carries the ISA flags, the processor and the C runtime, and says when no host reported a path",
                  none.StampKey);

            string tempRoot = Path.Combine(Path.GetTempPath(), "seedlab-cpu-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                CacheRoot cache = CacheRoot.Open(new CacheRootOptions { Override = tempRoot });
                HardwareProbe.ReportDispatch("simd=avx2/none hw=avx512/vbmi req=auto", "avx2 (hardware avx512/vbmi, request auto)");
                HardwareInfo avx2 = HardwareProbe.Probe();
                HardwareProbe.ReportDispatch("simd=scalar/none hw=avx512/vbmi req=scalar", "scalar (hardware avx512/vbmi, request scalar)");
                HardwareInfo scalar = HardwareProbe.Probe();
                HardwareProbe.ReportDispatch(null, null);

                MachineSelfTest t = new MachineSelfTest(cache);
                check(t.Fingerprint(avx2) != t.Fingerprint(scalar) && t.Fingerprint(avx2) != t.Fingerprint(none),
                      "a different vector path is a different stamp, so a pass earned on one never vouches for another",
                      t.Fingerprint(avx2) + " / " + t.Fingerprint(scalar) + " / " + t.Fingerprint(none));
                check(avx2.Lines().Count == none.Lines().Count + 1
                      && avx2.Lines()[avx2.Lines().Count - 1].Contains("avx2 (hardware", StringComparison.Ordinal),
                      "the machine block names the path when the host reported one", avx2.Lines()[avx2.Lines().Count - 1]);

                SelfTestOutcome o = t.Verify(avx2, force: true);
                string stamp = Path.Combine(cache.SelfTest, "passed-" + o.Fingerprint + ".txt");
                string text = File.Exists(stamp) ? File.ReadAllText(stamp) : "";
                check(o.Status == SelfTestStatus.Passed && text.Contains("cpu=" + avx2.Cpu.Key.Substring(4), StringComparison.Ordinal)
                      && text.Contains("ucrt=", StringComparison.Ordinal) && text.Contains("\nsimd=avx2/none hw=avx512/vbmi", StringComparison.Ordinal),
                      "the stamp file records the processor, the C runtime and the path it was earned on",
                      o.Status + "; " + o.Message);

                // ---- the built-in suites ------------------------------------------------------------------
                List<string> names = new List<string>();
                foreach (ISelfTestSuite s in t.Suites) names.Add(s.Name);
                check(names.Count == 3 && names[0] == "numerics" && names[1] == "libm-dense" && names[2] == "denormals"
                      && !t.HasGeneratorSuite,
                      "three built-in suites, and none of them counts as the generator-level suite an unverified platform needs",
                      string.Join(", ", names));
                MachineSelfTest withGen = new MachineSelfTest(cache);
                withGen.Register(new FakeSuite("generator", true));
                check(withGen.HasGeneratorSuite, "registering one does", "");
            }
            finally
            {
                HardwareProbe.ReportDispatch(null, null);
                try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true); } catch (Exception) { }
            }

            // ---- libm-dense --------------------------------------------------------------------------------
            IReadOnlyList<LibmDense.SiteDigest> a = LibmDense.Compute();
            IReadOnlyList<LibmDense.SiteDigest> b = LibmDense.Compute();
            bool same = a.Count == b.Count && a.Count == 5;
            for (int i = 0; same && i < a.Count; i++) same = a[i].Raw == b[i].Raw && a[i].Consumed == b[i].Consumed;
            check(same, "libm-dense is deterministic: five sites, the same digests twice", a.Count + " sites");

            SelfTestSuiteResult ld = new LibmDense().Run(CancellationToken.None);
            check(ld.Passed && ld.Checks == 10,
                  "this machine's C runtime reproduces the recorded digests at every site, raw and as consumed",
                  ld + " in " + Bytes.Duration(ld.Elapsed));

            List<LibmDense.SiteDigest> tampered = new List<LibmDense.SiteDigest>(a);
            tampered[2] = new LibmDense.SiteDigest(a[2].Site, a[2].Raw, "0" + a[2].Consumed.Substring(1));
            SelfTestSuiteResult bad = LibmDense.Check(tampered);
            check(bad.Failures == 1 && bad.FirstFailure.Contains("sin-cos-float", StringComparison.Ordinal),
                  "one site's consumed values differing fails the suite, naming the site", bad.FirstFailure);
            List<LibmDense.SiteDigest> rawOnly = new List<LibmDense.SiteDigest>(a);
            rawOnly[0] = new LibmDense.SiteDigest(a[0].Site, "0" + a[0].Raw.Substring(1), a[0].Consumed);
            SelfTestSuiteResult badRaw = LibmDense.Check(rawOnly);
            check(badRaw.Failures == 1 && badRaw.FirstFailure.Contains("raw", StringComparison.Ordinal),
                  "a raw difference fails too, even when the consumed values agree", badRaw.FirstFailure);

            // ---- subnormals ---------------------------------------------------------------------------------
            check(DenormalProbe.CheckCurrentThread() == null, "this thread produces and reads subnormals as IEEE-754 says", "");
            SelfTestSuiteResult dn = new DenormalProbe().Run(CancellationToken.None);
            check(dn.Passed && dn.Checks == 8, "the denormal suite passes on the calling thread and on a new one", dn.ToString());

            // ---- a suite that threw: what a machine report may print --------------------------------------
            string secret = Path.Combine(Path.GetTempPath(), "someone", "groundtruth", "natives", "natives-libm.json");
            SelfTestSuiteResult threw = SelfTestSuiteResult.Threw("x", new FileNotFoundException("Could not find file '" + secret + "'.", secret));
            SelfTestSuiteResult denied = SelfTestSuiteResult.Threw("y", new UnauthorizedAccessException("Access to the path '" + secret + "' is denied."));
            check(threw.FirstFailure.Contains(secret, StringComparison.Ordinal)
                  && !threw.ShareableFailure.Contains("someone", StringComparison.Ordinal)
                  && threw.ShareableFailure.Contains("natives-libm.json", StringComparison.Ordinal)
                  && !denied.ShareableFailure.Contains("someone", StringComparison.Ordinal)
                  && denied.ShareableFailure.Contains("UnauthorizedAccessException", StringComparison.Ordinal),
                  "a suite that threw keeps the whole message for this machine, and only the exception type and the file's "
                  + "name for a report sent elsewhere (an I/O message names the full path)",
                  threw.ShareableFailure + " | " + denied.ShareableFailure);
            SelfTestSuiteResult numbersOnly = new SelfTestSuiteResult("z", 3, 1, "Math.Sin(1) gave 0x1, recorded 0x2", TimeSpan.Zero);
            check(numbersOnly.ShareableFailure == numbersOnly.FirstFailure, "a golden that did not reproduce shares its line as it is (numbers only)",
                  numbersOnly.ShareableFailure);

            // ---- the runtime's switches, both prefixes ------------------------------------------------------
            bool knobs = RuntimeKnobs.IsIsaKnob("DOTNET_EnableAVX512") && RuntimeKnobs.IsIsaKnob("COMPlus_EnableAVX512")
                         && RuntimeKnobs.IsIsaKnob("complus_enableavx2") && RuntimeKnobs.IsIsaKnob("DOTNET_EnableAVX")
                         && RuntimeKnobs.IsIsaKnob("DOTNET_EnableSSE42") && RuntimeKnobs.IsIsaKnob("DOTNET_EnableAVX10v2")
                         && RuntimeKnobs.IsIsaKnob("DOTNET_EnableAPX") && RuntimeKnobs.IsIsaKnob("COMPlus_EnableEmbeddedBroadcast")
                         && RuntimeKnobs.IsIsaKnob("DOTNET_PreferredVectorBitWidth") && RuntimeKnobs.IsIsaKnob("COMPlus_MaxVectorTBitWidth")
                         && !RuntimeKnobs.IsIsaKnob("DOTNET_EnableDiagnostics") && !RuntimeKnobs.IsIsaKnob("DOTNET_EnableEventPipe")
                         && !RuntimeKnobs.IsIsaKnob("COMPlus_EnableWriteXorExecute") && !RuntimeKnobs.IsIsaKnob("DOTNET_gcServer")
                         && !RuntimeKnobs.IsIsaKnob("SEEDLAB_SIMD") && !RuntimeKnobs.IsIsaKnob("EnableAVX2");
            check(knobs, "every code-generation switch is recognised under both prefixes, any case, and the diagnostics ones are not",
                  "DOTNET_/COMPlus_ Enable*, PreferredVectorBitWidth, MaxVectorTBitWidth");
            Dictionary<string, string?> childEnv = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["COMPlus_EnableAVX512"] = "0", ["DOTNET_EnableHWIntrinsic"] = "0", ["PATH"] = "x", ["DOTNET_EnableDiagnostics"] = "0",
            };
            List<string> strip = RuntimeKnobs.In(childEnv);
            check(strip.Count == 2 && strip.Contains("COMPlus_EnableAVX512") && strip.Contains("DOTNET_EnableHWIntrinsic"),
                  "a child's environment gives up exactly its code-generation switches", string.Join(", ", strip));
            IReadOnlyList<(string Name, string Value)> set = RuntimeKnobs.Set();
            check(set.Count == 0, "no code-generation switch is set in this test's own environment (the matrices would refuse)",
                  set.Count == 0 ? "none" : set[0].Name + "=" + set[0].Value);

            // ---- CPUID against the runtime ------------------------------------------------------------------
            CpuIdFeatures? cf = CpuIdFeatures.Read();
            if (cf == null)
            {
                check(!System.Runtime.Intrinsics.X86.X86Base.IsSupported, "CPUID cannot be read only where x86 intrinsics are off", "");
            }
            else
            {
                Dictionary<string, string> runtime = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["avx"] = Flag(System.Runtime.Intrinsics.X86.Avx.IsSupported),
                    ["fma"] = Flag(System.Runtime.Intrinsics.X86.Fma.IsSupported),
                    ["avx2"] = Flag(System.Runtime.Intrinsics.X86.Avx2.IsSupported),
                    ["avx512f"] = Flag(System.Runtime.Intrinsics.X86.Avx512F.IsSupported),
                    ["avx512bw"] = Flag(System.Runtime.Intrinsics.X86.Avx512BW.IsSupported),
                    ["avx512vbmi"] = Flag(System.Runtime.Intrinsics.X86.Avx512Vbmi.IsSupported),
                };
                List<string> off = cf.Disagreements(runtime);
                List<string> on = new List<string>();
                foreach ((string name, bool value) in cf.Bits()) if (value) on.Add(name);
                check(off.Count == 0, "this unswitched runtime reports exactly what the processor's CPUID bits imply",
                      off.Count == 0 ? string.Join(" ", on) : string.Join("; ", off));

                if (cf.ExpectedFacts()["avx512f"] == "1")
                {
                    Dictionary<string, string> masked = new Dictionary<string, string>(runtime) { ["avx512f"] = "0", ["avx512bw"] = "0", ["avx512vbmi"] = "0" };
                    List<string> caught = cf.Disagreements(masked);
                    check(caught.Count >= 2 && caught[0].Contains("switch", StringComparison.Ordinal),
                          "a runtime with AVX-512 switched off (as COMPlus_EnableAVX512=0 left in a shell does) disagrees with CPUID, naming the cause",
                          caught.Count > 0 ? caught[0] : "not caught");
                }
                else
                {
                    check(true, "the masked-AVX-512 comparison needs an AVX-512 CPU; not applicable here", "");
                }
            }

            // ---- AVX10 in the stamp key -------------------------------------------------------------------------
            CpuFeatures plain = new CpuFeatures(true, true, true, false, true, false, 32, true, false);
            CpuFeatures ten2 = new CpuFeatures(true, true, true, false, true, false, 32, true, false, avx10v1: true, avx10v2: true);
            check(plain.Key == "sse2 avx avx2 fma v32" && ten2.Key == "sse2 avx avx2 avx10v1 avx10v2 fma v32",
                  "AVX10.1 and AVX10.2 are in the stamp key where the runtime reports them, and a key without them is unchanged",
                  plain.Key + " | " + ten2.Key);
        }

        private static string Flag(bool b) => b ? "1" : "0";
    }
}
