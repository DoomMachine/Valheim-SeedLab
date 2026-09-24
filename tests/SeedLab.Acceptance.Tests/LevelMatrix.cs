using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using SeedLab.LocationOracle;
using SeedLab.Runtime.Hardware;
using SeedLab.WorldGen.Simd;
using SeedLab.WorldGen.Unity;

namespace SeedLabAcceptanceTests
{
    /// <summary>
    /// The same worlds at every vector level this machine can reach (design 12.5, the fingerprint half
    /// of ST8).
    ///
    ///   dotnet run -c Release --project tests\SeedLab.Acceptance.Tests -- --level-matrix [--seeds N] [--threads N] [--levels K0,K3,K6,K7,K8,K10]
    ///
    /// <para>Each level is a CHILD process of this test's own executable, started with one runtime knob
    /// or request in its environment: K0 none, K3 <c>DOTNET_EnableAVX512=0</c>, K6
    /// <c>DOTNET_EnableAVX2=0</c>, K7 <c>DOTNET_EnableHWIntrinsic=0</c>, K8 <c>SEEDLAB_SIMD=scalar</c>,
    /// K10 <c>DOTNET_EnableAVX=0</c> (the shape of a CPU without AVX: SSE4.2 and 128-bit vectors). The
    /// knobs are read once at runtime start-up, which is why each level needs its own process. The child
    /// proves the knob took effect twice over: <c>SEEDLAB_SIMD_EXPECT</c> is set to what the level must
    /// look like, so the WorldGen module initialiser refuses to load if it does not; and the child prints
    /// its ISA facts, which this parent checks again. It then computes the five-layer fingerprints of
    /// the reference seeds with no profiler attached, and every digest must equal
    /// <c>WorldFingerprintReference.json</c>, recorded at the default level.</para>
    ///
    /// <para><b>This process is K0</b>, and which levels exist is derived from its flags - so it refuses to
    /// run while any runtime code-generation switch is set in its environment (<c>DOTNET_</c> or
    /// <c>COMPlus_</c>, <see cref="RuntimeKnobs"/>), compares its flags with the processor's CPUID bits
    /// (<see cref="CpuIdFeatures"/>), and takes every such switch out of each child's environment before
    /// setting the one its level names.</para>
    ///
    /// <para>What a level changes is not only the Perlin path. The JIT also compiles ordinary scalar code
    /// differently with AVX-512 on or off (EVEX encodings, the saturating float-to-integer sequences)
    /// and with no intrinsics at all, so K3 and K7 test the compiled generator, not only
    /// <c>PerlinFast</c>. What no knob changes is the C runtime's libm: <c>Math.Sin</c> goes to
    /// ucrtbase.dll, which picks its FMA3 path from the CPU itself, so a CPU without FMA3 is NOT
    /// simulated here.</para>
    /// </summary>
    public static class LevelMatrix
    {
        private sealed class Level
        {
            public string Id = "";
            public string Knob = "";
            public string Value = "";
            public Dictionary<string, string> Expect = new Dictionary<string, string>(StringComparer.Ordinal);
            public bool Applicable = true;
        }

        public static int Run(string[] args)
        {
            int threads = ProfileNeutrality.IntArg(args, "--threads", Math.Max(1, Environment.ProcessorCount / 2));
            int limit = ProfileNeutrality.IntArg(args, "--seeds", 64);
            string only = "";
            for (int i = 0; i < args.Length - 1; i++) if (args[i] == "--levels") only = args[i + 1];

            Console.WriteLine("SeedLab level matrix: the reference worlds at every vector level this machine can reach");
            string refPath = Path.Combine(AppContext.BaseDirectory, ProfileNeutrality.ReferenceFile);
            if (!File.Exists(refPath))
            {
                Console.Error.WriteLine("the recorded reference " + refPath + " is missing.");
                return 2;
            }

            List<string> leaked = new List<string>();
            foreach ((string name, string value) in RuntimeKnobs.Set()) leaked.Add(name + "=" + value);
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(SimdDispatch.EnvironmentVariable)))
            {
                leaked.Add(SimdDispatch.EnvironmentVariable + "=" + Environment.GetEnvironmentVariable(SimdDispatch.EnvironmentVariable));
            }

            if (leaked.Count > 0)
            {
                foreach (string k in leaked)
                {
                    Console.WriteLine("FAIL  " + k + " is set in the parent; the parent is the default level and must run without knobs.");
                }

                return 1;
            }

