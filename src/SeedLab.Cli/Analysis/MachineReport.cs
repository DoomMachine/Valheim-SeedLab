using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SeedLab.Cli.Infra;
using SeedLab.LocationOracle;
using SeedLab.Runtime.Execution;
using SeedLab.Runtime.Hardware;
using SeedLab.Runtime.SelfTest;
using SeedLab.WorldGen.Simd;
using SeedLab.WorldGen.Unity;

namespace SeedLab.Cli.Analysis
{
    /// <summary>
    /// <c>vseed selftest --report</c> and <c>vseed selftest --isa-json</c>: what this machine is, which
    /// vector path the generator runs on it, whether its arithmetic reproduces the reference machine's,
    /// and whether its worlds do.
    ///
    /// <para><b>Who it is for.</b> A tester on a CPU the author does not have runs it and sends the text
    /// back; nothing is ever sent by SeedLab itself. It works on a clone of the public repository - no
    /// <c>groundtruth\</c> and no <c>data\</c>: the terrain layers of the world fingerprints need only the
    /// seed, and the location layers say they were not computed rather than fail.</para>
    ///
    /// <para><b>What it never contains:</b> the machine's name, the user's name, any path (the cache root,
    /// the game folder, the data folder and the ground-truth folder all can name the user), serial
    /// numbers or network addresses. Hardware and OS version only: the CPU's own identity, its ISA flags,
    /// the runtime and C runtime versions, digests of computed values.</para>
    ///
    /// <para><b>What it compares against.</b> The world fingerprints recorded by the acceptance suite on
    /// the reference machine (the one whose output the gates compare with the game itself), embedded in
    /// this build, and the <c>libm-dense</c> digests recorded with them. "=" means bit-identical.</para>
    /// </summary>
    public static class MachineReport
    {
        public const string Schema = "seedlab-machine-report/1";
        public const string IsaSchema = "seedlab-isa/1";
        private const string ReferenceResource = "SeedLab.Cli.WorldFingerprintReference.json";

        /// <summary>
        /// SeedLab's own variables that change how this process runs, printed when set: the vector-path
        /// request, the per-level proof's assertion and the profiler's counter switch (which slows every
        /// world and would otherwise be invisible). The runtime's switches come from
        /// <see cref="RuntimeKnobs"/>, both prefixes. Values only - never other variables.
        /// </summary>
        private static readonly string[] SeedLabVariables =
        {
            SimdDispatch.EnvironmentVariable, SimdDispatch.ExpectVariable, SeedLab.WorldGen.Diagnostics.PhaseClock.EnvironmentVariable,
        };

        // =============================================================================================
        // --isa-json
        // =============================================================================================

