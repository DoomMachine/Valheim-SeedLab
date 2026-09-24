using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using SeedLab.Runtime.Hardware;
using SeedLab.Runtime.SelfTest;
using SeedLab.Runtime.Storage;

namespace SeedLab.RuntimeTests
{
    /// <summary>A registered generator-level suite, standing in for the one the CLI supplies.</summary>
    public sealed class FakeSuite : ISelfTestSuite
    {
        private readonly bool _pass;
        public FakeSuite(string name, bool pass) { Name = name; _pass = pass; }
        public string Name { get; }
        public string Describes => "a stand-in for the generator goldens";
        public SelfTestSuiteResult Run(CancellationToken cancel) =>
            new SelfTestSuiteResult(Name, 10, _pass ? 0 : 1,
                _pass ? "" : "sample 7 gave 0x3F800001, recorded 0x3F800000", TimeSpan.FromMilliseconds(1));
    }

    public static class SelfTestChecks
    {
        public static void Run(Action<bool, string, string> check)
        {
            string tempRoot = Path.Combine(Path.GetTempPath(),
                "seedlab-selftest-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                CacheRoot cache = CacheRoot.Open(new CacheRootOptions { Override = tempRoot });
                HardwareInfo hw = HardwareProbe.Probe();

                // --- the embedded vectors reproduce -------------------------------------------------
                NumericVectors v = NumericVectors.Embedded();
                SelfTestSuiteResult r = v.Run(CancellationToken.None);
                check(r.Passed && r.Checks >= 250,
                    "every recorded libm and float value reproduces bit for bit on this machine",
                    r.Checks + " values exact in " + Bytes.Duration(r.Elapsed));

                check(NumericVectors.Record().Trim() != "" &&
                      NumericVectors.FromText(NumericVectors.Record()).Run(CancellationToken.None).Passed,
                    "recording and verifying agree (the vectors describe what the code computes)", "");

                // --- a divergence fails, and says what to do -----------------------------------------
                string[] recorded = NumericVectors.Record().Split('\n');
                for (int i = 0; i < recorded.Length; i++)
                    if (recorded[i].StartsWith("f64 chain-f64 ", StringComparison.Ordinal))
                        recorded[i] = "f64 chain-f64 DEADBEEFDEADBEEF";
                string tampered = string.Join("\n", recorded);
                MachineSelfTest bad = new MachineSelfTest(cache, NumericVectors.FromText(tampered));
                SelfTestOutcome badOutcome = bad.Verify(hw, force: true);
                check(badOutcome.Status == SelfTestStatus.Failed,
                    "a single divergent bit fails the machine", badOutcome.Results.Count + " suites run");
                check(badOutcome.Message.Contains("SELF-TEST FAILED")
                      && badOutcome.Message.Contains("chain-f64")
                      && badOutcome.Message.Contains("What to do"),
                    "the message names the failing case and what to do about it",
                    badOutcome.Message.Split('\n')[0]);

                bool threw = false;
                try { badOutcome.EnsureUsable(); }
                catch (SelfTestFailedException) { threw = true; }
                check(threw, "and it FAILS CLOSED - the run stops rather than reporting unverified seeds", "");

                // A vector file that covers fewer cases than the code defines is a failure too.
                string thin = "# a vector file that records nothing\n";
                SelfTestSuiteResult thinResult = NumericVectors.FromText(thin).Run(CancellationToken.None);
                check(!thinResult.Passed && thinResult.FirstFailure.Contains("emit-vectors"),
                    "a vector file that stopped covering the cases is itself a failure",
                    thinResult.FirstFailure);

                // --- a clean pass, and the stamp ------------------------------------------------------
                MachineSelfTest good = new MachineSelfTest(cache);
                SelfTestOutcome first = good.Verify(hw, force: true);
                check(first.Status == SelfTestStatus.Passed, "a good machine passes", first.Message);
                first.EnsureUsable();

                SelfTestOutcome second = good.Verify(hw);
                check(second.Status == SelfTestStatus.PassedCached,
                    "the pass is stamped, so it costs nothing on the next command", second.Message);
                check(good.Verify(hw, force: true).Status == SelfTestStatus.Passed,
                    "--recheck runs it again anyway", "");

                // --- the fingerprint covers what matters -----------------------------------------------
                MachineSelfTest withSuite = new MachineSelfTest(cache);
                withSuite.Register(new FakeSuite("generator", true));
                check(withSuite.Fingerprint(hw) != good.Fingerprint(hw),
                    "registering a suite changes the fingerprint, so the gate re-runs",
                    good.Fingerprint(hw) + " -> " + withSuite.Fingerprint(hw));
                check(withSuite.Verify(hw, force: true).Status == SelfTestStatus.Passed && withSuite.HasGeneratorSuite,
                    "a registered generator suite runs alongside the numeric one", "");

                MachineSelfTest failing = new MachineSelfTest(cache);
                failing.Register(new FakeSuite("generator", false));
                SelfTestOutcome fo = failing.Verify(hw, force: true);
                check(fo.Status == SelfTestStatus.Failed && fo.Message.Contains("sample 7"),
                    "a failing generator suite fails the machine as loudly as the numeric one",
                    fo.Message.Split('\n')[1]);

                MachineSelfTest otherVectors = new MachineSelfTest(cache, NumericVectors.FromText(tampered));
                check(otherVectors.Fingerprint(hw) != good.Fingerprint(hw),
                    "a different vector set is a different fingerprint, so the gate re-runs",
                    "embedded vectors " + NumericVectors.Embedded().VectorsHash);

                // --- the unverified-architecture policy ---------------------------------------------------
                check(MachineSelfTest.IsVerifiedArchitecture(Architecture.X64)
                      && !MachineSelfTest.IsVerifiedArchitecture(Architecture.Arm64),
                    "x64 is the verified architecture; arm64 is not", "");

                MachineSelfTest off = new MachineSelfTest(cache) { Enabled = false };
                SelfTestOutcome skipped = off.Verify(hw, force: true);
                check(skipped.Status == SelfTestStatus.Skipped && skipped.Message.Contains("UNVERIFIED"),
                    "--skip-self-test is allowed, and says on the record that the run is unverified",
                    skipped.Message);
                skipped.EnsureUsable();
                check(true, "a skipped self-test does not throw (the user chose it)", "");
            }
            finally
            {
                try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true); } catch (Exception) { }
            }
        }
    }
}
