using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using SeedLab.Search.Criteria;
using SeedLab.Search.Evaluation;
using SeedLab.Search.Execution;
using SeedLab.Search.Locations;
using SeedLab.Search.Output;

namespace Proof
{
    /// <summary>
    /// Proof harness for the search engine's safety work. Every number it prints it measured.
    /// Subcommands: bounded, memory, run (for the kill test), resume, rotate, refuse, policy, region.
    /// </summary>
    public static class Program
    {
        private const string Engine = "0.1.0-proof";

        public static int Main(string[] args)
        {
            if (args.Length == 0) { Console.Error.WriteLine("usage: proof <bounded|memory|run|rotate|refuse|policy|region|estimate>"); return 2; }
            switch (args[0])
            {
                case "bounded": return Bounded(args);
                case "memory": return Memory(args);
                case "run": return RunLeg(args);
                case "rotate": return Rotate(args);
                case "refuse": return Refuse(args);
                case "policy": return Policy(args);
                case "region": return Region(args);
                case "estimate": return Estimate(args);
                case "screen": return Screen(args);
                default: Console.Error.WriteLine("unknown: " + args[0]); return 2;
            }
        }

        // A cheap biome query: every seed is measured on a small disc, so a leg runs in seconds.
        private static Query Q(string keep, double grid = 96, double radius = 2000, string test = "at_least",
                               double value = 1, string importance = "nice")
        {
            string json = @"{
              ""version"": 1, ""defs"": 1, ""name"": ""proof"",
              ""search"": { ""order"": ""shuffled"", ""key"": ""0x5EEDF00D1234ABCD"", ""grid"": " + grid.ToString(CultureInfo.InvariantCulture) + @",
                            ""keep"": " + keep + @", ""block_size"": 64, ""screen"": ""off"" },
              ""goals"": [
                { ""id"": ""meadows"", ""target"": ""biome:Meadows"", ""metric"": ""area_within"",
                  ""radius"": " + radius.ToString(CultureInfo.InvariantCulture) + @",
                  ""test"": """ + test + @""", ""value"": " + value.ToString(CultureInfo.InvariantCulture) + @",
                  ""importance"": """ + importance + @""" }
              ]
            }";
            return QueryReader.Parse(json, "proof");
        }

        private static ILocationOracle Oracle => UnavailableLocationOracle.Instance;

        // ---- 1. --keep is a real cap ---------------------------------------------------------------
        private static int Bounded(string[] args)
        {
            long seeds = Arg(args, "--seeds", 20000);
            int keep = (int)Arg(args, "--keep", 50);
            string dir = Dir(args);
            Directory.CreateDirectory(dir);

            string capped = Path.Combine(dir, "capped.jsonl");
            string all = Path.Combine(dir, "all.jsonl");
            foreach (string f in new[] { capped, all }) if (File.Exists(f)) File.Delete(f);

            SearchOutcome a = Once(Q(keep.ToString(CultureInfo.InvariantCulture)), capped, seeds, 8);
            SearchOutcome b = Once(Q("\"all\""), all, seeds, 8);

            long cappedLines = CountLines(capped), allLines = CountLines(all);
            Console.WriteLine("seeds scanned            " + a.Evaluated.ToString("N0", CultureInfo.InvariantCulture));
            Console.WriteLine("matches (true total)     " + a.Passed.ToString("N0", CultureInfo.InvariantCulture)
                              + " / keep-all file " + allLines.ToString("N0", CultureInfo.InvariantCulture));
            Console.WriteLine("bounded file records     " + cappedLines + "  (keep = " + keep + ")");
            Console.WriteLine("bounded file bytes       " + new FileInfo(capped).Length.ToString("N0", CultureInfo.InvariantCulture)
                              + "   keep-all bytes " + new FileInfo(all).Length.ToString("N0", CultureInfo.InvariantCulture));
            Console.WriteLine("report line              " + a.Passed.ToString("N0", CultureInfo.InvariantCulture)
                              + " -> \"top " + a.ResultsWritten + " of " + a.Passed + "\"");

            // The kept set must be exactly the top N of the unbounded file, in the same order.
            List<string> keptSeeds = SeedOrder(capped);
            List<string> topOfAll = TopOf(all, keep);
            bool same = keptSeeds.Count == topOfAll.Count;
            for (int i = 0; same && i < keptSeeds.Count; i++) same = keptSeeds[i] == topOfAll[i];
            Console.WriteLine("kept set == top-N of all " + (same ? "YES (identical order, seed for seed)" : "NO"));
            return same && cappedLines == keep ? 0 : 1;
        }

