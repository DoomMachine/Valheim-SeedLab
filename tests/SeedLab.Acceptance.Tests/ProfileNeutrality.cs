using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SeedLab.LocationOracle;
using SeedLab.WorldGen.Diagnostics;

namespace SeedLabAcceptanceTests
{
    /// <summary>
    /// Profiling changes no result (PT2), and the phase tree adds up.
    ///
    ///   dotnet run -c Release --project tests\SeedLab.Acceptance.Tests -- --profile-neutrality [--seeds N] [--threads N]
    ///   dotnet run -c Release --project tests\SeedLab.Acceptance.Tests -- --record-fingerprints &lt;file.json&gt; [--threads N]
    ///
    /// <para>The five-layer world fingerprints (<see cref="WorldFingerprint"/>) of the 64 reference seeds
    /// were recorded ONCE, by a build with the profiler's types present and no phase boundary or counter
    /// wired into the generator, at the runtime's default ISA settings, into
    /// <c>WorldFingerprintReference.json</c>. This check recomputes them three ways and requires every
    /// digest to equal the recording:</para>
    /// <list type="number">
    /// <item><b>off</b> - no sink on any worker thread: the generator's boundaries see null. A sink set
    /// on the calling thread meanwhile must record nothing, which proves the sink is per thread;</item>
    /// <item><b>phases</b> - a <see cref="PhaseSink"/> on every worker thread, so every boundary records;</item>
    /// <item><b>phases + counters</b> - the same in a CHILD process started with
    /// <c>SEEDLAB_PROFILE_COUNTERS=1</c>. It has to be a second process: the counter switch is a
    /// static readonly field read once, which is what lets the JIT delete the counter sites from a normal
    /// run, and so it cannot be flipped inside this one. The child asserts the switch took effect, and
    /// that the counters actually counted, rather than trusting the variable's name.</item>
    /// </list>
    ///
    /// <para><b>The tree.</b> With phases on, every seed must also satisfy the structure the phase map
    /// promises, exactly and in integers: each pre-generation step entered once per pre-generation, the
    /// steps' ticks and allocated bytes summing to no more than their parent's, the oracle's six steps
    /// once each, and four constructions and three pre-generations per fingerprint (the lattice handle
    /// defers and never pre-generates; the eager, the deferred-then-forced and the oracle's own handles
    /// each pre-generate once).</para>
    /// </summary>
    public static class ProfileNeutrality
    {
        public const string ReferenceFile = "WorldFingerprintReference.json";

        internal sealed class Outcome
        {
            public List<WorldFingerprint> Prints = new List<WorldFingerprint>();
            public List<string> TreeFailures = new List<string>();
            public long[] CounterTotals = new long[PhaseClock.Capacity];
            public long PregenEntries;
            public double Seconds;
        }

        // ---- the check ------------------------------------------------------------------------------