        /// <summary>
        /// The in-process ISA state and dispatch as one JSON document on stdout, whatever <c>--json</c>
        /// says: the knob matrix spawns this under each runtime knob and asserts the effect from it.
        /// </summary>
        public static int IsaJson(CliRuntime rt)
        {
            HardwareInfo hw = rt.Context.Hardware;
            using MemoryStream ms = new MemoryStream();
            using (Utf8JsonWriter j = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
            {
                j.WriteStartObject();
                j.WriteString("schema", IsaSchema);
                j.WriteStartObject("facts");
                foreach (KeyValuePair<string, string> kv in SimdDispatch.Facts()) j.WriteString(kv.Key, kv.Value);
                j.WriteEndObject();
                j.WriteString("dispatch_key", SimdDispatch.Key);
                j.WriteString("dispatch", SimdDispatch.Summary);
                j.WriteString("reason", SimdDispatch.Reason);
                j.WriteBoolean("perlin_8wide", PerlinFast.Use8Wide);
                j.WriteString("perlin_selftest", PerlinSelfTest.Report());
                WriteKnobs(j);
                WriteCpu(j, hw);
                WriteCpuId(j);
                j.WriteString("ucrt", hw.UcrtVersion);
                j.WriteString("isa_key", hw.Features.Key);
                j.WriteString("stamp_key", hw.StampKey);
                j.WriteString("self_test", rt.SelfTest?.Status.ToString() ?? "not run");
                j.WriteString("self_test_fingerprint", rt.SelfTest?.Fingerprint ?? "");
                j.WriteEndObject();
            }

            Console.Out.WriteLine(System.Text.Encoding.UTF8.GetString(ms.ToArray()));
            return rt.SelfTest == null || rt.SelfTest.Ok || rt.SelfTest.Status == SelfTestStatus.Skipped
                ? ExitCodes.Ok
                : ExitCodes.CheckFailed;
        }

        // =============================================================================================
        // --report
        // =============================================================================================

        private sealed class Reference
        {
            public string DataStamp = "";
            public List<(int Seed, string[] Layers)> Seeds = new List<(int, string[])>();
        }

        private sealed class Row
        {
            public int Seed;
            public string[] Layers = new string[5];
            public bool[] Computed = new bool[5];
            public string[]? Want;
        }

        public static int Report(Args a, Out o, CliRuntime rt)
        {
            int seedCount = Math.Clamp(a.Int("seeds", 8), 1, 64);
            a.RejectUnknown();

            HardwareInfo hw = rt.Context.Hardware;

            // ---- the self-test, run now rather than read from a stamp -----------------------------------
            // --skip-self-test turns the gate off for commands that answer questions; this report IS the
            // self-test, sent from a machine nobody has checked, so it runs it anyway and says so.
            MachineSelfTest gate = rt.Context.SelfTest;
            bool skipAsked = !gate.Enabled;
            if (skipAsked)
            {
                gate.Enabled = true;
                NativesGoldenSuite? natives = NativesGoldenSuite.TryCreate();
                if (natives != null) gate.Register(natives);
            }

            SelfTestOutcome st = gate.Verify(hw, force: true);
            string perlin;
            bool perlinOk = true;
            try { perlin = PerlinSelfTest.ProveEveryPath(); }
            catch (Exception ex)
            {
                perlin = "FAILED: " + ex.Message;
                perlinOk = false;
            }

            IReadOnlyList<LibmDense.SiteDigest> libm = LibmDense.Compute();

            // ---- the fingerprints -----------------------------------------------------------------------
            Reference? reference = LoadReference();
            List<int> seeds = new List<int>();
            if (reference != null)
            {
                for (int i = 0; i < reference.Seeds.Count && seeds.Count < seedCount; i++) seeds.Add(reference.Seeds[i].Seed);
            }
            else
            {
                int[] all = WorldFingerprinter.ReferenceSeeds();
                for (int i = 0; i < all.Length && seeds.Count < seedCount; i++) seeds.Add(all[i]);
            }

            WorldFingerprinter? fp = null;
            string locationNote;
            try
            {
                fp = WorldFingerprinter.Open();
                locationNote = reference != null && !string.Equals(fp.DataStamp, reference.DataStamp, StringComparison.Ordinal)
                    ? "this build's game data is " + fp.DataStamp + " and the reference was recorded on " + reference.DataStamp
                      + ": L4-L5 are computed but not compared"
                    : "computed with game data " + fp.DataStamp;
            }
            catch (Exception ex)
            {
                // Never the exception's text: it names the folder it looked in, which can name the user.
                locationNote = "not computed: " + (ex is SeedLab.Data.StaleGameDataException
                    ? "the game data is for another Valheim build"
                    : "no usable game data (data\\) beside this build - terrain layers only");
            }

            WorkerPlan plan = rt.Plan(fp != null ? WorkTier.LocationsAll : WorkTier.HeightsRivers, 12.0);
            Row[] rows = new Row[seeds.Count];
            DateTime t0 = DateTime.UtcNow;
            try
            {
                Parallel.For(0, seeds.Count, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, plan.Workers) }, i =>
                {
                    Row r = new Row { Seed = seeds[i] };
                    if (fp != null)
                    {
                        WorldFingerprint f = fp.Compute(seeds[i]);
                        for (int l = 0; l < 5; l++) { r.Layers[l] = f.Layer(l); r.Computed[l] = true; }
                    }
                    else
                    {
                        TerrainFingerprint f = WorldFingerprinter.ComputeTerrain(seeds[i]);
                        for (int l = 0; l < 3; l++) { r.Layers[l] = f.Layer(l); r.Computed[l] = true; }
                    }

                    if (reference != null)
                    {
                        foreach ((int seed, string[] layers) in reference.Seeds)
                        {
                            if (seed == seeds[i]) r.Want = layers;
                        }
                    }

                    rows[i] = r;
                });
            }
            finally
            {
                fp?.Dispose();
            }