        // ---- 2. memory is bounded ------------------------------------------------------------------
        private static int Memory(string[] args)
        {
            double seconds = Arg(args, "--seconds", 60);
            string dir = Dir(args);
            Directory.CreateDirectory(dir);
            string outPath = Path.Combine(dir, "mem.jsonl");
            if (File.Exists(outPath)) File.Delete(outPath);

            Query q = Q("1000", 96, 1000);
            q.Search.BlockSize = 64;
            SearchSession s = SearchSession.Create(q, Oracle, Engine, 0, 16, outPath, acceptScanOrder: true);
            IResultSink? sink = s.Start(resume: false);

            Process me = Process.GetCurrentProcess();
            long peakWs = 0;
            int peakPending = 0;
            Timer t = new Timer(_ =>
            {
                me.Refresh();
                long ws = me.WorkingSet64;
                if (ws > peakWs) peakWs = ws;
            }, null, 1000, 1000);

            SearchOutcome o = s.Run(sink, p => { if (p.PendingBlocks > peakPending) peakPending = p.PendingBlocks; },
                                    TimeSpan.FromSeconds(seconds), TimeSpan.FromSeconds(10));
            t.Dispose();
            sink?.Finish();
            sink?.Dispose();

            Console.WriteLine("whole-space plan blocks  " + s.Plan.Blocks.ToString("N0", CultureInfo.InvariantCulture)
                              + " of " + s.Plan.BlockSize + " seeds");
            Console.WriteLine("ran                      " + o.Seconds.ToString("0.0", CultureInfo.InvariantCulture)
                              + " s, " + o.Evaluated.ToString("N0", CultureInfo.InvariantCulture) + " seeds, "
                              + o.SeedsPerSecond.ToString("N0", CultureInfo.InvariantCulture) + " seeds/s");
            Console.WriteLine("matches                  " + o.Passed.ToString("N0", CultureInfo.InvariantCulture)
                              + ", kept " + o.ResultsWritten);
            Console.WriteLine("peak working set         " + (peakWs / 1048576.0).ToString("N0", CultureInfo.InvariantCulture) + " MB");
            Console.WriteLine("peak pending blocks      " + o.PeakPendingBlocks + " (bound "
                              + RunOptions.DefaultPendingBlocks(16) + ")");
            Console.WriteLine("results file             " + new FileInfo(outPath).Length.ToString("N0", CultureInfo.InvariantCulture) + " B");
            return 0;
        }

        // ---- 3. one leg of a killable run ----------------------------------------------------------
        private static int RunLeg(string[] args)
        {
            string dir = Dir(args);
            Directory.CreateDirectory(dir);
            string outPath = Path.Combine(dir, "run.jsonl");
            string ckpt = Path.Combine(dir, "run.ckpt");
            bool resume = Has(args, "--resume");
            long seeds = Arg(args, "--seeds", 40000);
            int threads = (int)Arg(args, "--threads", 8);

            if (!resume)
            {
                foreach (string f in new[] { outPath, ckpt, ckpt + ".top" }) if (File.Exists(f)) File.Delete(f);
            }

            Query q = Q(Has(args, "--keep-all") ? "\"all\"" : "100", 96, 2000);
            SearchSession s = SearchSession.Create(q, Oracle, Engine, seeds, threads, outPath, acceptScanOrder: true);
            IResultSink? sink = s.Start(resume, ckpt);
            if (s.RepairedBytes > 0)
            {
                Console.WriteLine("repaired torn tail       " + s.RepairedBytes.ToString("N0", CultureInfo.InvariantCulture)
                                  + " B discarded back to the checkpoint's results_length");
            }

            SearchOutcome o = s.Run(sink, null, TimeSpan.Zero, TimeSpan.FromSeconds(Arg(args, "--ckpt-every", 2)));
            sink?.Finish();
            sink?.Dispose();
            Console.WriteLine("leg done  complete=" + o.Complete + "  seeds=" + o.Evaluated
                              + "  matches=" + o.Passed + "  kept=" + o.ResultsWritten
                              + "  ckpt=" + (o.CheckpointPath ?? "(retired)"));
            return 0;
        }