        public static int Run(string[] args)
        {
            int threads = IntArg(args, "--threads", Math.Max(1, Environment.ProcessorCount / 2));
            int limit = IntArg(args, "--seeds", 64);

            Console.WriteLine("SeedLab profile neutrality (PT2): profiling off, phases on, phases + counters on");
            string refPath = Path.Combine(AppContext.BaseDirectory, ReferenceFile);
            if (!File.Exists(refPath))
            {
                Console.Error.WriteLine("the recorded reference " + refPath + " is missing.");
                return 2;
            }

            List<(int Seed, string[] Layers, bool Stale)> reference = ReadReference(refPath, out string stamp);
            if (limit < reference.Count) reference = reference.GetRange(0, Math.Max(1, limit));
            int[] seeds = new int[reference.Count];
            for (int i = 0; i < seeds.Length; i++) seeds[i] = reference[i].Seed;
            Console.WriteLine("  reference      " + ReferenceFile + " (data " + stamp + "), " + seeds.Length + " seeds");
            Console.WriteLine("  threads        " + threads);
            Console.WriteLine("  counters       " + (PhaseClock.CountersOn ? "ON in this process (unexpected)" : "off in this process, as they must be"));

            int failures = 0;
            if (PhaseClock.CountersOn)
            {
                Console.WriteLine("FAIL  SEEDLAB_PROFILE_COUNTERS is set in the parent; unset it - the parent is the 'off' leg.");
                failures++;
            }

            using WorldFingerprinter fp = WorldFingerprinter.Open();
            if (!string.Equals(fp.DataStamp, stamp, StringComparison.Ordinal))
            {
                Console.WriteLine("FAIL  the reference was recorded on data " + stamp + " and this is " + fp.DataStamp);
                return 1;
            }

            // 1. off. The workers never touch PhaseSink.Current, so they see a new thread's default. A
            // sink set on THIS thread meanwhile must record nothing: if Current were ever shared between
            // threads rather than thread-static, every worker's boundaries would land in it, and "off"
            // would not be off.
            PhaseSink spy = new PhaseSink();
            PhaseSink.Current = spy;
            Outcome off;
            try
            {
                off = Compute(fp, seeds, threads, withSinks: false);
            }
            finally
            {
                PhaseSink.Current = null;
            }

            failures += Compare("off", off, reference);
            long[] spied = new long[PhaseSink.SnapshotLength];
            spy.Snapshot(spied);
            long spiedEntries = 0;
            for (int i = PhaseSink.EntriesOffset; i < PhaseSink.AllocOffset; i++) spiedEntries += spied[i];
            if (spiedEntries != 0)
            {
                Console.WriteLine("FAIL  off: a sink set on the calling thread recorded " + spiedEntries
                                  + " boundaries from the workers - PhaseSink.Current is not per thread");
                failures++;
            }
            else
            {
                Console.WriteLine("PASS  off: a sink set on the calling thread recorded nothing from the " + threads
                                  + " worker thread(s) (Current is per thread)");
            }

            // 2. phases on
            Outcome phases = Compute(fp, seeds, threads, withSinks: true);
            failures += Compare("phases", phases, reference);
            failures += Tree("phases", phases, seeds.Length);

            // 3. phases + counters, in a child process
            failures += ChildLeg(seeds.Length, threads, reference);

            Console.WriteLine();
            Console.WriteLine((failures == 0 ? "PASS" : "FAIL") + "  profile neutrality: "
                              + (failures == 0 ? "every digest of every layer equal in all three legs, the tree adds up"
                                               : failures + " failure(s)"));
            return failures == 0 ? 0 : 1;
        }

        internal static int Compare(string leg, Outcome o, List<(int Seed, string[] Layers, bool Stale)> reference)
        {
            int bad = 0;
            for (int i = 0; i < reference.Count; i++)
            {
                WorldFingerprint f = o.Prints[i];
                if (f.Seed != reference[i].Seed)
                {
                    Console.WriteLine("FAIL  " + leg + ": seed order " + f.Seed + " != " + reference[i].Seed);
                    bad++;
                    continue;
                }

                for (int l = 0; l < 5; l++)
                {
                    if (!string.Equals(f.Layer(l), reference[i].Layers[l], StringComparison.Ordinal))
                    {
                        Console.WriteLine("FAIL  " + leg + ": seed " + f.Seed + " " + WorldFingerprint.LayerNames[l]
                                          + " " + f.Layer(l) + " != recorded " + reference[i].Layers[l]);
                        bad++;
                    }
                }

                if (!f.DeferredMatchesEager)
                {
                    Console.WriteLine("FAIL  " + leg + ": seed " + f.Seed + " deferred pre-generation differs from eager");
                    bad++;
                }

                if (f.RiverCacheStale != reference[i].Stale)
                {
                    Console.WriteLine("FAIL  " + leg + ": seed " + f.Seed + " river cache staleness changed");
                    bad++;
                }
            }

            Console.WriteLine((bad == 0 ? "PASS" : "FAIL") + "  " + leg.PadRight(22) + reference.Count + " seeds x 5 layers"
                              + (bad == 0 ? " identical to the recording" : ": " + bad + " difference(s)")
                              + "  (" + o.Seconds.ToString("F1", CultureInfo.InvariantCulture) + " s)");
            return bad;
        }