            double fpSeconds = (DateTime.UtcNow - t0).TotalSeconds;
            bool locationComparable = fp != null && reference != null && locationNote.StartsWith("computed with", StringComparison.Ordinal);

            int compared = 0, differ = 0;
            foreach (Row r in rows)
            {
                for (int l = 0; l < 5; l++)
                {
                    if (!r.Computed[l] || r.Want == null) continue;
                    if (l >= 3 && !locationComparable) continue;
                    compared++;
                    if (!string.Equals(r.Layers[l], r.Want[l], StringComparison.Ordinal)) differ++;
                }
            }

            int libmDiffer = 0;
            foreach (LibmDense.SiteDigest d in libm)
            {
                foreach ((string site, string raw, string consumed) in LibmDense.Recorded)
                {
                    if (site != d.Site) continue;
                    if (d.Raw != raw) libmDiffer++;
                    if (d.Consumed != consumed) libmDiffer++;
                }
            }

            bool pass = st.Ok && perlinOk && differ == 0 && libmDiffer == 0 && compared > 0;
            string verdict = pass
                ? "PASS - this machine reproduces the reference machine's arithmetic and all " + compared
                  + " compared world digests bit for bit"
                : "FAIL - " + string.Join("; ", Failures(st, perlinOk, differ, compared, libmDiffer));

            if (o.Json)
            {
                WriteJson(o.J, rt, hw, st, skipAsked, perlin, libm, rows, locationNote, locationComparable, compared, differ, libmDiffer, pass, verdict);
                return pass ? ExitCodes.Ok : ExitCodes.CheckFailed;
            }

            o.Header("SeedLab machine report (" + Schema + ")");
            o.Line("  Hardware and software versions only: no machine name, user name or path. Send the text as it is.");
            o.Line();
            o.Field("program", "vseed " + Verified.EngineVersion + ", for Valheim " + Verified.GameVersion
                               + " (worldGenVersion " + Verified.WorldGenVersion + ")");
            o.Field("os", hw.OSDescription + " (" + hw.RuntimeIdentifier + ")");
            o.Field("runtime", hw.FrameworkDescription + ", process " + hw.ProcessArchitecture + " on " + hw.OSArchitecture
                               + (GCSettings.IsServerGC ? ", server GC" : ", workstation GC"));
            o.Field("cpu", hw.Cpu.Describe());
            o.Field("logical cores", hw.LogicalCores.ToString(CultureInfo.InvariantCulture));
            o.Field("isa", IsaLine());
            o.Field("cpuid", CpuIdLine());
            o.Field("simd path", SimdDispatch.Summary);
            o.Field("simd reason", SimdDispatch.Reason);
            o.Field("runtime knobs", KnobLine());
            o.Field("ucrtbase", hw.UcrtVersion);
            o.Field("stamp key", hw.StampKey);

            o.Header("Self-test (run now, not read from a stamp" + (skipAsked ? "; --skip-self-test does not apply to this report" : "") + ")");
            foreach (SelfTestSuiteResult r in st.Results) o.Field(r.Name, Shareable(r));
            if (!rt.Context.SelfTest.HasGeneratorSuite)
            {
                o.Field("seedlab/natives", "not run - groundtruth\\natives is not beside this build (it is not in the public repository)");
            }

            o.Field("perlin", perlin);
            o.Field("status", st.Status + (st.Ok ? "" : ": " + FirstLine(st.Message)));