            List<(int Seed, string[] Layers, bool Stale)> reference = ProfileNeutrality.ReadReference(refPath, out string stamp);
            if (limit < reference.Count) reference = reference.GetRange(0, Math.Max(1, limit));
            Dictionary<string, string> k0 = SimdDispatch.Facts();
            Console.WriteLine("  reference      " + ProfileNeutrality.ReferenceFile + " (data " + stamp + "), " + reference.Count + " seeds");
            Console.WriteLine("  threads        " + threads + " per child");
            Console.WriteLine("  this process   " + SimdDispatch.Summary);

            // Independent of the runtime: what this processor's CPUID bits say an unswitched runtime reports.
            CpuIdFeatures? cpuid = CpuIdFeatures.Read();
            if (cpuid == null)
            {
                Console.WriteLine("FAIL  the processor's CPUID bits cannot be read here, so K0 cannot be shown to be the default level");
                return 1;
            }

            List<string> off = cpuid.Disagreements(k0);
            if (off.Count > 0)
            {
                foreach (string d in off) Console.WriteLine("FAIL  K0 is not the default level: " + d);
                return 1;
            }

            Console.WriteLine("  K0 = default   the runtime's flags are what the CPUID bits imply");

            int failures = 0, ran = 0, notApplicable = 0;
            foreach (Level lv in Levels(k0))
            {
                if (only.Length > 0 && Array.IndexOf(only.Split(','), lv.Id) < 0) continue;
                string label = (lv.Id + " " + (lv.Knob.Length == 0 ? "default" : lv.Knob + "=" + lv.Value)).PadRight(34);
                if (!lv.Applicable)
                {
                    notApplicable++;
                    Console.WriteLine("n/a   " + label + " this CPU lacks what the knob turns off (the runtime and the CPUID "
                                      + "bits agree), so the level is the default one");
                    continue;
                }

                ran++;
                failures += RunLevel(lv, label, reference, threads);
            }