        // ---- 4. rotation, manifest, reduce ----------------------------------------------------------
        private static int Rotate(string[] args)
        {
            string dir = Dir(args);
            Directory.CreateDirectory(dir);
            foreach (string f in Directory.GetFiles(dir, "seg*")) File.Delete(f);
            string outPath = Path.Combine(dir, Has(args, "--csv") ? "seg.csv" : "seg.jsonl");

            Query q = Q("\"all\"", 96, 1000);
            q.Output.RotateBytes = Arg(args, "--rotate", 200000);
            q.Output.Compress = true;
            q.Output.Reduce = Args(args, "--reduce", "none");
            SearchSession s = SearchSession.Create(q, Oracle, Engine, Arg(args, "--seeds", 4000), 8, outPath,
                                                   acceptScanOrder: true);
            IResultSink? sink = s.Start(resume: false);
            SearchOutcome o = s.Run(sink, null, TimeSpan.Zero, TimeSpan.FromSeconds(2));
            sink?.Finish();
            sink?.Dispose();

            SegmentedResultSink seg = (SegmentedResultSink)sink!;
            Console.WriteLine("records written          " + o.Passed.ToString("N0", CultureInfo.InvariantCulture));
            Console.WriteLine("segments                 " + seg.Segments.Count);
            long onDisk = 0, raw = 0;
            foreach (SegmentInfo si in seg.Segments)
            {
                onDisk += si.FileBytes;
                raw += si.RawBytes;
                Console.WriteLine("  " + si.File + "  records " + si.Records + "  raw "
                                  + si.RawBytes.ToString("N0", CultureInfo.InvariantCulture) + " B  file "
                                  + si.FileBytes.ToString("N0", CultureInfo.InvariantCulture) + " B"
                                  + (si.ReducedBy != null ? "  reduced " + si.ReducedBy + " -> " + si.ReducedRecords
                                                            + " records, raw segment deleted" : "")
                                  + "  sha " + si.Sha256.Substring(0, 12));
            }

            Console.WriteLine("raw " + raw.ToString("N0", CultureInfo.InvariantCulture) + " B -> on disk "
                              + onDisk.ToString("N0", CultureInfo.InvariantCulture) + " B  ("
                              + (raw > 0 ? (raw / (double)Math.Max(1, onDisk)).ToString("0.0", CultureInfo.InvariantCulture) : "-")
                              + "x)");
            Console.WriteLine("manifest                 " + Path.GetFileName(seg.ManifestPath) + "  "
                              + new FileInfo(seg.ManifestPath).Length + " B");

            // Every closed gz segment must be readable to the end - the multi-member flush must not
            // have left a cut deflate stream.
            int checkedSegments = 0;
            foreach (SegmentInfo si in seg.Segments)
            {
                string p = Path.Combine(dir, si.File);
                if (!p.EndsWith(".gz", StringComparison.Ordinal) || !File.Exists(p)) continue;
                using FileStream fs = File.OpenRead(p);
                using System.IO.Compression.GZipStream gz =
                    new System.IO.Compression.GZipStream(fs, System.IO.Compression.CompressionMode.Decompress);
                using StreamReader sr = new StreamReader(gz);
                long n = 0;
                while (sr.ReadLine() != null) n++;
                // A CSV segment carries its own header line, so it holds records + 1 lines. That is
                // the point of the header being there: every segment is a file someone opens alone.
                long expected = si.Records + (outPath.EndsWith(".csv", StringComparison.Ordinal) ? 1 : 0);
                Console.WriteLine("  decompressed " + si.File + " -> " + n + " lines (expected " + expected + ")");
                if (n != expected) return 1;
                checkedSegments++;
            }

            Console.WriteLine("gz segments verified     " + checkedSegments);
            return 0;
        }

