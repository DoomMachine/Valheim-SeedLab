using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SeedLab.Search.Criteria;
using SeedLab.Search.Evaluation;
using SeedLab.Search.Execution;
using SeedLab.Search.Locations;
using SeedLab.Search.Output;

namespace SeedLab.SearchTests
{
    /// <summary>
    /// Where a funnel's stage two checkpoints, which cache root <c>vseed serve</c> uses, and what
    /// becomes of the stage-two checkpoints an earlier build left at the old default path - the
    /// follow-up the 2026-09-24 block-size review recorded, fixed the same day.
    ///
    /// <para><b>The defect.</b> Both front ends built stage two with a bare
    /// <see cref="SearchSession.Create"/>, which named its checkpoint from a hard-coded
    /// <c>%LOCALAPPDATA%\SeedLab\checkpoints</c>: the terminal then used that path instead of the one
    /// it had resolved from <c>--checkpoint</c>, <c>--cache-dir</c> and <c>SEEDLAB_CACHE_DIR</c>, and
    /// the page dropped the one its planner had set. Stage one's survivor list, named from the run's own
    /// path, went where it was told; stage two did not. And <c>vseed serve</c> never handed the web
    /// server its runtime, so the server started a second one that had not seen <c>--cache-dir</c>.</para>
    ///
    /// <para><b>In process:</b> the fallback directory is the cache root's, stage two keeps the run's
    /// path, orphaned temp files are tidied in the run's own cache root only, and the move of a legacy
    /// stage-two checkpoint - a real one, written by a stage two that was stopped - adopts exactly the
    /// file that is this stage two's, resumes it to the uninterrupted run's bytes, and leaves every
    /// other file as it was. <b>End to end</b>, against the built <c>vseed</c>: a terminal funnel and a
    /// page funnel, each stopped by its budget during stage two with <c>--cache-dir</c> set, leave
    /// stage two's checkpoint under that directory, the terminal's resumes from there, and serve's
    /// startup block names the <c>--cache-dir</c>.</para>
    ///
    /// <para><b>The user's real cache root is never read or written.</b> Every directory here is a
    /// temp one, and every child process is also given <c>SEEDLAB_CACHE_DIR</c> pointing at a second,
    /// empty temp directory: that variable now decides the only fallback left, so "nothing landed
    /// there" is the proof that nothing fell back, without looking at <c>%LOCALAPPDATA%</c>.</para>
    /// </summary>
    public static class CheckpointPlaceChecks
    {
        private const string Engine = "tests";
        private const string CacheVariable = "SEEDLAB_CACHE_DIR";

        // A cheap bounded query for the in-process legs: a biome goal on a 2 km disc at G384 is
        // milliseconds a seed, keep 5 makes the run bounded (so a stop writes a kept-results snapshot
        // beside its checkpoint), and blocks of 4 give a stopped stage two a resume point to keep.
        private const string CheapQuery =
            @"{""version"":1,""defs"":1,""name"":""stage-two-place"",
               ""search"":{""order"":""shuffled"",""grid"":384,""screen"":""off"",""keep"":5,""block_size"":4},
               ""goals"":[
                 {""id"":""meadows"",""target"":""biome:Meadows"",""metric"":""area_within"",""radius"":2000,""test"":""at_least"",""value"":1000000,""importance"":""must""},
                 {""id"":""swamp"",""target"":""biome:Swamp"",""metric"":""area_within"",""radius"":2000,""test"":""at_least"",""value"":100000,""importance"":""nice""}]}";

        // The end-to-end funnel: a cheap must-have that passes nearly every seed (so stage two has
        // about 96 survivors to place at about a second each), a boss-altar distance only placement
        // answers, and a nice-to-have to rank by. Measured 2026-09-24 on 4 workers in blocks of 4:
        // stage one 96 seeds in about 0.1 s, stage two about 1.06 s a survivor - so a 1 s budget
        // always lets stage one finish and always stops stage two after its first block per worker
        // (16 of about 96 placed), with a 4x margin either way.
        private const string FunnelGoals =
            @"{""id"":""meadows"",""target"":""biome:Meadows"",""metric"":""area_within"",""radius"":2000,""test"":""at_least"",""value"":2000000,""importance"":""must""},
              {""id"":""elder"",""target"":""location:Eikthyrnir"",""metric"":""nearest_distance"",""test"":""near"",""value"":600,""importance"":""must""},
              {""id"":""swamp"",""target"":""biome:Swamp"",""metric"":""area_within"",""radius"":2000,""test"":""at_least"",""value"":100000,""importance"":""nice""}";

