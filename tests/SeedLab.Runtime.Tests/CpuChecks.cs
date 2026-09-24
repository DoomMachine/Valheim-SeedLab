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
        }
    }
}
