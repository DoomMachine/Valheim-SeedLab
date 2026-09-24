using System;
using System.IO;
using SeedLab.Runtime;
using SeedLab.Runtime.Hardware;
using SeedLab.Runtime.SelfTest;

namespace SeedLab.RuntimeTests
{
    /// <summary>
    /// The runtime layer's tests. No test runner, no package: run it, read the lines, check the exit code.
    /// </summary>
    public static class Program
    {
        private static int _pass, _fail;

        public static int Main(string[] args)
        {
            if (Array.IndexOf(args, "--emit-vectors") >= 0) return EmitVectors(args);
            if (Array.IndexOf(args, "--probe") >= 0) return PrintProbe();
            if (Array.IndexOf(args, "--emit-libm-dense") >= 0) return EmitLibmDense();

            // ST2 needs the built vseed.exe, so it is its own run rather than a section here:
            // dotnet run -c Release --project tests\SeedLab.Runtime.Tests -- --knob-matrix
            if (Array.IndexOf(args, "--knob-matrix") >= 0) return KnobMatrix.Run(args);

            Console.WriteLine("SeedLab.Runtime tests");
            Console.WriteLine(new string('=', 78));

            Section("1. The hardware probe reports this machine, and never throws");
            HardwareChecks.Run(Check);

            Section("2. Modes, worker counts and the memory guard");
            ResourceChecks.Run(Check);

            Section("3. The auto-throttle: one line, never silent, always overridable");
            ThrottleChecks.Run(Check);

            Section("4. The cache root, per-process scratch, the reaper and durable writes");
            StorageChecks.Run(Check);

            Section("5. The estimator: ranges, constants, verdicts and live re-calibration");
            EstimateChecks.Run(Check);

            Section("6. The machine self-test fails closed");
            SelfTestChecks.Run(Check);

            Section("7. The session logs (this one and the last one), access checks, and a busy file waited out or named");
            SessionLogChecks.Run(Check);

            Section("8. The web servers' registry: found, told live from stale, and never made by reading it");
            ServerRegistryChecks.Run(Check);

            Section("9. The quiet-machine probe: what else ran, named, never pretended away");
            QuietProbeChecks.Run(Check);

            Section("10. The processor, the C runtime and the vector path in the stamp; libm-dense and subnormals");
            CpuChecks.Run(Check);

            Console.WriteLine();
            Console.WriteLine(new string('=', 78));
            Console.WriteLine((_fail == 0 ? "PASS" : "FAIL") + "  " + _pass + " checks passed, " + _fail + " failed");
            return _fail == 0 ? 0 : 1;
        }

        private static int PrintProbe()
        {
            HardwareInfo hw = HardwareProbe.Probe();
            Console.WriteLine("SeedLab.Runtime hardware probe");
            Console.WriteLine(new string('=', 78));
            Console.Write(hw.Format());
            Console.WriteLine("  volume(cwd) " + VolumeInfo.For(Directory.GetCurrentDirectory()));
            using RuntimeContext ctx = RuntimeContext.Start(new RuntimeOptions());
            Console.WriteLine();
            foreach (string line in ctx.StartupLines()) Console.WriteLine("  " + line);
            Console.WriteLine();
            foreach (string line in ctx.PlanWorkers(SeedLab.Runtime.Execution.WorkTier.BiomeGrid,
                                                    SeedLab.Runtime.Execution.WorkerFootprints.CellsForSpacing(384)).Lines())
                Console.WriteLine("  " + line);
            Console.WriteLine();
            Console.Write(ctx.Cache.MeasureUsage().Format());
            return 0;
        }

        /// <summary>
        /// Re-records the numeric goldens. Only legitimate on a machine that has just passed the full
        /// gates: recording on any other machine would bake its divergence in as the reference.
        /// </summary>
        private static int EmitVectors(string[] args)
        {
            string outPath = "selftest-vectors.txt";
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == "--out") outPath = args[i + 1];

            string text = NumericVectors.Record();
            File.WriteAllText(outPath, text, new System.Text.UTF8Encoding(false));
            Console.WriteLine("recorded " + text.Split('\n').Length + " lines to " + Path.GetFullPath(outPath));
            Console.WriteLine("copy it to src/SeedLab.Runtime/SelfTest/selftest-vectors.txt and rebuild");
            return 0;
        }

        /// <summary>
        /// Prints the libm-dense digests this machine computes, as the lines to paste into
        /// <c>LibmDense.Reference</c>. The same rule as --emit-vectors: only on a machine that has just
        /// passed the full gates, or the reference would describe the wrong library.
        /// </summary>
        private static int EmitLibmDense()
        {
            foreach (LibmDense.SiteDigest d in LibmDense.Compute())
            {
                Console.WriteLine("            (\"" + d.Site + "\", \"" + d.Raw + "\", \"" + d.Consumed + "\"),");
            }

            Console.WriteLine("// " + System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier + ", "
                              + System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription
                              + ", ucrtbase " + CpuIdentity.UcrtVersion());
            return 0;
        }

        private static void Section(string title)
        {
            Console.WriteLine();
            Console.WriteLine(title);
            Console.WriteLine(new string('-', title.Length));
        }

        public static void Check(bool ok, string name, string detail)
        {
            if (ok) _pass++;
            else _fail++;
            Console.WriteLine("  [" + (ok ? "ok  " : "FAIL") + "] " + name + (detail.Length > 0 ? " - " + detail : ""));
        }
    }
}