            o.Header("libm-dense digests (65,536 arguments per site; = means equal to the reference machine)");
            foreach (LibmDense.SiteDigest d in libm)
            {
                string rawRef = "", useRef = "";
                foreach ((string site, string raw, string consumed) in LibmDense.Recorded)
                {
                    if (site == d.Site) { rawRef = raw; useRef = consumed; }
                }

                o.Field(d.Site, "raw " + Short(d.Raw) + (d.Raw == rawRef ? " =" : " DIFFERS")
                                + "   consumed " + Short(d.Consumed) + (d.Consumed == useRef ? " =" : " DIFFERS"));
            }

            o.Header("World fingerprints (" + rows.Length + " seeds; L1-L3 need only the seed, L4-L5 need game data)");
            o.Line("  L1 lattice biome + base height, L2 pre-generation, L3 the 2048^2 point grid, L4 all placements, L5 the oracle");
            o.Line("  L4-L5: " + locationNote);
            List<string[]> table = new List<string[]>();
            foreach (Row r in rows)
            {
                string[] cells = new string[6];
                cells[0] = r.Seed.ToString(CultureInfo.InvariantCulture);
                for (int l = 0; l < 5; l++)
                {
                    if (!r.Computed[l]) { cells[l + 1] = "-"; continue; }
                    string mark = r.Want == null || (l >= 3 && !locationComparable) ? "" :
                                  string.Equals(r.Layers[l], r.Want[l], StringComparison.Ordinal) ? " =" : " DIFFERS";
                    cells[l + 1] = r.Layers[l].Substring(0, 12) + mark;
                }

                table.Add(cells);
            }

            o.Table(new[] { "seed", "L1", "L2", "L3", "L4", "L5" }, table);
            o.Line("  (" + Out.F(fpSeconds, 1) + " s on " + plan.Workers + " worker(s); --json prints every digest in full)");

            o.Header("Verdict");
            o.Line("  " + verdict);
            o.Line();
            return pass ? ExitCodes.Ok : ExitCodes.CheckFailed;
        }

        private static IEnumerable<string> Failures(SelfTestOutcome st, bool perlinOk, int differ, int compared, int libmDiffer)
        {
            if (!st.Ok) yield return "the machine self-test did not pass (" + st.Status + ")";
            if (!perlinOk) yield return "a Perlin path differs from the reference transcription";
            if (libmDiffer > 0) yield return libmDiffer + " libm-dense digest(s) differ from the reference machine's";
            if (differ > 0) yield return differ + " of " + compared + " world digests differ from the reference machine's";
            if (compared == 0) yield return "no world digest could be compared (the embedded reference is missing)";
        }

        /// <summary>
        /// One suite's line for the report: the counts and, when it failed, the failure WITHOUT an
        /// exception message - an I/O error's message names the full path, and this text is sent to
        /// someone else.
        /// </summary>
        private static string Shareable(SelfTestSuiteResult r)
            => (r.Checks - r.Failures).ToString(CultureInfo.InvariantCulture) + "/" + r.Checks.ToString(CultureInfo.InvariantCulture)
               + " exact" + (r.Passed ? "" : "  FIRST FAILURE: " + r.ShareableFailure);

        /// <summary>The processor's own feature bits, and whether the runtime's flags agree with them.</summary>
        private static string CpuIdLine()
        {
            CpuIdFeatures? c = CpuIdFeatures.Read();
            if (c == null) return "not readable in this process (x86 intrinsics are switched off, or not x86)";
            List<string> on = new List<string>();
            foreach ((string name, bool value) in c.Bits()) if (value) on.Add(name);
            if (c.Avx10) on.Add("avx10." + c.Avx10Version.ToString(CultureInfo.InvariantCulture));
            List<string> off = c.Disagreements(SimdDispatch.Facts());
            return string.Join(" ", on) + (off.Count == 0 ? "; the runtime reports what these bits imply"
                                                          : "; DIFFERS from the runtime: " + string.Join("; ", off));
        }