        // ---- 5. the refusals ------------------------------------------------------------------------
        private static int Refuse(string[] args)
        {
            int failures = 0;

            // (a) non-mergeable reduction
            failures += Expect("reduce median:score", () => ReduceSpec.Parse("median:score"));
            failures += Expect("reduce p95:score", () => ReduceSpec.Parse("p95:score"));
            Console.WriteLine("  mergeable top:100       -> " + ReduceSpec.Parse("top:100").Text);
            Console.WriteLine("  mergeable count         -> " + ReduceSpec.Parse("count").Text);

            // (b) rotate + json array
            OutputPolicy p = new OutputPolicy { Path = "r.json", Format = ResultFormat.Json, KeepAll = true, RotateBytes = 1 << 20 };
            failures += Expect("rotate with --format json", () => p.Validate());

            // (c) the refusal rule: all must-haves, nothing to rank by
            Query q = Q("1000", 96, 2000, "at_least", 1000, "must");
            CompiledQuery cq = CompiledQuery.Compile(q, Oracle);
            ScanPlan plan = new ScanPlan(q.Search.Order, q.Search.From, q.Search.To, 1, 64, 10000);
            SearchPreflight pf = SearchPreflightCheck.Check(q, cq, plan, new OutputPolicy { Path = "r.jsonl", Keep = 1000 }, 8);
            Console.WriteLine("  all-musts bounded run   -> " + (pf.Ok ? "ALLOWED (wrong)" : "refused"));
            if (pf.Ok) failures++;
            else Console.WriteLine("     " + pf.Refusals[0]);

            SearchPreflight ok = SearchPreflightCheck.Check(q, cq, plan,
                new OutputPolicy { Path = "r.jsonl", Keep = 1000 }, 8, acceptScanOrder: true);
            Console.WriteLine("  with --accept-scan-order-> " + (ok.Ok ? "allowed" : "refused (wrong)"));
            if (!ok.Ok) failures++;

            // (c2) a rotated run refuses to resume: the alternative is overwriting segment 0001
            OutputPolicy rot = new OutputPolicy
            {
                Path = Path.Combine(Path.GetTempPath(), "seedlab-proof-rotate", "r.jsonl"),
                Format = ResultFormat.Jsonl,
                KeepAll = true,
                RotateBytes = 1 << 20,
            };
            Query rq = Q("\"all\"", 96, 1000);
            CompiledQuery rcq = CompiledQuery.Compile(rq, Oracle);
            try
            {
                ResultSinks.Create(rot, rq, rcq, Engine, resumeLength: 1024);
                Console.WriteLine("  resume a rotated run    -> ALLOWED (wrong: it would overwrite segment 0001)");
                failures++;
            }
            catch (InvalidOperationException ex)
            {
                Console.WriteLine("  resume a rotated run    -> refused: " + ex.Message.Split('.')[0]);
            }

            // ...and a FRESH rotated run is still allowed.
            using (IResultSink fresh = ResultSinks.Create(rot, rq, rcq, Engine, resumeLength: -1))
            {
                Console.WriteLine("  fresh rotated run       -> allowed (" + fresh.GetType().Name + ")");
            }

            // (d) zero must-have goals warns before the run
            Query nice = Q("1000", 96, 2000);
            CompiledQuery cn = CompiledQuery.Compile(nice, Oracle);
            SearchPreflight w = SearchPreflightCheck.Check(nice, cn, plan,
                new OutputPolicy { Path = "r.jsonl", KeepAll = true }, 8);
            bool warned = false;
            foreach (string s in w.Warnings) if (s.Contains("no must-have goal")) warned = true;
            Console.WriteLine("  no must-have warning    -> " + (warned ? "present" : "MISSING"));
            if (!warned) failures++;
            foreach (string s in w.Warnings) Console.WriteLine("     ! " + s);
            foreach (string s in w.Confirmations) Console.WriteLine("     ? " + s);
            return failures == 0 ? 0 : 1;
        }

        private static int Expect(string what, Action a)
        {
            try
            {
                a();
                Console.WriteLine("  " + what + " -> ALLOWED (wrong)");
                return 1;
            }
            catch (QueryException ex)
            {
                Console.WriteLine("  " + what + " -> refused: " + ex.Message);
                return 0;
            }
        }

        private static int Expect(string what, Func<object> f) => Expect(what, () => { f(); });