        public static void Run(Action<bool, string, string> check)
        {
            string dir = Path.Combine(Path.GetTempPath(), "seedlab-stage2-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                FallbackIsTheCacheRoot(check, Path.Combine(dir, "fallback"));
                StageTwoKeepsThePath(check, Path.Combine(dir, "keep"));
                Adoption(check, Path.Combine(dir, "adopt"));
                LeftAlone(check, Path.Combine(dir, "alone"));
                EndToEnd(check, Path.Combine(dir, "e2e"));
            }
            finally
            {
                DeleteTree(dir);
            }
        }

        // =========================================================================================
        // The fallback: the cache root's own resolution, and orphans tidied in the run's root only.

        private static void FallbackIsTheCacheRoot(Action<bool, string, string> check, string dir)
        {
            string env = Path.Combine(dir, "env");
            string mine = Path.Combine(dir, "mine");
            string? before = Environment.GetEnvironmentVariable(CacheVariable);
            try
            {
                Environment.SetEnvironmentVariable(CacheVariable, env);
                string root = CheckpointStore.Root;
                check(root == Path.Combine(Path.GetFullPath(env), "checkpoints"),
                      "the fallback checkpoints directory honours SEEDLAB_CACHE_DIR, through the cache root's own resolution",
                      root);
                check(!string.Equals(Path.GetFullPath(CheckpointStore.LegacyRoot), root, StringComparison.OrdinalIgnoreCase),
                      "and the legacy location stays the old definition, which never read that variable",
                      "only the move of an earlier build's stage two looks there");

                SearchSession fallback = Session(Parse(), null, null);
                check(fallback.CheckpointPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal),
                      "a library caller that names no directory gets its checkpoint in that cache root",
                      fallback.CheckpointPath);

                // One orphan in the run's own directory and one in the fallback's, both an hour old.
                Directory.CreateDirectory(mine);
                Directory.CreateDirectory(root);
                string ownOrphan = Path.Combine(mine, "old.ckpt.tmp");
                string otherOrphan = Path.Combine(root, "old.ckpt.tmp");
                foreach (string p in new[] { ownOrphan, otherOrphan })
                {
                    File.WriteAllText(p, "{");
                    File.SetLastWriteTimeUtc(p, DateTime.UtcNow.AddHours(-1));
                }

                SearchSession own = Session(Parse(), mine, null);
                own.Start(resume: false);
                check(!File.Exists(ownOrphan) && File.Exists(otherOrphan),
                      "a run given its cache root tidies orphaned temp files there and nowhere else",
                      "own orphan " + (File.Exists(ownOrphan) ? "kept" : "removed") + ", the other root's "
                      + (File.Exists(otherOrphan) ? "left alone" : "REMOVED"));

                fallback.Start(resume: false, Path.Combine(mine, "elsewhere.ckpt"));
                check(!File.Exists(otherOrphan),
                      "and a run with no cache root of its own still tidies the fallback's, as it always did",
                      File.Exists(otherOrphan) ? "kept" : "removed");
            }
            finally
            {
                Environment.SetEnvironmentVariable(CacheVariable, before);
            }
        }

        // =========================================================================================
        // Stage two keeps the run's path: the default one, and one named like --checkpoint names it.

        private static void StageTwoKeepsThePath(Action<bool, string, string> check, string dir)
        {
            SearchSession full = Session(Parse(), dir, null);
            check(full.CheckpointDirectory == dir && full.CheckpointPath == CheckpointStore.PathIn(dir, full.QueryHash),
                  "a session given a checkpoints directory names its checkpoint there",
                  full.CheckpointPath);

            int[] survivors = Survivors(full, 16);
            SearchSession two = full.ForSurvivors(survivors, BlockSizing.Decide(4, survivors.Length, 1), 16, null, false, true);
            check(two.CheckpointPath == full.CheckpointPath && two.CheckpointDirectory == dir
                  && two.QueryHash == full.QueryHash && two.Plan.IsExplicit && two.Plan.Limit == 16 && two.Plan.BlockSize == 4,
                  "stage two keeps the run's checkpoint path and directory, over the survivor list at the gate's block size",
                  two.CheckpointPath + ", " + two.Plan.Limit + " survivors in blocks of " + two.Plan.BlockSize);

            // The terminal's --checkpoint sets the path on the session after Create; stage two keeps THAT.
            string named = Path.Combine(dir, "named", "mine.ckpt");
            full.CheckpointPath = named;
            SearchSession namedTwo = full.ForSurvivors(survivors, BlockSizing.Decide(4, survivors.Length, 1), 16, null, false, true);
            check(namedTwo.CheckpointPath == named,
                  "and a path named on the command line is the one stage two uses too",
                  namedTwo.CheckpointPath);
        }

        // =========================================================================================
        // The move of an earlier build's stage two: the matching file, adopted, and resumed.

        private static void Adoption(Action<bool, string, string> check, string dir)
        {
            string legacyDir = Path.Combine(dir, "legacy");
            string newDir = Path.Combine(dir, "new");
            string results = Path.Combine(dir, "resumed.jsonl");
            string reference = Path.Combine(dir, "reference.jsonl");

            // The earlier build's leg: stage two at the old path, stopped after its first two blocks.
            SearchSession full = Session(Parse(), legacyDir, results);
            int[] survivors = Survivors(full, 16);
            string hash = full.QueryHash;
            SearchOutcome first = StopStageTwo(full, survivors, results, survivors[4]);
            string legacy = CheckpointStore.PathIn(legacyDir, hash);
            string legacyTop = CheckpointStore.SnapshotPathFor(legacy);
            bool made = File.Exists(legacy) && File.Exists(legacyTop) && !first.Complete && first.Evaluated == 8;
            check(made, "a stopped stage two left a checkpoint and a kept-results snapshot at the old path (GUARD)",
                  first.Evaluated + " of 16 placed, checkpoint " + (File.Exists(legacy) ? "there" : "MISSING")
                  + ", snapshot " + (File.Exists(legacyTop) ? "there" : "MISSING"));
            if (!made) return;

            byte[] topBefore = File.ReadAllBytes(legacyTop);
            string moved = CheckpointStore.PathIn(newDir, hash);
            LegacyCheckpoint? r = CheckpointStore.AdoptLegacyStageTwo(legacy, moved, Parse(), survivors, hash);
            check(r != null && r.Adopted && r.Reason == null && r.LeftBehind.Count == 0,
                  "THE MOVE: the earlier build's stage two, which is this run's, is adopted",
                  r == null ? "nothing was decided" : r.Adopted ? "adopted" : "not adopted: " + r.Reason);

            Checkpoint? c = File.Exists(moved) ? Checkpoint.Load(moved) : null;
            check(c != null && c.NextBlock == 2 && c.SeedsEvaluated == 8
                  && c.KeptSnapshot == CheckpointStore.SnapshotPathFor(moved)
                  && File.Exists(c.KeptSnapshot) && Same(File.ReadAllBytes(c.KeptSnapshot), topBefore),
                  "the checkpoint is at the new path with its resume point, and names a byte-identical copy of the snapshot beside it",
                  c == null ? "no checkpoint at the new path" : "next block " + c.NextBlock + ", kept_snapshot " + c.KeptSnapshot);
            check(!File.Exists(legacy) && !File.Exists(legacyTop),
                  "and the legacy pair is gone, so nothing is left for a later run to trip over",
                  (File.Exists(legacy) ? "checkpoint LEFT, " : "") + (File.Exists(legacyTop) ? "snapshot LEFT" : "both removed"));
            if (c == null) return;

            // Resumed from the new path, exactly as the terminal's --resume does it: the block size
            // from the checkpoint, then Start's own Load and MustMatch, then the rest of the survivors.
            SearchSession full2 = Session(Parse(), newDir, results);
            BlockSizeDecision size = BlockSizing.Decide(full2.Query.Search.BlockSize, survivors.Length, 1,
                                                        SearchSpec.DefaultBlockSize, ResumePoint.From(c));
            SearchSession two2 = full2.ForSurvivors(survivors, size, 16, results, false, true);
            IResultSink? sink = two2.Start(resume: true);
            SearchOutcome rest = two2.Run(sink, null, TimeSpan.Zero, TimeSpan.FromHours(1));
            sink?.Finish();
            sink?.Dispose();

            SearchSession full3 = Session(Parse(), Path.Combine(dir, "reference"), reference);
            SearchSession two3 = full3.ForSurvivors(survivors, BlockSizing.Decide(4, survivors.Length, 1), 16, reference, false, true);
            IResultSink? refSink = two3.Start(resume: false);
            SearchOutcome whole = two3.Run(refSink, null, TimeSpan.Zero, TimeSpan.FromHours(1));
            refSink?.Finish();
            refSink?.Dispose();

            bool same = rest.Complete && whole.Complete && File.Exists(results) && File.Exists(reference)
                        && Same(File.ReadAllBytes(results), File.ReadAllBytes(reference));
            check(same,
                  "the adopted stage two resumes and finishes with the uninterrupted stage two's bytes",
                  "resumed " + (rest.Complete ? "complete" : "INCOMPLETE") + ", " + rest.Passed + " matches; uninterrupted "
                  + whole.Passed + "; files " + (same ? "identical" : "DIFFER"));
            check(!File.Exists(moved) && !File.Exists(CheckpointStore.SnapshotPathFor(moved)),
                  "and the finished run retires the moved checkpoint like any other",
                  File.Exists(moved) ? "still there" : "retired");
        }

        // =========================================================================================
        // Everything that is not this stage two's is left exactly as it was, and said.

        private static void LeftAlone(Action<bool, string, string> check, string dir)
        {
            SearchSession probe = Session(Parse(), Path.Combine(dir, "probe"), null);
            int[] survivors = Survivors(probe, 16);
            string hash = probe.QueryHash;

            // Another survivor list: the same query, the same count, another key.
            string otherDir = Path.Combine(dir, "other-list");
            int[] reversed = (int[])survivors.Clone();
            Array.Reverse(reversed);
            StopStageTwo(Session(Parse(), otherDir, Path.Combine(dir, "other.jsonl")), reversed,
                         Path.Combine(dir, "other.jsonl"), reversed[4]);
            Untouched(check, "another survivor list's stage two", CheckpointStore.PathIn(otherDir, hash),
                      Path.Combine(dir, "to-other", "x.ckpt"), survivors, hash, "Feistel key");

            // The same query's SAMPLE run: the file a default-layout sample left at the old path.
            string sampleDir = Path.Combine(dir, "sample");
            SearchSession sample = Session(Parse(), sampleDir, Path.Combine(dir, "sample.jsonl"));
            IResultSink? sink = sample.Start(resume: false);
            sample.Run(sink, null, TimeSpan.Zero, TimeSpan.FromHours(1),
                       run => run.EvaluatorFactory = () => new StopAt(new SeedEvaluator(sample.Compiled, Oracle), sample.Plan.SeedAt(4), run));
            sink?.Finish();
            sink?.Dispose();
            Untouched(check, "a sample run's checkpoint of the same query", CheckpointStore.PathIn(sampleDir, hash),
                      Path.Combine(dir, "to-sample", "x.ckpt"), survivors, hash, "scan order");

            // A file that is not a checkpoint at all.
            string junkDir = Path.Combine(dir, "junk");
            Directory.CreateDirectory(junkDir);
            string junk = CheckpointStore.PathIn(junkDir, hash);
            File.WriteAllText(junk, "{ \"version\": 1, \"engine\": ");
            Untouched(check, "a file that does not parse", junk, Path.Combine(dir, "to-junk", "x.ckpt"),
                      survivors, hash, "could not be read");

            // Nothing to decide: the new path is taken, or it IS the legacy path.
            string taken = Path.Combine(dir, "taken", "x.ckpt");
            Directory.CreateDirectory(Path.GetDirectoryName(taken)!);
            File.WriteAllText(taken, "the new path's own");
            byte[] junkBefore = File.ReadAllBytes(junk);
            LegacyCheckpoint? none1 = CheckpointStore.AdoptLegacyStageTwo(junk, taken, Parse(), survivors, hash);
            LegacyCheckpoint? none2 = CheckpointStore.AdoptLegacyStageTwo(junk, junk, Parse(), survivors, hash);
            check(none1 == null && none2 == null && File.ReadAllText(taken) == "the new path's own"
                  && Same(File.ReadAllBytes(junk), junkBefore),
                  "a new path that already has a checkpoint, or that is the legacy path itself, is left to the ordinary resume",
                  (none1 == null ? "taken: nothing done" : "taken: ACTED") + ", " + (none2 == null ? "same path: nothing done" : "same path: ACTED"));
        }

        private static void Untouched(Action<bool, string, string> check, string what, string legacy, string newPath,
                                      int[] survivors, string hash, string reasonMentions)
        {
            string top = CheckpointStore.SnapshotPathFor(legacy);
            bool hadTop = File.Exists(top);
            byte[] before = File.Exists(legacy) ? File.ReadAllBytes(legacy) : Array.Empty<byte>();
            byte[] topBefore = hadTop ? File.ReadAllBytes(top) : Array.Empty<byte>();
            DateTime when = File.Exists(legacy) ? File.GetLastWriteTimeUtc(legacy) : DateTime.MinValue;

            LegacyCheckpoint? r = CheckpointStore.AdoptLegacyStageTwo(legacy, newPath, Parse(), survivors, hash);
            bool left = before.Length > 0 && r != null && !r.Adopted && r.Reason != null
                        && r.Reason.Contains(reasonMentions, StringComparison.Ordinal)
                        && File.Exists(legacy) && Same(File.ReadAllBytes(legacy), before)
                        && File.GetLastWriteTimeUtc(legacy) == when
                        && (!hadTop || Same(File.ReadAllBytes(top), topBefore))
                        && !File.Exists(newPath) && !File.Exists(CheckpointStore.SnapshotPathFor(newPath));
            check(left, what + " is not adopted, is left byte for byte as it was, and the reason is said",
                  r == null ? "nothing was decided" : r.Adopted ? "ADOPTED" : r.Reason ?? "(no reason)");
        }

        // =========================================================================================
        // End to end, against the built vseed: the terminal and the page.

        private static void EndToEnd(Action<bool, string, string> check, string dir)
        {
            string? root = RepoRoot();
            string exe = root == null ? "" : Path.Combine(root, "src", "SeedLab.Cli", "bin", "Release", "net10.0",
                                                           OperatingSystem.IsWindows() ? "vseed.exe" : "vseed");
            bool built = root != null && File.Exists(exe);
            check(built, "the Release vseed is built, for the end-to-end checks",
                  built ? exe : "build src\\SeedLab.Cli -c Release first - the checks below are SKIPPED, not passed");
            if (!built) return;

            // A stale vseed would test yesterday's code: it must carry the SeedLab.Search these tests do.
            string mine = Path.Combine(AppContext.BaseDirectory, "SeedLab.Search.dll");
            string theirs = Path.Combine(Path.GetDirectoryName(exe)!, "SeedLab.Search.dll");
            bool fresh = File.Exists(theirs) && Same(SHA256.HashData(File.ReadAllBytes(mine)), SHA256.HashData(File.ReadAllBytes(theirs)));
            check(fresh, "that vseed carries the same SeedLab.Search as these tests",
                  fresh ? "SHA-256 equal" : "rebuild src\\SeedLab.Cli -c Release - the checks below are SKIPPED, not passed");
            if (!fresh) return;

            ILocationOracle oracle = SeedLab.LocationOracle.DumpedLocationOracle.Create(out string? problem);
            check(oracle.Available, "the dumped location table is available, for a funnel's stage two",
                  oracle.Available ? "" : (problem ?? "unavailable") + " - the checks below are SKIPPED, not passed");
            (oracle as IDisposable)?.Dispose();
            if (!oracle.Available) return;

            Terminal(check, exe, root!, Path.Combine(dir, "cli"));
            Page(check, exe, root!, Path.Combine(dir, "serve"));
        }

        private static void Terminal(Action<bool, string, string> check, string exe, string root, string dir)
        {
            string cache = Path.Combine(dir, "cache-dir");
            string fallback = Path.Combine(dir, "env-cache");
            string query = Path.Combine(dir, "stage-two.json");
            Directory.CreateDirectory(dir);
            File.WriteAllText(query, "{\"version\":1,\"defs\":1,\"name\":\"stage-two-path\",\"search\":{\"order\":\"shuffled\","
                                     + "\"grid\":384,\"screen\":\"off\",\"keep\":20},\"goals\":[" + FunnelGoals + "]}");
            List<string> args = new List<string>
            {
                "search", query, "--seeds", "96", "--budget", "1s", "--threads", "4", "--block-size", "4",
                "--strategy", "funnel", "--yes", "--progress", "none", "--cache-dir", cache,
                "--skip-self-test", "--ignore-running-game",
            };

            (int exit, string text) = RunToEnd(exe, root, args, fallback, TimeSpan.FromMinutes(3));

            // The machine block's "checkpoint  <path>"; the preflight's "checkpoint   one block of ..."
            // starts the same way, and is not a path.
            string? planned = LineValue(text, "  checkpoint  ", Path.IsPathRooted);
            string checkpoints = Path.Combine(Path.GetFullPath(cache), "checkpoints");
            bool stoppedInTwo = exit == 0 && text.Contains("stopped on the wall budget", StringComparison.Ordinal)
                                && !text.Contains("Stopped during stage 1", StringComparison.Ordinal);
            check(stoppedInTwo, "a terminal funnel with --cache-dir, stopped by its budget during stage 2 (GUARD)",
                  "exit " + exit + (stoppedInTwo ? "" : ": " + Tail(text)));
            if (!stoppedInTwo || planned == null) return;

            Checkpoint? c = File.Exists(planned) ? Checkpoint.Load(planned) : null;
            bool stageTwo = c != null && c.Order == "sequential" && c.From == 0 && c.NextBlock > 0 && c.SeedsEvaluated < c.Limit;
            check(planned.StartsWith(checkpoints + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && stageTwo,
                  "THE FIX, terminal: stage 2 checkpointed at the path the plan block named, under --cache-dir",
                  planned + (c == null ? " - NO FILE" : " - " + c.Order + ", " + c.SeedsEvaluated + " of " + c.Limit + " survivors placed"));
            check(File.Exists(Path.ChangeExtension(planned, ".survivors")),
                  "beside stage 1's survivor list, which always went there",
                  Path.ChangeExtension(planned, ".survivors"));
            check(Empty(fallback),
                  "and nothing reached the cache root SEEDLAB_CACHE_DIR names, so nothing fell back to a default",
                  Empty(fallback) ? "empty" : string.Join(", ", Directory.GetFiles(fallback, "*", SearchOption.AllDirectories)));
            if (c == null) return;

            // --resume: stage one's list is reused, and stage two resumes from THAT file - the peek that
            // sizes it and the file Start opens are the run's path now, not the default one.
            args.Add("--resume");
            (int exit2, string text2) = RunToEnd(exe, root, args, fallback, TimeSpan.FromMinutes(3));
            Checkpoint? after = File.Exists(planned) ? Checkpoint.Load(planned) : null;
            bool resumed = exit2 == 0
                           && text2.Contains("stage 1 was already finished: reusing", StringComparison.Ordinal)
                           && text2.Contains("resuming from " + planned, StringComparison.Ordinal)
                           && (after == null ? text2.Contains("retired", StringComparison.Ordinal) : after.SeedsEvaluated > c.SeedsEvaluated);
            check(resumed, "and --resume continues stage 2 from that file",
                  "exit " + exit2 + ", " + (after == null ? "finished" : after.SeedsEvaluated + " of " + after.Limit + " placed now")
                  + (resumed ? "" : ": " + Tail(text2)));
            check(Empty(fallback), "still nothing in the SEEDLAB_CACHE_DIR root after the resume",
                  Empty(fallback) ? "empty" : "FILES");
        }

        private static void Page(Action<bool, string, string> check, string exe, string root, string dir)
        {
            string cache = Path.Combine(dir, "cache-dir");
            string fallback = Path.Combine(dir, "env-cache");
            Directory.CreateDirectory(dir);

            ProcessStartInfo psi = Start(exe, root, new List<string>
            {
                "serve", "--port", "0", "--no-browser", "--cache-dir", cache, "--skip-self-test", "--ignore-running-game",
            }, fallback);
            List<string> lines = new List<string>();
            ManualResetEventSlim ready = new ManualResetEventSlim(false);
            using Process p = new Process { StartInfo = psi };
            p.OutputDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                lock (lines) lines.Add(e.Data);
                if (e.Data.Contains("press Ctrl+C to stop", StringComparison.Ordinal)) ready.Set();
            };
            p.ErrorDataReceived += (_, e) =>
            {
                if (e.Data != null) lock (lines) lines.Add(e.Data);
            };

            try
            {
                p.Start();
                p.StandardInput.Close();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                bool up = ready.Wait(TimeSpan.FromSeconds(60));
                string text;
                lock (lines) text = string.Join("\n", lines);
                check(up, "vseed serve --cache-dir starts (GUARD)", up ? "" : Tail(text));
                if (!up) return;

                string full = Path.GetFullPath(cache);
                check(LineValue(text, "  cache       ") == full,
                      "THE FIX, serve: the server's own startup block names the --cache-dir as its cache",
                      LineValue(text, "  cache       ") ?? "(no cache line)");

                string url = (LineValue(text, "SeedLab is serving at ") ?? "").Trim();
                string body = "{\"name\":\"stage-two-path\",\"goals\":[" + FunnelGoals + "],"
                              + "\"budgetSeeds\":96,\"budgetSeconds\":1,\"keep\":20,\"gridSpacingM\":384,\"threads\":4,"
                              + "\"blockSize\":4,\"order\":\"shuffled\",\"screen\":\"off\",\"strategy\":\"funnel\","
                              + "\"confirmed\":true,\"ignoreRunningGame\":true}";
                using HttpClient http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
                using HttpResponseMessage started = http.PostAsync(url + "/api/search",
                    new StringContent(body, Encoding.UTF8, "application/json")).GetAwaiter().GetResult();
                string startedBody = started.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                check(started.IsSuccessStatusCode, "the page's funnel is accepted (GUARD)", (int)started.StatusCode + " " + startedBody);
                if (!started.IsSuccessStatusCode) return;

                string id;
                using (JsonDocument d = JsonDocument.Parse(startedBody)) id = d.RootElement.GetProperty("id").GetString() ?? "";
                string stream = http.GetStringAsync(url + "/api/search/" + id + "/stream").GetAwaiter().GetResult();
                string? status = null, checkpoint = null;
                bool wall = false;
                foreach (string chunk in stream.Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!chunk.StartsWith("event: done\n", StringComparison.Ordinal)) continue;
                    using JsonDocument d = JsonDocument.Parse(chunk.Substring(chunk.IndexOf("\ndata: ", StringComparison.Ordinal) + 7));
                    status = d.RootElement.GetProperty("status").GetString();
                    wall = d.RootElement.TryGetProperty("stoppedByWall", out JsonElement w) && w.GetBoolean();
                    if (d.RootElement.TryGetProperty("checkpointPath", out JsonElement cp) && cp.ValueKind == JsonValueKind.String)
                        checkpoint = cp.GetString();
                }

                bool stopped = status == "done" && wall && checkpoint != null;
                check(stopped, "the page's funnel was stopped by its budget during stage 2 (GUARD)",
                      "status " + (status ?? "(no done event)") + ", stopped by the wall " + wall);
                if (!stopped) return;

                Checkpoint? c = File.Exists(checkpoint!) ? Checkpoint.Load(checkpoint!) : null;
                string checkpoints = Path.Combine(full, "checkpoints");
                check(checkpoint!.StartsWith(checkpoints + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                      && c != null && c.Order == "sequential" && c.From == 0 && c.SeedsEvaluated < c.Limit,
                      "THE FIX, page: stage 2 checkpointed in the runtime cache root --cache-dir chose, the planner's path",
                      checkpoint + (c == null ? " - NO FILE" : " - " + c.Order + ", " + c.SeedsEvaluated + " of " + c.Limit + " survivors placed"));
                check(Empty(fallback),
                      "and nothing - no tile, no checkpoint - reached the cache root SEEDLAB_CACHE_DIR names",
                      Empty(fallback) ? "empty" : string.Join(", ", Directory.GetFiles(fallback, "*", SearchOption.AllDirectories)));
            }
            finally
            {
                try
                {
                    if (!p.HasExited) p.Kill(entireProcessTree: true);
                    p.WaitForExit(10_000);
                }
                catch (Exception)
                {
                    // Never started, or already gone.
                }
            }
        }

        // =========================================================================================
        // Helpers.

        private static ILocationOracle Oracle => UnavailableLocationOracle.Instance;

        private static Query Parse() => QueryReader.Parse(CheapQuery, "stage-two-place");

        private static SearchSession Session(Query q, string? checkpointDirectory, string? outPath)
            => SearchSession.Create(q, Oracle, Engine, 16, 1, outPath, acceptScanOrder: true, allowVacuous: true,
                                    checkpointDirectory: checkpointDirectory);

        /// <summary>The first n seeds of the run's own walk, standing in for a stage-one survivor list.</summary>
        private static int[] Survivors(SearchSession full, int n)
        {
            int[] s = new int[n];
            for (int i = 0; i < n; i++) s[i] = full.Plan.SeedAt(i);
            return s;
        }

        /// <summary>A stage two over <paramref name="survivors"/>, stopped during the block that holds <paramref name="stopSeed"/>.</summary>
        private static SearchOutcome StopStageTwo(SearchSession full, int[] survivors, string results, int stopSeed)
        {
            SearchSession two = full.ForSurvivors(survivors, BlockSizing.Decide(4, survivors.Length, 1), 16, results, false, true);
            IResultSink? sink = two.Start(resume: false);
            SearchOutcome o = two.Run(sink, null, TimeSpan.Zero, TimeSpan.FromHours(1),
                                      run => run.EvaluatorFactory = () => new StopAt(new SeedEvaluator(two.Compiled, Oracle), stopSeed, run));
            sink?.Finish();
            sink?.Dispose();
            return o;
        }

        /// <summary>The SeedLab folder: the first parent of this test's binaries that holds src\SeedLab.Cli.</summary>
        private static string? RepoRoot()
        {
            for (DirectoryInfo? d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            {
                if (Directory.Exists(Path.Combine(d.FullName, "src", "SeedLab.Cli"))) return d.FullName;
            }

            return null;
        }

        /// <summary>
        /// A vseed child in the SeedLab folder (location answers need its data\), with stdin closed -
        /// nothing is reading a keyboard, as in a script - and SEEDLAB_CACHE_DIR at the fallback root.
        /// </summary>
        private static ProcessStartInfo Start(string exe, string root, List<string> args, string fallback)
        {
            ProcessStartInfo psi = new ProcessStartInfo(exe)
            {
                WorkingDirectory = root,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (string a in args) psi.ArgumentList.Add(a);
            psi.Environment[CacheVariable] = fallback;
            return psi;
        }

        /// <summary>Runs a vseed to its end and returns its exit code and everything it printed.</summary>
        private static (int Exit, string Text) RunToEnd(string exe, string root, List<string> args, string fallback, TimeSpan timeout)
        {
            using Process p = Process.Start(Start(exe, root, args, fallback))!;
            p.StandardInput.Close();
            Task<string> stdout = p.StandardOutput.ReadToEndAsync();
            Task<string> stderr = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit((int)timeout.TotalMilliseconds))
            {
                try { p.Kill(entireProcessTree: true); } catch (Exception) { }
                p.WaitForExit(10_000);
                return (-1, "(timed out after " + timeout.TotalSeconds + " s)\n" + stderr.Result + stdout.Result);
            }

            p.WaitForExit();
            return (p.ExitCode, stderr.Result + "\n" + stdout.Result);
        }

        /// <summary>
        /// The rest of the first line that starts with <paramref name="prefix"/> (and whose rest
        /// <paramref name="accept"/> takes, when given), or null.
        /// </summary>
        private static string? LineValue(string text, string prefix, Func<string, bool>? accept = null)
        {
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                if (!line.StartsWith(prefix, StringComparison.Ordinal)) continue;
                string rest = line.Substring(prefix.Length);
                if (accept == null || accept(rest)) return rest;
            }

            return null;
        }

        private static string Tail(string text)
            => text.Length <= 600 ? text.Replace('\n', ' ') : "..." + text.Substring(text.Length - 600).Replace('\n', ' ');

        private static bool Empty(string dir)
            => !Directory.Exists(dir) || Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Length == 0;

        private static bool Same(byte[] a, byte[] b) => a.AsSpan().SequenceEqual(b);

        private static void DeleteTree(string dir)
        {
            // A child that was just killed can hold a file for a moment after it exits.
            for (int i = 0; i < 10 && Directory.Exists(dir); i++)
            {
                try
                {
                    Directory.Delete(dir, true);
                }
                catch (Exception) when (i < 9)
                {
                    Thread.Sleep(300);
                }
                catch (Exception)
                {
                }
            }
        }

        /// <summary>An evaluator that presses Stop while it evaluates one chosen seed.</summary>
        private sealed class StopAt : ISeedEvaluator
        {
            private readonly ISeedEvaluator _inner;
            private readonly int _seed;
            private readonly SearchRun _run;

            public StopAt(ISeedEvaluator inner, int seed, SearchRun run)
            {
                _inner = inner;
                _seed = seed;
                _run = run;
            }

            public SeedResult Evaluate(int seed, bool full = false)
            {
                if (seed == _seed) _run.RequestStop();
                return _inner.Evaluate(seed, full);
            }

            public double ConstructSeconds => _inner.ConstructSeconds;
            public double SampleSeconds => _inner.SampleSeconds;
            public double PregenSeconds => _inner.PregenSeconds;
            public double LocationSeconds => _inner.LocationSeconds;
            public long Pregenerated => _inner.Pregenerated;
            public long ProbeAccepts => _inner.ProbeAccepts;
            public long EarlyExits => _inner.EarlyExits;
            public long LocationAborts => _inner.LocationAborts;
            public long LocationSkips => _inner.LocationSkips;
            public long Placed => _inner.Placed;
        }
    }
}