        private static void WriteCpuId(Utf8JsonWriter j)
        {
            CpuIdFeatures? c = CpuIdFeatures.Read();
            if (c == null)
            {
                j.WriteNull("cpuid");
                return;
            }

            j.WriteStartObject("cpuid");
            j.WriteStartObject("bits");
            foreach ((string name, bool value) in c.Bits()) j.WriteString(name, value ? "1" : "0");
            j.WriteEndObject();
            j.WriteNumber("avx10_version", c.Avx10Version);
            j.WriteStartObject("expected");
            foreach (KeyValuePair<string, string> kv in c.ExpectedFacts()) j.WriteString(kv.Key, kv.Value);
            j.WriteEndObject();
            j.WriteStartArray("disagreements");
            foreach (string d in c.Disagreements(SimdDispatch.Facts())) j.WriteStringValue(d);
            j.WriteEndArray();
            j.WriteEndObject();
        }

        private static string FirstLine(string s)
        {
            int nl = s.IndexOf('\n');
            return nl < 0 ? s : s.Substring(0, nl);
        }

        private static string Short(string hex) => hex.Length > 16 ? hex.Substring(0, 16) : hex;

        private static string IsaLine()
        {
            List<string> on = new List<string>();
            foreach ((string name, bool value) in SimdDispatch.Isa.Flags()) if (value) on.Add(name);
            return (on.Count == 0 ? "none" : string.Join(" ", on)) + ", Vector<T> " + SimdDispatch.Isa.VectorByteWidth + " bytes";
        }

        private static string KnobLine()
        {
            List<string> set = new List<string>();
            foreach ((string name, string value) in SetKnobs()) set.Add(name + "=" + value);
            return set.Count == 0 ? "none set" : string.Join(" ", set);
        }

        /// <summary>The runtime's code-generation switches and SeedLab's own variables, where set. Nothing else from the environment.</summary>
        private static IEnumerable<(string Name, string Value)> SetKnobs()
        {
            foreach ((string name, string value) in RuntimeKnobs.Set()) yield return (name, Clip(value));

            foreach (string k in SeedLabVariables)
            {
                string? v = Environment.GetEnvironmentVariable(k);
                if (!string.IsNullOrEmpty(v)) yield return (k, Clip(v));
            }
        }

        private static string Clip(string v) => v.Length > 80 ? v.Substring(0, 80) + "..." : v;

        private static void WriteKnobs(Utf8JsonWriter j)
        {
            j.WriteStartObject("knobs");
            foreach ((string name, string value) in SetKnobs()) j.WriteString(name, value);
            j.WriteEndObject();
        }

        private static void WriteCpu(Utf8JsonWriter j, HardwareInfo hw)
        {
            j.WriteStartObject("cpu");
            j.WriteString("vendor", hw.Cpu.Vendor);
            if (hw.Cpu.Family.HasValue) j.WriteNumber("family", hw.Cpu.Family.Value); else j.WriteNull("family");
            if (hw.Cpu.Model.HasValue) j.WriteNumber("model", hw.Cpu.Model.Value); else j.WriteNull("model");
            if (hw.Cpu.Stepping.HasValue) j.WriteNumber("stepping", hw.Cpu.Stepping.Value); else j.WriteNull("stepping");
            j.WriteString("brand", hw.Cpu.Brand);
            if (hw.Cpu.Hybrid.HasValue) j.WriteBoolean("hybrid", hw.Cpu.Hybrid.Value); else j.WriteNull("hybrid");
            j.WriteString("source", hw.Cpu.Source);
            j.WriteString("key", hw.Cpu.Key);
            j.WriteEndObject();
        }