        // ---- 6. the grid policy and the presets -----------------------------------------------------
        private static int Policy(string[] args)
        {
            foreach (string name in Presets.Names)
            {
                Query q;
                try { q = Presets.Load(name); }
                catch (Exception ex) { Console.WriteLine(name + ": " + ex.Message); continue; }

                CompiledQuery cq = CompiledQuery.Compile(q, Oracle);
                GridPlan gp = GridPolicy.AutoPick(cq, q.Search.ScreenGrid > 0 ? q.Search.ScreenGrid : 24.0);
                Console.WriteLine(name.PadRight(22) + " grid " + q.Search.Grid.ToString("0.#", CultureInfo.InvariantCulture).PadLeft(5)
                                  + "  " + gp.Describe()
                                  + (gp.UnsafeMusts.Count > 0 ? "   UNSAFE MUSTS: " + gp.UnsafeMusts.Count : ""));
                foreach (string u in gp.UnsafeMusts) Console.WriteLine("      ! " + u);
            }

            return 0;
        }

        // ---- 7. region restriction cost --------------------------------------------------------------
        private static int Region(string[] args)
        {
            int n = (int)Arg(args, "--seeds", 16);
            foreach (double radius in new double[] { 1000, 2000, 5000, 10500 })
            {
                Query q = Q("1000", 12, radius);
                CompiledQuery cq = CompiledQuery.Compile(q, Oracle);
                SeedEvaluator ev = new SeedEvaluator(cq, Oracle);
                ev.Evaluate(1);
                Stopwatch sw = Stopwatch.StartNew();
                for (int i = 0; i < n; i++) ev.Evaluate(1000 + i);
                sw.Stop();
                double ms = sw.Elapsed.TotalMilliseconds / n;
                Console.WriteLine("radius " + radius.ToString("N0", CultureInfo.InvariantCulture).PadLeft(7) + " m   "
                                  + ms.ToString("0.00", CultureInfo.InvariantCulture).PadLeft(9) + " ms/seed   predicted speed-up "
                                  + RunEstimator.RegionSpeedup(radius).ToString("0.0", CultureInfo.InvariantCulture) + "x");
            }

            return 0;
        }

        // ---- 8. the estimate, against what really happened -------------------------------------------
        private static int Estimate(string[] args)
        {
            string dir = Dir(args);
            Directory.CreateDirectory(dir);
            string outPath = Path.Combine(dir, "est.jsonl");
            if (File.Exists(outPath)) File.Delete(outPath);
            long seeds = Arg(args, "--seeds", 60000);

            Query q = Q("1000", 96, 2000);
            SearchSession s = SearchSession.Create(q, Oracle, Engine, seeds, 8, outPath, acceptScanOrder: true);
            IResultSink? sink = s.Start(resume: false);

            List<string> projections = new List<string>();
            Stopwatch sw = Stopwatch.StartNew();
            long lastSeeds = 0;
            SearchOutcome o = s.Run(sink, p =>
            {
                if (p.Evaluated - lastSeeds < seeds / 6) return;
                lastSeeds = p.Evaluated;
                RunEstimate e = s.Estimator.Project(seeds - p.Evaluated, p.Evaluated, s.Output,
                                                    s.Compiled.Goals.Count, outPath);
                if (e.BasedOnSeeds == 0) return;
                projections.Add("at " + p.Evaluated.ToString("N0", CultureInfo.InvariantCulture) + " seeds: "
                                + e.WallText + " left, " + e.MatchesText + " matches, " + e.BytesText + " on disk  ["
                                + e.Basis + "]");
            }, TimeSpan.Zero, TimeSpan.FromSeconds(2));
            sw.Stop();
            sink?.Finish();
            sink?.Dispose();

            foreach (string p in projections) Console.WriteLine("  " + p);
            Console.WriteLine("actual                   " + o.Seconds.ToString("0.0", CultureInfo.InvariantCulture)
                              + " s total, " + o.Passed.ToString("N0", CultureInfo.InvariantCulture) + " matches, "
                              + new FileInfo(outPath).Length.ToString("N0", CultureInfo.InvariantCulture) + " B on disk");
            Console.WriteLine("estimator violations     " + s.Estimator.Violations
                              + " (times a slice fell outside the range the previous projection claimed)");
            return 0;
        }