        private static int Tree(string leg, Outcome o, int seeds)
        {
            foreach (string f in o.TreeFailures) Console.WriteLine("FAIL  " + leg + " tree: " + f);
            bool recorded = o.PregenEntries == 3L * seeds;
            if (!recorded)
            {
                Console.WriteLine("FAIL  " + leg + ": the sinks recorded " + o.PregenEntries + " pre-generations, "
                                  + "expected " + (3L * seeds) + " - phases were not on");
            }

            int bad = o.TreeFailures.Count + (recorded ? 0 : 1);
            Console.WriteLine((bad == 0 ? "PASS" : "FAIL") + "  " + (leg + " tree").PadRight(22) + seeds
                              + " seeds: pre-generation's nine steps once each and within it, the oracle's six once each,"
                              + " 4 constructions and 3 pre-generations per seed");
            return bad;
        }

        private static int ChildLeg(int seeds, int threads, List<(int Seed, string[] Layers, bool Stale)> reference)
        {
            string exe = Environment.ProcessPath ?? "";
            ProcessStartInfo psi = new ProcessStartInfo
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };

            // Under 'dotnet SeedLab.Acceptance.Tests.dll' the process is dotnet itself; under 'dotnet run'
            // or the apphost it is the test executable.
            if (string.Equals(Path.GetFileNameWithoutExtension(exe), "dotnet", StringComparison.OrdinalIgnoreCase))
            {
                psi.FileName = exe;
                psi.ArgumentList.Add(typeof(ProfileNeutrality).Assembly.Location);
            }
            else
            {
                psi.FileName = exe;
            }

            psi.ArgumentList.Add("--fingerprint-child");
            psi.ArgumentList.Add("--seeds");
            psi.ArgumentList.Add(seeds.ToString(CultureInfo.InvariantCulture));
            psi.ArgumentList.Add("--threads");
            psi.ArgumentList.Add(threads.ToString(CultureInfo.InvariantCulture));
            psi.Environment[PhaseClock.EnvironmentVariable] = "1";

            Stopwatch sw = Stopwatch.StartNew();
            using Process p = Process.Start(psi) ?? throw new InvalidOperationException("the child did not start");
            Task<string> err = p.StandardError.ReadToEndAsync();
            string stdout = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            sw.Stop();
            if (p.ExitCode != 0)
            {
                Console.WriteLine("FAIL  counters: the child exited " + p.ExitCode + ": " + err.Result.Trim());
                return 1;
            }

            using JsonDocument doc = JsonDocument.Parse(stdout);
            JsonElement root = doc.RootElement;
            int bad = 0;
            if (!root.GetProperty("counters_on").GetBoolean())
            {
                Console.WriteLine("FAIL  counters: SEEDLAB_PROFILE_COUNTERS=1 had no effect in the child");
                bad++;
            }

            long baseHeights = root.GetProperty("counters").GetProperty(PhaseClock.Name(Counter.BaseHeight)).GetInt64();
            long pregen = root.GetProperty("pregen_entries").GetInt64();
            if (baseHeights <= 0 || pregen != 3L * seeds)
            {
                Console.WriteLine("FAIL  counters: the child counted " + baseHeights + " base heights and "
                                  + pregen + " pre-generations - counters or phases were not really on");
                bad++;
            }

            Outcome o = new Outcome { Seconds = sw.Elapsed.TotalSeconds };
            foreach (JsonElement r in root.GetProperty("results").EnumerateArray())
            {
                o.Prints.Add(FromJson(r));
            }