        private static void WriteJson(Utf8JsonWriter j, CliRuntime rt, HardwareInfo hw, SelfTestOutcome st, bool skipAsked, string perlin,
                                      IReadOnlyList<LibmDense.SiteDigest> libm, Row[] rows, string locationNote,
                                      bool locationComparable, int compared, int differ, int libmDiffer, bool pass, string verdict)
        {
            j.WriteStartObject();
            j.WriteString("schema", Schema);
            j.WriteString("program", "vseed " + Verified.EngineVersion);
            j.WriteString("game_version", Verified.GameVersion);
            j.WriteString("os", hw.OSDescription);
            j.WriteString("rid", hw.RuntimeIdentifier);
            j.WriteString("runtime", hw.FrameworkDescription);
            j.WriteString("process_architecture", hw.ProcessArchitecture.ToString());
            j.WriteBoolean("server_gc", GCSettings.IsServerGC);
            j.WriteNumber("logical_cores", hw.LogicalCores);
            WriteCpu(j, hw);
            j.WriteStartObject("isa");
            foreach (KeyValuePair<string, string> kv in SimdDispatch.Facts()) j.WriteString(kv.Key, kv.Value);
            j.WriteEndObject();
            WriteCpuId(j);
            j.WriteString("dispatch_key", SimdDispatch.Key);
            j.WriteString("dispatch_reason", SimdDispatch.Reason);
            WriteKnobs(j);
            j.WriteString("ucrt", hw.UcrtVersion);
            j.WriteString("stamp_key", hw.StampKey);

            j.WriteStartObject("self_test");
            j.WriteString("status", st.Status.ToString());
            if (skipAsked) j.WriteString("note", "--skip-self-test was given; it does not apply to this report, which ran the self-test");
            j.WriteStartArray("suites");
            foreach (SelfTestSuiteResult r in st.Results)
            {
                j.WriteStartObject();
                j.WriteString("name", r.Name);
                j.WriteNumber("checks", r.Checks);
                j.WriteNumber("failures", r.Failures);
                if (r.ShareableFailure.Length > 0) j.WriteString("first_failure", r.ShareableFailure);
                j.WriteEndObject();
            }

            j.WriteEndArray();
            j.WriteString("perlin", perlin);
            j.WriteEndObject();

            j.WriteStartArray("libm_dense");
            foreach (LibmDense.SiteDigest d in libm)
            {
                j.WriteStartObject();
                j.WriteString("site", d.Site);
                j.WriteString("raw", d.Raw);
                j.WriteString("consumed", d.Consumed);
                foreach ((string site, string raw, string consumed) in LibmDense.Recorded)
                {
                    if (site != d.Site) continue;
                    j.WriteBoolean("raw_equal", d.Raw == raw);
                    j.WriteBoolean("consumed_equal", d.Consumed == consumed);
                }

                j.WriteEndObject();
            }

            j.WriteEndArray();

            j.WriteStartObject("fingerprints");
            j.WriteString("locations", locationNote);
            j.WriteBoolean("locations_compared", locationComparable);
            j.WriteStartArray("seeds");
            foreach (Row r in rows)
            {
                j.WriteStartObject();
                j.WriteNumber("seed", r.Seed);
                for (int l = 0; l < 5; l++)
                {
                    if (!r.Computed[l]) continue;
                    j.WriteString(WorldFingerprint.LayerNames[l], r.Layers[l]);
                    if (r.Want != null && (l < 3 || locationComparable))
                    {
                        j.WriteBoolean(WorldFingerprint.LayerNames[l] + "_equal", string.Equals(r.Layers[l], r.Want[l], StringComparison.Ordinal));
                    }
                }

                j.WriteEndObject();
            }

            j.WriteEndArray();
            j.WriteNumber("compared", compared);
            j.WriteNumber("differ", differ);
            j.WriteEndObject();

            j.WriteNumber("libm_dense_differ", libmDiffer);
            j.WriteBoolean("passed", pass);
            j.WriteString("verdict", verdict);
            j.WriteEndObject();
        }

        /// <summary>The recording embedded from the acceptance suite, or null when this build lacks it.</summary>
        private static Reference? LoadReference()
        {
            try
            {
                using Stream? s = typeof(MachineReport).Assembly.GetManifestResourceStream(ReferenceResource);
                if (s == null) return null;
                using JsonDocument doc = JsonDocument.Parse(s);
                JsonElement root = doc.RootElement;
                if (root.GetProperty("schema").GetString() != WorldFingerprint.Schema) return null;
                Reference r = new Reference { DataStamp = root.GetProperty("data").GetString() ?? "" };
                foreach (JsonElement e in root.GetProperty("seeds").EnumerateArray())
                {
                    string[] layers = new string[5];
                    for (int l = 0; l < 5; l++) layers[l] = e.GetProperty(WorldFingerprint.LayerNames[l]).GetString() ?? "";
                    r.Seeds.Add((e.GetProperty("seed").GetInt32(), layers));
                }

                return r;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