        // ---- 9. screen-then-verify returns the same seeds as a pure G12 run --------------------------
        private static int Screen(string[] args)
        {
            long seeds = Arg(args, "--seeds", 2000);
            string dir = Dir(args);
            Directory.CreateDirectory(dir);

            // Three bulk must-haves - the family the resolution study says a coarse screen is safe
            // for - plus one nice-to-have to rank by.
            const string Body =
                "{ \"id\": \"meadows\", \"target\": \"biome:Meadows\", \"metric\": \"area_within\","
                + " \"radius\": 2000, \"test\": \"at_least\", \"value\": 4600000, \"importance\": \"must\" },"
                + "{ \"id\": \"blackforest\", \"target\": \"biome:BlackForest\", \"metric\": \"area_within\","
                + " \"radius\": 3000, \"test\": \"at_least\", \"value\": 11500000, \"importance\": \"must\" },"
                + "{ \"id\": \"swamp-far\", \"target\": \"biome:Swamp\", \"metric\": \"area_within\","
                + " \"radius\": 1500, \"test\": \"at_most\", \"value\": 0, \"importance\": \"must\" },"
                + "{ \"id\": \"rank\", \"target\": \"biome:Meadows\", \"metric\": \"area_within\","
                + " \"radius\": 2000, \"test\": \"at_least\", \"value\": 4000000, \"importance\": \"nice\" }";

            string Make(string screen) =>
                "{ \"version\": 1, \"defs\": 1, \"name\": \"screenproof\","
                + " \"search\": { \"order\": \"shuffled\", \"key\": \"0x5EEDF00D1234ABCD\", \"grid\": 12,"
                + " \"keep\": \"all\", \"block_size\": 64, \"screen\": \"" + screen + "\","
                + " \"screen_grid\": 24 }, \"goals\": [" + Body + "] }";

            string exactPath = Path.Combine(dir, "exact.jsonl");
            string screenPath = Path.Combine(dir, "screened.jsonl");
            foreach (string f in new[] { exactPath, screenPath }) if (File.Exists(f)) File.Delete(f);

            Stopwatch sw = Stopwatch.StartNew();
            SearchOutcome a = Once(QueryReader.Parse(Make("off"), "exact"), exactPath, seeds, 16);
            double exactSeconds = sw.Elapsed.TotalSeconds;

            sw.Restart();
            SearchSession s = SearchSession.Create(QueryReader.Parse(Make("auto"), "screened"), Oracle, Engine,
                                                   seeds, 16, screenPath, acceptScanOrder: true);
            IResultSink? sink = s.Start(resume: false);
            SearchOutcome b = s.Run(sink, null, TimeSpan.Zero, TimeSpan.FromSeconds(5));
            sink?.Finish();
            sink?.Dispose();
            double screenSeconds = sw.Elapsed.TotalSeconds;

            Console.WriteLine("grid plan                " + s.Grid.Describe());
            Console.WriteLine("exact  (grid 12 only)    " + a.Passed.ToString("N0", CultureInfo.InvariantCulture)
                              + " matches of " + a.Evaluated.ToString("N0", CultureInfo.InvariantCulture)
                              + " seeds in " + exactSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s");
            Console.WriteLine("screen G24 1 % -> G12    " + b.Passed.ToString("N0", CultureInfo.InvariantCulture)
                              + " matches of " + b.Evaluated.ToString("N0", CultureInfo.InvariantCulture)
                              + " seeds in " + screenSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s"
                              + "   (" + (exactSeconds / Math.Max(0.001, screenSeconds)).ToString("0.00", CultureInfo.InvariantCulture)
                              + "x)");

            HashSet<string> exact = new HashSet<string>(SeedOrder(exactPath), StringComparer.Ordinal);
            HashSet<string> screened = new HashSet<string>(SeedOrder(screenPath), StringComparer.Ordinal);
            int lost = 0, extra = 0;
            foreach (string x in exact) if (!screened.Contains(x)) lost++;
            foreach (string x in screened) if (!exact.Contains(x)) extra++;

            Console.WriteLine("false negatives          " + lost + "  (a seed the exact run matched and the screen threw away)");
            Console.WriteLine("false positives          " + extra + "  (must be 0: every survivor is re-measured exactly)");

            // And the records must be identical, not merely the seed sets: the screened run writes the
            // 12 m measurement, not the 24 m one.
            Dictionary<string, string> bySeed = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string line in File.ReadLines(exactPath))
            {
                if (line.Length == 0) continue;
                using System.Text.Json.JsonDocument d = System.Text.Json.JsonDocument.Parse(line);
                bySeed[d.RootElement.GetProperty("seed").GetInt32().ToString(CultureInfo.InvariantCulture)] = line;
            }

            int differing = 0, compared = 0;
            foreach (string line in File.ReadLines(screenPath))
            {
                if (line.Length == 0) continue;
                using System.Text.Json.JsonDocument d = System.Text.Json.JsonDocument.Parse(line);
                string seed = d.RootElement.GetProperty("seed").GetInt32().ToString(CultureInfo.InvariantCulture);
                if (!bySeed.TryGetValue(seed, out string? other)) continue;
                compared++;
                if (StripScreenFields(line) != other) differing++;
            }

            Console.WriteLine("records compared         " + compared + ", differing " + differing
                              + " (the screened run writes the 12 m measurement, not the 24 m one)");
            return lost == 0 && extra == 0 && differing == 0 ? 0 : 1;
        }