            foreach (JsonElement t in root.GetProperty("tree_failures").EnumerateArray()) o.TreeFailures.Add(t.GetString() ?? "");
            o.PregenEntries = pregen;
            bad += Compare("phases+counters", o, reference);
            bad += Tree("phases+counters", o, seeds);
            if (bad == 0)
            {
                Console.WriteLine("      counters in the child: " + baseHeights.ToString("N0", CultureInfo.InvariantCulture)
                                  + " base heights, " + root.GetProperty("counters").GetProperty(PhaseClock.Name(Counter.WorldAngle)).GetInt64().ToString("N0", CultureInfo.InvariantCulture)
                                  + " world angles over " + seeds + " fingerprints");
            }

            return bad;
        }

        // ---- the child ------------------------------------------------------------------------------

        public static int Child(string[] args)
        {
            int threads = IntArg(args, "--threads", 1);
            int limit = IntArg(args, "--seeds", 64);
            int[] all = WorldFingerprinter.ReferenceSeeds();
            int[] seeds = all.AsSpan(0, Math.Min(limit, all.Length)).ToArray();
            using WorldFingerprinter fp = WorldFingerprinter.Open();
            Outcome o = Compute(fp, seeds, threads, withSinks: true);

            using MemoryStream ms = new MemoryStream();
            using (Utf8JsonWriter j = new Utf8JsonWriter(ms))
            {
                j.WriteStartObject();
                j.WriteBoolean("counters_on", PhaseClock.CountersOn);
                j.WriteNumber("pregen_entries", o.PregenEntries);
                j.WriteStartObject("counters");
                foreach (Counter c in PhaseClock.All) j.WriteNumber(PhaseClock.Name(c), o.CounterTotals[(int)c]);
                j.WriteEndObject();
                j.WriteStartArray("tree_failures");
                foreach (string f in o.TreeFailures) j.WriteStringValue(f);
                j.WriteEndArray();
                j.WriteStartArray("results");
                foreach (WorldFingerprint f in o.Prints) WriteJson(j, f, null);
                j.WriteEndArray();
                j.WriteEndObject();
            }

            Console.Out.Write(Encoding.UTF8.GetString(ms.ToArray()));
            return 0;
        }

        // ---- recording ------------------------------------------------------------------------------