            Console.WriteLine();
            Console.WriteLine((failures == 0 ? "PASS" : "FAIL") + "  level matrix: " + ran + " level(s) run, " + notApplicable
                              + " not applicable here, " + (failures == 0
                                  ? "every digest of every layer identical to the recording at every level"
                                  : failures + " failure(s)"));
            return failures == 0 && ran > 0 ? 0 : 1;
        }

        private static IEnumerable<Level> Levels(Dictionary<string, string> k0)
        {
            bool has2 = k0["avx2"] == "1", has512 = k0["avx512f"] == "1", x86 = k0["x86base"] == "1", hasAvx = k0["avx"] == "1";
            yield return new Level
            {
                Id = "K0",
                Expect = { ["hardware"] = k0["hardware"], ["active"] = k0["active"], ["avx2"] = k0["avx2"], ["avx512f"] = k0["avx512f"] },
            };
            yield return new Level
            {
                Id = "K3", Knob = "DOTNET_EnableAVX512", Value = "0", Applicable = has512,
                Expect = { ["avx512f"] = "0", ["avx512bw"] = "0", ["avx2"] = k0["avx2"], ["active"] = has2 ? "avx2" : "scalar" },
            };
            yield return new Level
            {
                Id = "K6", Knob = "DOTNET_EnableAVX2", Value = "0", Applicable = has2,
                Expect = { ["avx2"] = "0", ["avx512f"] = "0", ["active"] = "scalar" },
            };
            yield return new Level
            {
                Id = "K7", Knob = "DOTNET_EnableHWIntrinsic", Value = "0", Applicable = x86,
                Expect = { ["x86base"] = "0", ["sse2"] = "0", ["avx2"] = "0", ["v128acc"] = "0", ["active"] = "scalar" },
            };
            yield return new Level
            {
                Id = "K8", Knob = SimdDispatch.EnvironmentVariable, Value = "scalar", Applicable = has2,
                Expect = { ["avx2"] = k0["avx2"], ["requested"] = "scalar", ["active"] = "scalar" },
            };
            yield return new Level
            {
                Id = "K10", Knob = "DOTNET_EnableAVX", Value = "0", Applicable = hasAvx,
                Expect =
                {
                    ["avx"] = "0", ["avx2"] = "0", ["fma"] = "0", ["sse42"] = k0["sse42"], ["v128acc"] = "1",
                    ["vector_bytes"] = "16", ["active"] = "scalar",
                },
            };
        }

        private static int RunLevel(Level lv, string label, List<(int Seed, string[] Layers, bool Stale)> reference, int threads)
        {
            string exe = Environment.ProcessPath ?? "";
            ProcessStartInfo psi = new ProcessStartInfo
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                FileName = exe,
            };
            if (string.Equals(Path.GetFileNameWithoutExtension(exe), "dotnet", StringComparison.OrdinalIgnoreCase))
            {
                psi.ArgumentList.Add(typeof(LevelMatrix).Assembly.Location);
            }

            psi.ArgumentList.Add("--level-child");
            psi.ArgumentList.Add("--seeds");
            psi.ArgumentList.Add(reference.Count.ToString(CultureInfo.InvariantCulture));
            psi.ArgumentList.Add("--threads");
            psi.ArgumentList.Add(threads.ToString(CultureInfo.InvariantCulture));
            foreach (string k in RuntimeKnobs.In(psi.Environment)) psi.Environment.Remove(k);
            psi.Environment.Remove(SimdDispatch.EnvironmentVariable);
            if (lv.Knob.Length > 0) psi.Environment[lv.Knob] = lv.Value;

            List<string> expect = new List<string>();
            foreach (KeyValuePair<string, string> kv in lv.Expect) expect.Add(kv.Key + "=" + kv.Value);
            psi.Environment[SimdDispatch.ExpectVariable] = string.Join(",", expect);

            Stopwatch sw = Stopwatch.StartNew();
            using Process p = Process.Start(psi) ?? throw new InvalidOperationException("the child did not start");
            Task<string> err = p.StandardError.ReadToEndAsync();
            string stdout = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            sw.Stop();
            if (p.ExitCode != 0)
            {
                string e = err.Result.Trim();
                int nl = e.IndexOf('\n');
                Console.WriteLine("FAIL  " + label + " the child exited " + p.ExitCode + " - knob had no effect, or the level failed: "
                                  + (nl < 0 ? e : e.Substring(0, nl)));
                return 1;
            }

            using JsonDocument doc = JsonDocument.Parse(stdout);
            JsonElement root = doc.RootElement;
            int bad = 0;
            Dictionary<string, string> facts = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (JsonProperty f in root.GetProperty("facts").EnumerateObject()) facts[f.Name] = f.Value.GetString() ?? "";
            foreach (KeyValuePair<string, string> kv in lv.Expect)
            {
                if (!facts.TryGetValue(kv.Key, out string? have) || have != kv.Value)
                {
                    Console.WriteLine("FAIL  " + label + " " + kv.Key + " is " + (have ?? "missing") + ", expected " + kv.Value
                                      + " - knob had no effect");
                    bad++;
                }
            }

            ProfileNeutrality.Outcome o = new ProfileNeutrality.Outcome { Seconds = sw.Elapsed.TotalSeconds };
            foreach (JsonElement r in root.GetProperty("results").EnumerateArray()) o.Prints.Add(ProfileNeutrality.FromJson(r));
            if (o.Prints.Count != reference.Count)
            {
                Console.WriteLine("FAIL  " + label + " the child returned " + o.Prints.Count + " fingerprints, expected " + reference.Count);
                return bad + 1;
            }

            List<string> on = new List<string>();
            foreach (string f in new[] { "x86base", "sse2", "sse42", "avx", "avx2", "fma", "avx512f", "avx512bw", "avx512vbmi", "v512acc" })
            {
                if (facts.TryGetValue(f, out string? v) && v == "1") on.Add(f);
            }

            Console.WriteLine("      " + label + " flags [" + string.Join(" ", on) + "], active " + facts["active"]
                              + ", " + (root.GetProperty("perlin_8wide").GetBoolean() ? "8-wide" : "scalar") + " Perlin");
            bad += ProfileNeutrality.Compare(lv.Id, o, reference);
            return bad;
        }

        /// <summary>The child: this process's facts and the reference seeds' fingerprints, profiler off.</summary>
        public static int Child(string[] args)
        {
            int threads = ProfileNeutrality.IntArg(args, "--threads", 1);
            int limit = ProfileNeutrality.IntArg(args, "--seeds", 64);
            int[] all = WorldFingerprinter.ReferenceSeeds();
            int[] seeds = all.AsSpan(0, Math.Min(limit, all.Length)).ToArray();
            using WorldFingerprinter fp = WorldFingerprinter.Open();
            ProfileNeutrality.Outcome o = ProfileNeutrality.Compute(fp, seeds, threads, withSinks: false);

            using MemoryStream ms = new MemoryStream();
            using (Utf8JsonWriter j = new Utf8JsonWriter(ms))
            {
                j.WriteStartObject();
                j.WriteStartObject("facts");
                foreach (KeyValuePair<string, string> kv in SimdDispatch.Facts()) j.WriteString(kv.Key, kv.Value);
                j.WriteEndObject();
                j.WriteBoolean("perlin_8wide", PerlinFast.Use8Wide);
                j.WriteString("perlin", PerlinSelfTest.Report());
                j.WriteStartArray("results");
                foreach (WorldFingerprint f in o.Prints) ProfileNeutrality.WriteJson(j, f, null);
                j.WriteEndArray();
                j.WriteEndObject();
            }

            Console.Out.Write(Encoding.UTF8.GetString(ms.ToArray()));
            return 0;
        }
    }
}