        /// <summary>Removes the two provenance fields only a screened record carries.</summary>
        private static string StripScreenFields(string line)
        {
            int i = line.IndexOf(",\"screened_at_grid_m\"", StringComparison.Ordinal);
            if (i < 0) return line;
            int end = line.IndexOf('}', i);
            return line.Substring(0, i) + line.Substring(end);
        }

        // ---- helpers ---------------------------------------------------------------------------------
        private static SearchOutcome Once(Query q, string outPath, long seeds, int threads)
        {
            SearchSession s = SearchSession.Create(q, Oracle, Engine, seeds, threads, outPath, acceptScanOrder: true);
            IResultSink? sink = s.Start(resume: false);
            SearchOutcome o = s.Run(sink, null, TimeSpan.Zero, TimeSpan.FromSeconds(5));
            sink?.Finish();
            sink?.Dispose();
            return o;
        }

        private static long CountLines(string path)
        {
            long n = 0;
            foreach (string _ in File.ReadLines(path)) n++;
            return n;
        }

        private static List<string> SeedOrder(string path)
        {
            List<string> seeds = new List<string>();
            foreach (string line in File.ReadLines(path))
            {
                if (line.Length == 0) continue;
                using System.Text.Json.JsonDocument d = System.Text.Json.JsonDocument.Parse(line);
                seeds.Add(d.RootElement.GetProperty("seed").GetInt32().ToString(CultureInfo.InvariantCulture));
            }

            return seeds;
        }

        private static List<string> TopOf(string path, int keep)
        {
            List<(double Score, int Seed)> all = new List<(double, int)>();
            foreach (string line in File.ReadLines(path))
            {
                if (line.Length == 0) continue;
                using System.Text.Json.JsonDocument d = System.Text.Json.JsonDocument.Parse(line);
                all.Add((d.RootElement.GetProperty("score").GetDouble(), d.RootElement.GetProperty("seed").GetInt32()));
            }

            all.Sort((a, b) =>
            {
                int c = b.Score.CompareTo(a.Score);
                return c != 0 ? c : a.Seed.CompareTo(b.Seed);
            });

            List<string> top = new List<string>();
            for (int i = 0; i < Math.Min(keep, all.Count); i++) top.Add(all[i].Seed.ToString(CultureInfo.InvariantCulture));
            return top;
        }

        private static bool Has(string[] a, string flag) => Array.IndexOf(a, flag) >= 0;

        private static long Arg(string[] a, string name, long fallback)
        {
            int i = Array.IndexOf(a, name);
            return i >= 0 && i + 1 < a.Length && long.TryParse(a[i + 1], out long v) ? v : fallback;
        }

        private static double Arg(string[] a, string name, double fallback)
        {
            int i = Array.IndexOf(a, name);
            return i >= 0 && i + 1 < a.Length
                   && double.TryParse(a[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
                ? v : fallback;
        }

        private static string Args(string[] a, string name, string fallback)
        {
            int i = Array.IndexOf(a, name);
            return i >= 0 && i + 1 < a.Length ? a[i + 1] : fallback;
        }

        private static string Dir(string[] a) => Args(a, "--dir", Path.Combine(Path.GetTempPath(), "seedlab-proof"));
    }
}