        public static int Record(string[] args)
        {
            if (args.Length < 2 || args[1].StartsWith("-", StringComparison.Ordinal))
            {
                Console.Error.WriteLine("usage: --record-fingerprints <file.json> [--threads N]");
                return 2;
            }

            string path = Path.GetFullPath(args[1]);
            int threads = IntArg(args, "--threads", Math.Max(1, Environment.ProcessorCount / 2));
            if (PhaseClock.CountersOn)
            {
                Console.Error.WriteLine("refusing to record with SEEDLAB_PROFILE_COUNTERS set: a reference is recorded with everything off.");
                return 2;
            }

            int[] seeds = WorldFingerprinter.ReferenceSeeds();
            using WorldFingerprinter fp = WorldFingerprinter.Open();
            Console.WriteLine("recording " + seeds.Length + " world fingerprints on " + threads + " threads (data " + fp.DataStamp + ")");
            Outcome o = Compute(fp, seeds, threads, withSinks: false);
            foreach (WorldFingerprint f in o.Prints)
            {
                if (!f.DeferredMatchesEager)
                {
                    Console.Error.WriteLine("seed " + f.Seed + ": deferred pre-generation differs from eager - not recording.");
                    return 1;
                }
            }

            using MemoryStream ms = new MemoryStream();
            using (Utf8JsonWriter j = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
            {
                j.WriteStartObject();
                j.WriteString("schema", WorldFingerprint.Schema);
                j.WriteString("recorded_utc", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
                j.WriteString("data", fp.DataStamp);
                j.WriteNumber("world_gen_version", WorldFingerprinter.WorldGenVersion);
                j.WriteString("recorded_with", "the profiler's types present and nothing wired to them; runtime ISA settings at their defaults");
                j.WriteStartObject("layers");
                foreach (string l in WorldFingerprint.LayerNames) j.WriteString(l, WorldFingerprint.Describe(l));
                j.WriteEndObject();
                j.WriteStartArray("seeds");
                for (int i = 0; i < o.Prints.Count; i++) WriteJson(j, o.Prints[i], WorldFingerprinter.SourceOf(i));
                j.WriteEndArray();
                j.WriteEndObject();
            }

            byte[] bytes = ms.ToArray();
            string text = Encoding.UTF8.GetString(bytes).Replace("\r\n", "\n") + "\n";
            File.WriteAllText(path, text, new UTF8Encoding(false));
            Console.WriteLine("wrote " + path + " in " + o.Seconds.ToString("F1", CultureInfo.InvariantCulture) + " s");
            return 0;
        }

        // ---- the work -------------------------------------------------------------------------------

        internal static Outcome Compute(WorldFingerprinter fp, int[] seeds, int threads, bool withSinks)
        {
            Outcome o = new Outcome();
            WorldFingerprint?[] prints = new WorldFingerprint?[seeds.Length];
            List<string> tree = new List<string>();
            long[] counters = new long[PhaseClock.Capacity];
            long pregen = 0;
            int next = -1;
            Exception? fault = null;
            object gate = new object();

            Stopwatch sw = Stopwatch.StartNew();
            Thread[] workers = new Thread[Math.Max(1, Math.Min(threads, seeds.Length))];
            for (int w = 0; w < workers.Length; w++)
            {
                workers[w] = new Thread(() =>
                {
                    // With sinks off, Current is left exactly as a new thread has it: the off leg proves
                    // that default, rather than a null this code wrote itself.
                    PhaseSink? sink = withSinks ? new PhaseSink() : null;
                    if (withSinks) PhaseSink.Current = sink;
                    PhaseClock.ResetCounters();
                    long[] before = new long[PhaseSink.SnapshotLength];
                    long[] after = new long[PhaseSink.SnapshotLength];
                    try
                    {
                        int i;
                        while ((i = Interlocked.Increment(ref next)) < seeds.Length)
                        {
                            sink?.Snapshot(before);
                            prints[i] = fp.Compute(seeds[i]);
                            if (sink == null) continue;
                            sink.Snapshot(after);
                            List<string> f = CheckTree(seeds[i], before, after);
                            lock (gate)
                            {
                                tree.AddRange(f);
                                pregen += after[PhaseSink.EntriesOffset + (int)Phase.Pregen]
                                          - before[PhaseSink.EntriesOffset + (int)Phase.Pregen];
                            }
                        }

                        Span<long> mine = stackalloc long[PhaseClock.Capacity];
                        PhaseClock.SnapshotCounters(mine);
                        lock (gate)
                        {
                            for (int c = 0; c < PhaseClock.Capacity; c++) counters[c] += mine[c];
                        }
                    }
                    catch (Exception ex)
                    {
                        lock (gate) fault ??= ex;
                    }
                    finally
                    {
                        if (withSinks) PhaseSink.Current = null;
                    }
                })
                {
                    IsBackground = true,
                    Name = "fingerprint-" + w,
                };
                workers[w].Start();
            }

            foreach (Thread t in workers) t.Join();
            sw.Stop();
            if (fault != null) throw new InvalidOperationException("a fingerprint worker failed", fault);

            foreach (WorldFingerprint? f in prints) o.Prints.Add(f!);
            o.TreeFailures = tree;
            o.CounterTotals = counters;
            o.PregenEntries = pregen;
            o.Seconds = sw.Elapsed.TotalSeconds;
            return o;
        }

        /// <summary>The structure one fingerprint's phases must have. Integers only; no tolerance.</summary>
        private static List<string> CheckTree(int seed, long[] before, long[] after)
        {
            List<string> f = new List<string>();
            long T(Phase p) => after[(int)p] - before[(int)p];
            long E(Phase p) => after[PhaseSink.EntriesOffset + (int)p] - before[PhaseSink.EntriesOffset + (int)p];
            long A(Phase p) => after[PhaseSink.AllocOffset + (int)p] - before[PhaseSink.AllocOffset + (int)p];

            if (E(Phase.Construct) != 4) f.Add("seed " + seed + ": construct entered " + E(Phase.Construct) + " times, expected 4");
            if (E(Phase.Pregen) != 3) f.Add("seed " + seed + ": pregen entered " + E(Phase.Pregen) + " times, expected 3");

            long sumT = 0, sumA = 0;
            foreach (Phase c in PhaseMap.PregenChildren)
            {
                if (E(c) != E(Phase.Pregen)) f.Add("seed " + seed + ": " + PhaseMap.Name(c) + " entered " + E(c) + " times, pregen " + E(Phase.Pregen));
                sumT += T(c);
                sumA += A(c);
            }

            if (sumT > T(Phase.Pregen)) f.Add("seed " + seed + ": pregen's steps took " + sumT + " ticks, more than pregen's own " + T(Phase.Pregen));
            if (sumA > A(Phase.Pregen)) f.Add("seed " + seed + ": pregen's steps allocated " + sumA + " bytes, more than pregen's own " + A(Phase.Pregen));

            foreach (Phase c in PhaseMap.OracleChildren)
            {
                if (E(c) != 1) f.Add("seed " + seed + ": " + PhaseMap.Name(c) + " entered " + E(c) + " times, expected 1");
            }

            foreach (Phase p in PhaseMap.All)
            {
                if (T(p) < 0 || A(p) < 0) f.Add("seed " + seed + ": " + PhaseMap.Name(p) + " went negative");
            }

            return f;
        }

        // ---- JSON -----------------------------------------------------------------------------------

        internal static void WriteJson(Utf8JsonWriter j, WorldFingerprint f, string? source)
        {
            j.WriteStartObject();
            j.WriteNumber("seed", f.Seed);
            if (source != null) j.WriteString("source", source);
            for (int l = 0; l < 5; l++) j.WriteString(WorldFingerprint.LayerNames[l], f.Layer(l));
            j.WriteBoolean("river_cache_stale", f.RiverCacheStale);
            j.WriteBoolean("deferred_matches_eager", f.DeferredMatchesEager);
            j.WriteNumber("instances", f.Instances);
            j.WriteNumber("oracle_hits", f.OracleHits);
            j.WriteEndObject();
        }

        internal static WorldFingerprint FromJson(JsonElement r)
        {
            string[] layers = new string[5];
            for (int l = 0; l < 5; l++) layers[l] = r.GetProperty(WorldFingerprint.LayerNames[l]).GetString() ?? "";
            return WorldFingerprint.FromRecorded(r.GetProperty("seed").GetInt32(), layers,
                                               r.GetProperty("river_cache_stale").GetBoolean(),
                                               r.GetProperty("deferred_matches_eager").GetBoolean(),
                                               r.GetProperty("instances").GetInt32(),
                                               r.GetProperty("oracle_hits").GetInt32());
        }

        internal static List<(int Seed, string[] Layers, bool Stale)> ReadReference(string path, out string stamp)
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllBytes(path));
            JsonElement root = doc.RootElement;
            if (root.GetProperty("schema").GetString() != WorldFingerprint.Schema)
            {
                throw new InvalidDataException(path + " is not a " + WorldFingerprint.Schema + " file.");
            }

            stamp = root.GetProperty("data").GetString() ?? "";
            List<(int, string[], bool)> list = new List<(int, string[], bool)>();
            foreach (JsonElement r in root.GetProperty("seeds").EnumerateArray())
            {
                string[] layers = new string[5];
                for (int l = 0; l < 5; l++) layers[l] = r.GetProperty(WorldFingerprint.LayerNames[l]).GetString() ?? "";
                list.Add((r.GetProperty("seed").GetInt32(), layers, r.GetProperty("river_cache_stale").GetBoolean()));
            }

            return list;
        }

        internal static int IntArg(string[] args, string name, int fallback)
        {
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == name && int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) && v > 0)
                {
                    return v;
                }
            }

            return fallback;
        }
    }
}
