using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using SeedLab.Search.Execution;

namespace SeedLab.SearchTests
{
    /// <summary>
    /// The terminal and the page when a file is in the way - the hosts' half of the follow-up to a run
    /// that died of "Access to the path is denied." (2026-09-24), end to end against the built
    /// <c>vseed</c>.
    ///
    /// <para><b>The terminal:</b> a results file another program holds without letting others write is
    /// refused before a seed is scanned (exit 3, the file named, nothing written), and <c>--dry-run</c>
    /// says so and goes on; <c>vseed map</c> does the same for <c>-o</c>; a checkpoint held for a
    /// while during a run gives a warning, then "saved again", and the run completes with the bytes of
    /// an unheld run; a checkpoint held through the end of a run the budget stops gives the last-save
    /// error, the older checkpoint still resumes to the same bytes.</para>
    ///
    /// <para><b>The session log:</b> written by a command, emptied by the next, a second one running
    /// at the same time writes <c>vseed.log.1</c>, <c>vseed clean</c> leaves a live session's log and
    /// its own and counts neither as freed, and a stale numbered log is deleted by the next session.</para>
    ///
    /// <para><b>The page:</b> a POST from another origin is refused (403) before any route, the page's
    /// own is not; a results file that is held refuses the search by name; a finished run whose
    /// checkpoint could not be deleted sends a "warning" event; and the last save of a run the budget
    /// stopped, held, fails, "Retry saving" fails while it is held and succeeds once it is not.</para>
    ///
    /// <para>The "other program" is a FileStream in this process: a rename, a delete and an open collide
    /// with a handle whichever process owns it (measured 2026-09-24). Every child is given
    /// <c>--cache-dir</c> under a temp folder and <c>SEEDLAB_CACHE_DIR</c> pointing at a second, empty
    /// one; the user's own cache root is never touched, and the temp folder is deleted afterwards.</para>
    /// </summary>
    public static class HostChecks
    {
        private const string CacheVariable = "SEEDLAB_CACHE_DIR";

        // A biome goal on a 2 km disc at G384: about 1,500 seeds a second a worker, and every seed
        // matches, so a bounded run keeps a real kept set and writes a snapshot with each checkpoint.
        private const string CheapQuery =
            @"{""version"":1,""defs"":1,""name"":""hosts-held-file"",
               ""search"":{""order"":""shuffled"",""grid"":384,""screen"":""off"",""keep"":5},
               ""goals"":[
                 {""id"":""meadows"",""target"":""biome:Meadows"",""metric"":""area_within"",""radius"":2000,""test"":""at_least"",""value"":1000000,""importance"":""must""},
                 {""id"":""swamp"",""target"":""biome:Swamp"",""metric"":""area_within"",""radius"":2000,""test"":""at_least"",""value"":100000,""importance"":""nice""}]}";

        // The same goals as the page sends them.
        private const string PageGoals =
            @"{""target"":""biome:Meadows"",""metric"":""area_within"",""radius"":2000,""test"":""at_least"",""value"":1000000,""importance"":""must""},
              {""target"":""biome:Swamp"",""metric"":""area_within"",""radius"":2000,""test"":""at_least"",""value"":100000,""importance"":""nice""}";

        public static void Run(Action<bool, string, string> check)
        {
            // Spelled as vseed spells it back: Path.GetFullPath expands an 8.3 short name in %TEMP%.
            string dir = Path.Combine(Path.GetTempPath(), "seedlab-hosts-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            dir = Path.GetFullPath(dir);
            try
            {
                string? exe = Vseed(check, out string root);
                if (exe == null) return;

                string query = Path.Combine(dir, "cheap.json");
                File.WriteAllText(query, CheapQuery);

                HeldResults(check, exe, root, query, Path.Combine(dir, "held-results"));
                HeldMapOutput(check, exe, Path.Combine(dir, "held-map"));
                MapHeldAfterTheCheck(check, exe, Path.Combine(dir, "held-map-late"));
                RotatedManifest(check, exe, root, query, Path.Combine(dir, "rotated"));
                string? reference = HeldCheckpoint(check, exe, root, query, Path.Combine(dir, "held-ckpt"));
                LastSave(check, exe, root, query, Path.Combine(dir, "last-save"), reference);
                LogsAndPage(check, exe, Path.Combine(dir, "page"));
            }
            finally
            {
                DeleteTree(dir);
            }
        }

        // =========================================================================================
        // A results file held without write sharing: refused by name before a seed, or said by --dry-run.

        private static void HeldResults(Action<bool, string, string> check, string exe, string root, string query, string dir)
        {
            Directory.CreateDirectory(dir);
            string cache = Path.Combine(dir, "cache");
            string outFile = Path.Combine(dir, "results.jsonl");
            File.WriteAllText(outFile, "the previous run's results\n");

            (int exit, string text) r;
            using (new FileStream(outFile, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                r = RunToEnd(exe, root, new List<string>
                {
                    "search", query, "--seeds", "64", "--json", "--out", outFile, "--yes",
                    "--cache-dir", cache, "--skip-self-test", "--ignore-running-game",
                }, Path.Combine(dir, "env"));
            }

            string checkpoints = Path.Combine(cache, "checkpoints");
            r.text = Flat(r.text);
            bool refused = r.exit == 3
                           && r.text.Contains("REFUSED - SeedLab cannot use a file this run needs", StringComparison.Ordinal)
                           && r.text.Contains(outFile, StringComparison.Ordinal)
                           && r.text.Contains("another program has it open", StringComparison.Ordinal)
                           && r.text.Contains("Nothing was scanned and nothing was written", StringComparison.Ordinal);
            check(refused,
                  "THE TERMINAL: a --json search whose results file another program holds is refused before a seed, exit 3, naming the file",
                  "exit " + r.exit + (refused ? "" : ": " + Tail(r.text)));
            check(!r.text.Contains("Exception", StringComparison.Ordinal) && !r.text.Contains("0x8007", StringComparison.Ordinal),
                  "and what it prints has no exception type and no HResult in it", refused ? "plain words" : Tail(r.text));
            check(File.ReadAllText(outFile) == "the previous run's results\n" && Empty(checkpoints),
                  "nothing was written: the results file is as it was and there is no checkpoint",
                  "results " + (File.ReadAllText(outFile).Length) + " B, checkpoints " + (Empty(checkpoints) ? "none" : "SOME"));

            string log = ReadLog(cache, 0);
            check(log.Contains("FAIL  write", StringComparison.Ordinal) && log.Contains("with exit code 3", StringComparison.Ordinal),
                  "and the session log has the failed check and the exit code",
                  log.Length == 0 ? "no log" : "found");

            // --dry-run says what it found and goes on to its estimate, with nothing to answer.
            (int exit, string text) d;
            using (new FileStream(outFile, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                d = RunToEnd(exe, root, new List<string>
                {
                    "search", query, "--seeds", "64", "--dry-run", "--calibrate", "2", "--out", outFile,
                    "--cache-dir", cache, "--skip-self-test", "--ignore-running-game",
                }, Path.Combine(dir, "env"));
            }

            d.text = Flat(d.text);
            bool said = d.exit == 0 && d.text.Contains("warning: SeedLab could not write to", StringComparison.Ordinal)
                        && d.text.Contains("--dry-run does not stop for this", StringComparison.Ordinal)
                        && d.text.Contains("Measured cost on this machine", StringComparison.Ordinal);
            check(said, "--dry-run with the same file held says so and still answers what the run would cost",
                  "exit " + d.exit + (said ? "" : ": " + Tail(d.text)));
            check(d.text.Contains("a real run would be refused here (exit 3)", StringComparison.Ordinal)
                  && !d.text.Contains("would stop here and ask", StringComparison.Ordinal),
                  "and, with nothing reading the keyboard, says a real run would be REFUSED here - as it is - not that it would ask",
                  LineWith(d.text, "--dry-run does not stop") ?? Tail(d.text));
            check(Empty(Path.Combine(dir, "env")), "nothing reached the cache root SEEDLAB_CACHE_DIR names", "");
        }

        // =========================================================================================
        // vseed map -o, held by an image viewer: refused before the render.

        private static void HeldMapOutput(Action<bool, string, string> check, string exe, string dir)
        {
            Directory.CreateDirectory(dir);
            string png = Path.Combine(dir, "map.png");
            File.WriteAllBytes(png, new byte[] { 1, 2, 3 });
            (int exit, string text) r;
            using (new FileStream(png, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                r = RunToEnd(exe, dir, new List<string>
                {
                    "map", "1", "--px", "64", "-o", png, "--json", "--cache-dir", Path.Combine(dir, "cache"),
                    "--skip-self-test", "--ignore-running-game",
                }, Path.Combine(dir, "env"));
            }

            r.text = Flat(r.text);
            bool ok = r.exit == 3 && r.text.Contains(png, StringComparison.Ordinal)
                      && r.text.Contains("Nothing was rendered and nothing was written", StringComparison.Ordinal)
                      && File.ReadAllBytes(png).Length == 3
                      && Directory.GetFiles(dir, ".seedlab-tmp-*").Length == 0;
            check(ok, "vseed map -o onto a file an image viewer holds: refused before the render, the file named, nothing left behind",
                  "exit " + r.exit + (ok ? "" : ": " + Tail(r.text)));
        }

        // =========================================================================================
        // A checkpoint held for a while during a run: a warning, "saved again", and the same bytes.

        private static string? HeldCheckpoint(Action<bool, string, string> check, string exe, string root, string query, string dir)
        {
            Directory.CreateDirectory(dir);
            string cache = Path.Combine(dir, "cache");
            string env = Path.Combine(dir, "env");

            // The reference: the same run with nothing in its way.
            string reference = Path.Combine(dir, "reference.jsonl");
            (int refExit, string refText) = RunToEnd(exe, root, Args(query, reference, Path.Combine(dir, "reference.ckpt"), cache, "20000"), env);
            check(refExit == 0 && File.Exists(reference), "an unheld reference run completes (GUARD)",
                  "exit " + refExit + (refExit == 0 ? "" : ": " + Tail(refText)));
            if (refExit != 0) return null;

            // The held run. The checkpoint path is taken by a file this process holds, sharing read only,
            // from before the run starts: the start check looks at the checkpoint's folder, not at a file a
            // fresh run is about to replace, so the run starts - and its first save runs into the holder.
            string held = Path.Combine(dir, "held.ckpt");
            string results = Path.Combine(dir, "held.jsonl");
            File.WriteAllText(held, "{}");
            FileStream? holder = new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.Read);
            ManualResetEventSlim warned = new ManualResetEventSlim(false);
            List<string> lines = new List<string>();
            int exit;
            try
            {
                using Process p = new Process { StartInfo = Start(exe, root, Args(query, results, held, cache, "20000"), env) };
                p.ErrorDataReceived += (_, e) =>
                {
                    if (e.Data == null) return;
                    lock (lines) lines.Add(e.Data);
                    if (e.Data.Contains("warning: the checkpoint could not be saved", StringComparison.Ordinal)) warned.Set();
                };
                p.OutputDataReceived += (_, e) =>
                {
                    if (e.Data != null) lock (lines) lines.Add(e.Data);
                };
                p.Start();
                p.StandardInput.Close();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();

                // Released at the first warning: the run is then going on without a current checkpoint,
                // and its next save has to be the one that works.
                bool sawWarning = warned.Wait(TimeSpan.FromSeconds(60));
                holder.Dispose();
                holder = null;
                if (!p.WaitForExit(180_000))
                {
                    p.Kill(entireProcessTree: true);
                    p.WaitForExit(10_000);
                }

                p.WaitForExit();
                exit = p.ExitCode;
                check(sawWarning, "THE TERMINAL: a checkpoint another program holds gives \"warning: the checkpoint could not be saved\" on stderr",
                      sawWarning ? "seen while the run went on" : "no warning within 60 s: " + Tail(Joined(lines)));
            }
            finally
            {
                holder?.Dispose();
            }

            string text = Flat(Joined(lines));
            bool completed = exit == 0 && text.Contains("the checkpoint was saved again at", StringComparison.Ordinal)
                             && text.Contains("retired - the run completed", StringComparison.Ordinal)
                             && text.Contains("failed along the way", StringComparison.Ordinal)
                             && !File.Exists(held);
            check(completed, "the run goes on, saves again once it is let go, completes and retires its checkpoint",
                  "exit " + exit + (completed ? "" : ": " + Tail(text)));

            bool same = File.Exists(results) && Same(File.ReadAllBytes(results), File.ReadAllBytes(reference));
            check(same, "and its results file is byte-identical to the unheld run's", same ? "identical" : "DIFFERS");

            // Held since before the run, and only the checkpoint's FOLDER was checked: nothing changed, so
            // nothing may be said to have (review of 2026-09-24: it said "so something changed after that").
            check(text.Contains("another program has it open", StringComparison.Ordinal)
                  && !text.Contains("so something changed after that", StringComparison.Ordinal),
                  "the warning claims no change since the start check, which looked only at the checkpoint's folder",
                  LineWith(Joined(lines), "could not be saved") ?? "(no warning line)");

            string log = ReadLog(cache, 0);
            check(log.Contains("WARN   printed  warning: the checkpoint could not be saved", StringComparison.Ordinal)
                  && log.Contains("retry    save " + held, StringComparison.Ordinal),
                  "the session log has the warning as printed and the retries behind it",
                  log.Length == 0 ? "no log" : "found");
            return same ? reference : null;
        }

        // =========================================================================================
        // A checkpoint held through the end of a run the budget stops: the last-save error, then --resume.

        private static void LastSave(Action<bool, string, string> check, string exe, string root, string query, string dir, string? reference)
        {
            Directory.CreateDirectory(dir);
            string cache = Path.Combine(dir, "cache");
            string env = Path.Combine(dir, "env");
            string ckpt = Path.Combine(dir, "run.ckpt");
            string results = Path.Combine(dir, "run.jsonl");

            List<string> args = Args(query, results, ckpt, cache, "20000");
            args.AddRange(new[] { "--budget", "3s", "--json" });
            ProcessStartInfo psi = Start(exe, root, args, env);
            FileStream? holder = null;
            int exit;
            string stdout, stderr;
            long heldBlock = -1;
            try
            {
                using Process p = Process.Start(psi)!;
                p.StandardInput.Close();
                Task<string> outTask = p.StandardOutput.ReadToEndAsync();
                Task<string> errTask = p.StandardError.ReadToEndAsync();

                // Held once the run has saved a real checkpoint, and until the run has ended: every save
                // after it, the last one included, runs into the holder.
                Stopwatch sw = Stopwatch.StartNew();
                while (holder == null && sw.Elapsed < TimeSpan.FromSeconds(30) && !p.HasExited)
                {
                    try
                    {
                        if (File.Exists(ckpt))
                        {
                            FileStream fs = new FileStream(ckpt, FileMode.Open, FileAccess.Read, FileShare.Read);
                            using StreamReader sr = new StreamReader(fs, Encoding.UTF8, false, 4096, leaveOpen: true);
                            using JsonDocument d = JsonDocument.Parse(sr.ReadToEnd());
                            heldBlock = d.RootElement.GetProperty("next_block").GetInt64();
                            holder = fs;
                        }
                    }
                    catch (Exception)
                    {
                        // Caught between the rename's two halves, or read mid-write; try again.
                    }

                    if (holder == null) Thread.Sleep(5);
                }

                if (!p.WaitForExit(180_000))
                {
                    p.Kill(entireProcessTree: true);
                    p.WaitForExit(10_000);
                }

                p.WaitForExit();
                exit = p.ExitCode;
                stdout = outTask.Result;
                stderr = errTask.Result;
            }
            finally
            {
                holder?.Dispose();
            }

            check(heldBlock > 0, "the run saved a checkpoint, which was then held to the end (GUARD)", "held at block " + heldBlock);
            if (heldBlock <= 0) return;

            long onDisk = -2, runBlock = -2;
            string problem = "", resumable = "";
            bool wall = false, complete = true;
            try
            {
                using JsonDocument j = JsonDocument.Parse(stdout);
                JsonElement e = j.RootElement.GetProperty("checkpoint_error");
                problem = e.GetProperty("problem").GetString() ?? "";
                onDisk = e.GetProperty("on_disk_block").GetInt64();
                runBlock = e.GetProperty("run_block").GetInt64();
                resumable = j.RootElement.GetProperty("resumable_checkpoint").GetString() ?? "";
                wall = j.RootElement.GetProperty("stopped_by_wall").GetBoolean();
                complete = j.RootElement.GetProperty("complete").GetBoolean();
            }
            catch (Exception ex)
            {
                problem = "(the JSON could not be read: " + ex.Message + ")";
            }

            stderr = Flat(stderr);
            bool reported = exit == 0 && wall && !complete
                            && stderr.Contains("error: the last checkpoint of this run could not be saved", StringComparison.Ordinal)
                            && stderr.Contains("resuming starts there", StringComparison.Ordinal)
                            && stderr.Contains("saving again is not offered", StringComparison.Ordinal);
            check(reported,
                  "THE LAST SAVE: held through the end of a run the budget stopped - an error that says what is on disk, and the exit code stays 0",
                  "exit " + exit + (reported ? "" : ": " + Tail(stderr)));
            check(stderr.Contains("saving the last checkpoint: " + ckpt + " is busy", StringComparison.Ordinal)
                  && stderr.IndexOf("saving the last checkpoint:", StringComparison.Ordinal)
                     < stderr.IndexOf("error: the last checkpoint of this run could not be saved", StringComparison.Ordinal),
                  "and the ~15 s wait before that error is said as it starts, not sat through in silence",
                  LineWith(stderr, "saving the last checkpoint") ?? "(no line: " + Tail(stderr) + ")");
            check(problem == "in_use" && onDisk == heldBlock && runBlock > onDisk
                  && string.Equals(resumable, ckpt, StringComparison.OrdinalIgnoreCase),
                  "--json carries checkpoint_error: in use, the block on disk, the block the run reached, and the older checkpoint as the resume point",
                  "problem " + problem + ", on disk " + onDisk + " (held " + heldBlock + "), run reached " + runBlock);

            string log = ReadLog(cache, 0);
            check(log.Contains("ERROR  search   the last checkpoint could not be saved", StringComparison.Ordinal)
                  && log.Contains("with exit code 0", StringComparison.Ordinal),
                  "the session log has the error, with the exception detail behind it, and the exit code", log.Length == 0 ? "no log" : "found");

            // The older checkpoint is a correct resume point: resumed to the end, the same bytes.
            if (reference == null)
            {
                check(false, "the resume from the older checkpoint (SKIPPED: no reference run above)", "");
                return;
            }

            List<string> resume = Args(query, results, ckpt, cache, "20000");
            resume.Add("--resume");
            (int rExit, string rText) = RunToEnd(exe, root, resume, env);
            rText = Flat(rText);
            bool same = rExit == 0 && rText.Contains("resuming from " + ckpt, StringComparison.Ordinal)
                        && File.Exists(results) && Same(File.ReadAllBytes(results), File.ReadAllBytes(reference));
            check(same, "--resume from the older checkpoint finishes with the bytes of a run nothing got in the way of",
                  "exit " + rExit + (same ? ", identical" : ": " + Tail(rText)));
        }

        // =========================================================================================
        // The session log's lifecycle, vseed clean beside a live session, and the page.

        private static void LogsAndPage(Action<bool, string, string> check, string exe, string dir)
        {
            Directory.CreateDirectory(dir);
            string cache = Path.Combine(dir, "cache");
            string env = Path.Combine(dir, "env");
            List<string> common = new List<string> { "--cache-dir", cache, "--skip-self-test", "--ignore-running-game" };

            // ---- written, then emptied by the next session ----------------------------------------
            (int e1, _) = RunToEnd(exe, dir, With(common, "at", "1", "0", "0"), env);
            string first = ReadLog(cache, 0);
            (int e2, _) = RunToEnd(exe, dir, With(common, "at", "2", "0", "0"), env);
            string second = ReadLog(cache, 0);
            check(e1 == 0 && first.Contains("command  vseed at 1 0 0", StringComparison.Ordinal)
                  && first.Contains("file access checked", StringComparison.Ordinal)
                  && first.Contains("INFO   integrity ", StringComparison.Ordinal)
                  && first.TrimEnd().EndsWith("with exit code 0", StringComparison.Ordinal),
                  "THE SESSION LOG: a command writes <cache>\\logs\\vseed.log - its command line, the access checks, the integrity line, the exit code",
                  first.Length == 0 ? "no log" : first.Split('\n').Length + " lines");
            check(e2 == 0 && second.Contains("command  vseed at 2 0 0", StringComparison.Ordinal)
                  && !second.Contains("vseed at 1 0 0", StringComparison.Ordinal),
                  "the next session empties it and writes its own, as BepInEx does with LogOutput.log",
                  second.Contains("vseed at 1 0 0", StringComparison.Ordinal) ? "the first session's lines are STILL there" : "rewritten");

            // ---- a live session: vseed serve ---------------------------------------------------------
            ProcessStartInfo psi = Start(exe, dir, With(common, "serve", "--port", "0", "--no-browser"), env);
            List<string> lines = new List<string>();
            ManualResetEventSlim ready = new ManualResetEventSlim(false);
            using Process serve = new Process { StartInfo = psi };
            serve.OutputDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                lock (lines) lines.Add(e.Data);
                if (e.Data.Contains("press Ctrl+C to stop", StringComparison.Ordinal)) ready.Set();
            };
            serve.ErrorDataReceived += (_, e) =>
            {
                if (e.Data != null) lock (lines) lines.Add(e.Data);
            };

            try
            {
                serve.Start();
                serve.StandardInput.Close();
                serve.BeginOutputReadLine();
                serve.BeginErrorReadLine();
                bool up = ready.Wait(TimeSpan.FromSeconds(60));
                string text = Joined(lines);
                check(up, "vseed serve starts (GUARD)", up ? "" : Tail(text));
                if (!up) return;
                check(text.Contains("  file access checked: ", StringComparison.Ordinal),
                      "serve's startup block carries the access and integrity line", LineWith(text, "file access checked") ?? "(none)");

                (int e3, _) = RunToEnd(exe, dir, With(common, "at", "3", "0", "0"), env);
                string numbered = ReadLog(cache, 1);
                string plain = ReadLog(cache, 0);
                check(e3 == 0 && numbered.Contains("command  vseed at 3 0 0", StringComparison.Ordinal)
                      && plain.Contains("command  vseed serve", StringComparison.Ordinal),
                      "a second command while serve runs writes vseed.log.1 and leaves serve's vseed.log alone",
                      "vseed.log.1 " + (numbered.Length > 0 ? "written" : "MISSING") + ", vseed.log "
                      + (plain.Contains("vseed serve", StringComparison.Ordinal) ? "still serve's" : "CHANGED"));

                (int e4, string cleanJson) = RunToEnd(exe, dir, With(common, "clean", "--what", "logs", "--yes", "--json"), env, stdoutOnly: true);
                bool cleanOk = false;
                string cleanDetail;
                try
                {
                    using JsonDocument d = JsonDocument.Parse(cleanJson);
                    JsonElement failed = d.RootElement.GetProperty("could_not_remove");
                    JsonElement kept = d.RootElement.GetProperty("kept");
                    long freed = d.RootElement.GetProperty("freed_bytes").GetInt64();
                    string plainPath = Path.Combine(Path.GetFullPath(cache), "logs", "vseed.log");
                    bool serveListed = false;
                    foreach (JsonElement f in failed.EnumerateArray())
                    {
                        if (string.Equals(f.GetProperty("path").GetString(), plainPath, StringComparison.OrdinalIgnoreCase)
                            && (f.GetProperty("why").GetString() ?? "").Contains("another vseed", StringComparison.Ordinal))
                        {
                            serveListed = true;
                        }
                    }

                    cleanOk = e4 == 0 && serveListed && kept.GetArrayLength() == 1 && File.Exists(plainPath);
                    cleanDetail = "exit " + e4 + ", freed " + freed + " B, could not remove " + failed.GetArrayLength()
                                  + (serveListed ? " (serve's log, named)" : " (serve's log NOT listed)") + ", kept " + kept.GetArrayLength();
                }
                catch (Exception ex)
                {
                    cleanDetail = "exit " + e4 + ", the JSON could not be read: " + ex.Message + " " + Tail(cleanJson);
                }

                check(cleanOk, "vseed clean --what logs --yes --json: serve's live log is listed as not removed, its own is kept, neither counted",
                      cleanDetail);

                Page(check, Url(text), Path.Combine(dir, "seedlab-results"));
            }
            finally
            {
                try
                {
                    if (!serve.HasExited) serve.Kill(entireProcessTree: true);
                    serve.WaitForExit(10_000);
                }
                catch (Exception)
                {
                }
            }

            // ---- a stale numbered log is deleted by the next session -----------------------------------
            (int e5, _) = RunToEnd(exe, dir, With(common, "at", "4", "0", "0"), env);
            string after = ReadLog(cache, 0);
            bool staleGone = !File.Exists(Path.Combine(cache, "logs", "vseed.log.1"));
            check(e5 == 0 && after.Contains("command  vseed at 4 0 0", StringComparison.Ordinal) && staleGone,
                  "once serve is gone the next session writes vseed.log again and deletes the stale vseed.log.1",
                  "vseed.log.1 " + (staleGone ? "deleted" : "STILL THERE"));

            // ---- the clean's table says what happened to the category, not that it was chosen ------------
            // Only vseed.log is left, and it is the clean's own log: kept, so the row may not say "removed".
            (int e6, string cleanText) = RunToEnd(exe, dir, With(common, "clean", "--what", "logs", "--yes"), env);
            string? logsRow = null;
            foreach (string l in cleanText.Split('\n'))
            {
                if (l.TrimStart().StartsWith("logs ", StringComparison.Ordinal)) logsRow = Flat(l.Trim());
            }

            check(e6 == 0 && logsRow != null && logsRow.Contains("kept (in use)", StringComparison.Ordinal)
                  && !Regex.IsMatch(logsRow, @"\bremoved\b") && Flat(cleanText).Contains("freed 0 B in 0 files", StringComparison.Ordinal),
                  "vseed clean --what logs --yes: a category whose only file is the command's own log reads 'kept (in use)', not 'removed'",
                  logsRow ?? "(no logs row: " + Tail(cleanText) + ")");
            check(Empty(env), "nothing reached the cache root SEEDLAB_CACHE_DIR names", "");
        }

        private static void Page(Action<bool, string, string> check, string url, string resultsDir)
        {
            using HttpClient http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };

            // ---- the cross-site guard -------------------------------------------------------------------
            int foreignCancel = Post(http, url + "/api/search/s0/cancel", "https://evil.example", null, null).Status;
            int foreignRetry = Post(http, url + "/api/search/s0/retry-save", "https://evil.example", "cross-site", null).Status;
            int ownRetry = Post(http, url + "/api/search/s0/retry-save", url, "same-origin", null).Status;
            int curl = Post(http, url + "/api/search/s0/cancel", null, null, null).Status;
            check(foreignCancel == 403 && foreignRetry == 403 && ownRetry == 404 && curl == 404,
                  "THE PAGE: a POST from another origin to Stop or Retry saving is refused (403) before its route; the page's own and curl's reach it (404 for no such run)",
                  "foreign Stop " + foreignCancel + ", foreign Retry saving " + foreignRetry + ", own " + ownRetry + ", no headers " + curl);

            // ---- a results file that is held refuses the search by name ---------------------------------
            Directory.CreateDirectory(resultsDir);
            string heldOut = Path.Combine(resultsDir, "held.jsonl");
            File.WriteAllText(heldOut, "old\n");
            (int Status, string Body) refused;
            using (new FileStream(heldOut, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                refused = Post(http, url + "/api/search", url, "same-origin",
                               Body("hosts-page-held", 64, 0, "\"outName\":\"held.jsonl\","));
            }

            string refusal = Field(refused.Body, "error") ?? "";
            check(refused.Status == 400 && Field(refused.Body, "kind") == "refused" && refusal.Contains(heldOut, StringComparison.Ordinal)
                  && refusal.Contains("press Find seeds again", StringComparison.Ordinal) && File.ReadAllText(heldOut) == "old\n",
                  "a page search whose results file another program holds is refused by name, with the page's way to try again",
                  refused.Status + " " + (refusal.Length > 160 ? refusal.Substring(0, 160) + "..." : refusal));

            // ---- a finished run whose checkpoint is held: a "warning" event --------------------------------
            string body = Body("hosts-page-retire", 64, 0, "");
            string ckpt = Field(Post(http, url + "/api/search/preflight", url, "same-origin", body).Body, "checkpointPath") ?? "";
            if (ckpt.Length == 0)
            {
                check(false, "the preflight names the run's checkpoint (GUARD)", "no checkpointPath");
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(ckpt)!);
            File.WriteAllText(ckpt, "{}");
            string stream;
            using (new FileStream(ckpt, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                (int st, string started) = Post(http, url + "/api/search", url, "same-origin", body);
                stream = st == 200 ? http.GetStringAsync(url + "/api/search/" + Field(started, "id") + "/stream").GetAwaiter().GetResult() : "";
            }

            string? warning = Event(stream, "warning", "message");
            string? done = Event(stream, "done", "retireWarning");
            check(warning != null && warning.Contains("could not be deleted", StringComparison.Ordinal) && done != null,
                  "a page run that finished but could not delete its checkpoint sends a \"warning\" event, and done carries it",
                  warning ?? "no warning event: " + Tail(stream));
            TryDelete(ckpt);

            // ---- a rotated page run that finishes while its manifest is held: finished, not failed ----------
            // Held by a program that lets others write, which the start check passes: the manifest is found
            // held only at the end, when every record is on disk.
            string manifest = Path.Combine(resultsDir, "rot.manifest.json");
            File.WriteAllText(manifest, "{}\n");
            string rotated;
            using (new FileStream(manifest, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                (int st, string started) = Post(http, url + "/api/search", url, "same-origin",
                    Body("hosts-page-rotated", 3000, 0, "\"outName\":\"rot.jsonl\",\"keepAll\":true,\"rotateBytes\":65536,"));
                rotated = st == 200 ? http.GetStringAsync(url + "/api/search/" + Field(started, "id") + "/stream").GetAwaiter().GetResult()
                                    : "(not started: " + st + " " + started + ")";
            }

            string? status = Event(rotated, "done", "status");
            string? resultsError = Event(rotated, "done", "resultsError");
            bool finished = status == "done" && resultsError != null
                            && resultsError.Contains("only the manifest that lists them could not be saved", StringComparison.Ordinal)
                            && rotated.Contains("saving the manifest: ", StringComparison.Ordinal)
                            && Directory.GetFiles(resultsDir, "rot.0*.jsonl.gz").Length >= 1;
            check(finished,
                  "a page run whose manifest is held at the end is published as done (not failed), with the wait and the manifest "
                  + "as warnings and resultsError on done",
                  "status " + (status ?? "(none)") + (finished ? "" : ": " + Tail(rotated)));

            // ---- the last save of a run the budget stopped, held: the error, then Retry saving -------------
            body = Body("hosts-page-last-save", 5_000_000, 2, "");
            ckpt = Field(Post(http, url + "/api/search/preflight", url, "same-origin", body).Body, "checkpointPath") ?? "";
            File.WriteAllText(ckpt, "{}");
            FileStream? holder = new FileStream(ckpt, FileMode.Open, FileAccess.Read, FileShare.Read);
            try
            {
                (int st, string started) = Post(http, url + "/api/search", url, "same-origin", body);
                string id = Field(started, "id") ?? "";
                stream = st == 200 ? http.GetStringAsync(url + "/api/search/" + id + "/stream").GetAwaiter().GetResult() : "";
                string? error = Event(stream, "done", "checkpointError");
                check(error != null && error.Contains("the last checkpoint of this run could not be saved", StringComparison.Ordinal),
                      "THE PAGE's LAST SAVE: held through the end, the done event carries checkpointError with the file and what a resume will do",
                      error ?? "no checkpointError: " + Tail(stream));

                string? problem = Field(error ?? "{}", "problem");
                check(problem == "in_use", "and its problem is spelled as the terminal's --json spells it, \"in_use\"",
                      "problem " + (problem ?? "(none)"));
                check(LastEventType(stream) == "done", "the done event is the last one the stream sends", LastEventType(stream) ?? "(none)");

                (int s1, string r1) = Post(http, url + "/api/search/" + id + "/retry-save", url, "same-origin", null);
                bool stillHeld = s1 == 200 && Field(r1, "saved") == "false" && (Field(r1, "message") ?? "").Contains(ckpt, StringComparison.Ordinal);
                check(stillHeld, "Retry saving while it is still held: not saved, and the reason names the file",
                      s1 + " " + Tail(r1));
                string heldState = http.GetStringAsync(url + "/api/search/" + id).GetAwaiter().GetResult();
                check(Field(Field(heldState, "save") ?? "{}", "checkpointError") != null,
                      "GET /api/search/{id} carries the save as it stands: not saved yet", Tail(heldState));

                holder.Dispose();
                holder = null;
                (int s2, string r2) = Post(http, url + "/api/search/" + id + "/retry-save", url, "same-origin", null);
                Checkpoint? c = null;
                try
                {
                    c = Checkpoint.Load(ckpt);
                }
                catch (Exception)
                {
                }

                bool saved = s2 == 200 && Field(r2, "saved") == "true" && c != null && c.NextBlock > 0
                             && string.Equals(Field(r2, "checkpointPath"), ckpt, StringComparison.OrdinalIgnoreCase);
                check(saved, "Retry saving once it is let go: saved, and the checkpoint on disk is the run's, a resume point",
                      s2 + (c == null ? ", no readable checkpoint" : ", next block " + c.NextBlock) + (saved ? "" : " " + Tail(r2)));

                // A tab that rejoins now is replayed the frozen done event, which still carries the error;
                // the GET is what it draws the box from, and it has followed the retry.
                string savedState = Field(http.GetStringAsync(url + "/api/search/" + id).GetAwaiter().GetResult(), "save") ?? "{}";
                (int s3, string r3) = Post(http, url + "/api/search/" + id + "/retry-save", url, "same-origin", null);
                check(Field(savedState, "checkpointError") == null
                      && string.Equals(Field(savedState, "checkpointPath"), ckpt, StringComparison.OrdinalIgnoreCase)
                      && Field(savedState, "resumeCommand") != null
                      && s3 == 200 && Field(r3, "saved") == "true" && (Field(r3, "message") ?? "").StartsWith("saved: ", StringComparison.Ordinal),
                      "after it: GET /api/search/{id} says saved, with the resume command, and a second Retry saving (another tab) "
                      + "says 'saved', not 'nothing to save'",
                      Tail(savedState) + " / " + Tail(r3));
            }
            finally
            {
                holder?.Dispose();
            }
        }

        // =========================================================================================
        // vseed map -o, opened by a viewer that lets others write - which the start check cannot see -
        // after the check: the wait is said, the image is not left half-written, and the note does not
        // claim that something changed.

        private static void MapHeldAfterTheCheck(Action<bool, string, string> check, string exe, string dir)
        {
            Directory.CreateDirectory(dir);
            string png = Path.Combine(dir, "map.png");
            File.WriteAllBytes(png, new byte[] { 1, 2, 3, 4 });
            (int exit, string text) r;
            using (new FileStream(png, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                r = RunToEnd(exe, dir, new List<string>
                {
                    "map", "1", "--px", "64", "-o", png, "--json", "--cache-dir", Path.Combine(dir, "cache"),
                    "--skip-self-test", "--ignore-running-game",
                }, Path.Combine(dir, "env"));
            }

            r.text = Flat(r.text);
            bool ok = r.exit == 3 && r.text.Contains("saving the map: ", StringComparison.Ordinal)
                      && r.text.Contains("SeedLab could not save " + png, StringComparison.Ordinal)
                      && r.text.Contains("passes that check and still stops SeedLab replacing it", StringComparison.Ordinal)
                      && !r.text.Contains("so something changed after that", StringComparison.Ordinal)
                      && File.ReadAllBytes(png).Length == 4 && Directory.GetFiles(dir, ".seedlab-tmp-*").Length == 0;
            check(ok, "vseed map onto a file a viewer opened sharing writes: the wait is said, then exit 3 naming it and that the start "
                      + "check cannot see such a viewer; the old file whole, no temp left",
                  "exit " + r.exit + (ok ? "" : ": " + Tail(r.text)));
        }

        // =========================================================================================
        // A rotated run that FINISHES while its manifest is held: the report, the records, and an exit
        // code that says the manifest is not what it should be - not the whole run as a failure.

        private static void RotatedManifest(Action<bool, string, string> check, string exe, string root, string query, string dir)
        {
            Directory.CreateDirectory(dir);
            string results = Path.Combine(dir, "rot.jsonl");
            string manifest = Path.Combine(dir, "rot.manifest.json");
            File.WriteAllText(manifest, "{\"an earlier run's manifest\": true}\n");
            List<string> args = Args(query, results, Path.Combine(dir, "rot.ckpt"), Path.Combine(dir, "cache"), "6000");
            args.AddRange(new[] { "--keep", "all", "--rotate", "1MB", "--json" });

            int exit;
            string stdout, stderr;
            using (new FileStream(manifest, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                using Process p = Process.Start(Start(exe, root, args, Path.Combine(dir, "env")))!;
                p.StandardInput.Close();
                Task<string> outTask = p.StandardOutput.ReadToEndAsync();
                Task<string> errTask = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(180_000))
                {
                    p.Kill(entireProcessTree: true);
                    p.WaitForExit(10_000);
                }

                p.WaitForExit();
                exit = p.ExitCode;
                stdout = outTask.Result;
                stderr = Flat(errTask.Result);
            }

            bool complete = false;
            long written = -1;
            string problem = "";
            try
            {
                using JsonDocument j = JsonDocument.Parse(stdout);
                complete = j.RootElement.GetProperty("complete").GetBoolean();
                written = j.RootElement.GetProperty("results_written").GetInt64();
                problem = j.RootElement.GetProperty("results_error").GetProperty("problem").GetString() ?? "";
            }
            catch (Exception ex)
            {
                problem = "(the JSON could not be read: " + ex.Message + ")";
            }

            int segments = Directory.GetFiles(dir, "rot.0*.jsonl.gz").Length;
            bool reported = exit == 3 && complete && written > 0 && problem == "in_use" && segments >= 2
                            && stderr.Contains("saving the manifest: " + manifest + " is busy", StringComparison.Ordinal)
                            && stderr.Contains("only the manifest that lists them could not be saved", StringComparison.Ordinal);
            check(reported,
                  "THE MANIFEST: a rotated run that finishes while its manifest is held prints its report (complete, the records "
                  + "written, results_error in_use), says the wait and that only the manifest failed, and exits 3",
                  "exit " + exit + ", complete " + complete + ", " + written + " records, " + segments + " segments, problem "
                  + problem + (reported ? "" : ": " + Tail(stderr)));
        }

        /// <summary>The type of the last event on an SSE stream, or null.</summary>
        private static string? LastEventType(string stream)
        {
            string? last = null;
            foreach (string chunk in stream.Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
            {
                if (chunk.StartsWith("event: ", StringComparison.Ordinal))
                {
                    int nl = chunk.IndexOf('\n');
                    last = nl < 0 ? chunk.Substring(7) : chunk.Substring(7, nl - 7);
                }
            }

            return last;
        }

        // =========================================================================================
        // Helpers.

        /// <summary>The built vseed, fresh against this build's SeedLab.Search; null (and said) when it is not.</summary>
        private static string? Vseed(Action<bool, string, string> check, out string root)
        {
            root = "";
            for (DirectoryInfo? d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            {
                if (Directory.Exists(Path.Combine(d.FullName, "src", "SeedLab.Cli"))) { root = d.FullName; break; }
            }

            string exe = root.Length == 0 ? "" : Path.Combine(root, "src", "SeedLab.Cli", "bin", "Release", "net10.0",
                                                              OperatingSystem.IsWindows() ? "vseed.exe" : "vseed");
            bool built = File.Exists(exe);
            check(built, "the Release vseed is built, for these end-to-end checks",
                  built ? exe : "build src\\SeedLab.Cli -c Release first - the checks below are SKIPPED, not passed");
            if (!built) return null;

            string mine = Path.Combine(AppContext.BaseDirectory, "SeedLab.Search.dll");
            string theirs = Path.Combine(Path.GetDirectoryName(exe)!, "SeedLab.Search.dll");
            bool fresh = File.Exists(theirs) && Same(SHA256.HashData(File.ReadAllBytes(mine)), SHA256.HashData(File.ReadAllBytes(theirs)));
            check(fresh, "that vseed carries the same SeedLab.Search as these tests",
                  fresh ? "SHA-256 equal" : "rebuild src\\SeedLab.Cli -c Release - the checks below are SKIPPED, not passed");
            return fresh ? exe : null;
        }

        /// <summary>The cheap query's search on one worker in blocks of 16, a checkpoint every half second.</summary>
        private static List<string> Args(string query, string results, string checkpoint, string cache, string seeds) => new List<string>
        {
            "search", query, "--seeds", seeds, "--threads", "1", "--block-size", "16", "--out", results,
            "--checkpoint", checkpoint, "--checkpoint-every", "0.5", "--yes", "--progress", "none",
            "--cache-dir", cache, "--skip-self-test", "--ignore-running-game",
        };

        private static List<string> With(List<string> common, params string[] args)
        {
            List<string> l = new List<string>(args);
            l.AddRange(common);
            return l;
        }

        /// <summary>A page query: the cheap goals, a seed budget, a time budget, and anything else.</summary>
        private static string Body(string name, long seeds, int seconds, string extra) =>
            "{\"name\":\"" + name + "\",\"goals\":[" + PageGoals + "]," + extra
            + "\"budgetSeeds\":" + seeds + ",\"budgetSeconds\":" + seconds + ",\"keep\":5,\"gridSpacingM\":384,"
            + "\"threads\":1,\"blockSize\":16,\"order\":\"shuffled\",\"screen\":\"off\",\"strategy\":\"sample\","
            + "\"confirmed\":true,\"acceptScanOrder\":true,\"ignoreRunningGame\":true}";

        private static (int Status, string Body) Post(HttpClient http, string url, string? origin, string? site, string? json)
        {
            using HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Post, url);
            if (json != null) req.Content = new StringContent(json, Encoding.UTF8, "application/json");
            if (origin != null) req.Headers.TryAddWithoutValidation("Origin", origin);
            if (site != null) req.Headers.TryAddWithoutValidation("Sec-Fetch-Site", site);
            using HttpResponseMessage res = http.Send(req);
            return ((int)res.StatusCode, res.Content.ReadAsStringAsync().GetAwaiter().GetResult());
        }

        /// <summary>A top-level field of a JSON object as text ("true", a string's value, a raw number), or null.</summary>
        private static string? Field(string json, string name)
        {
            try
            {
                using JsonDocument d = JsonDocument.Parse(json);
                if (!d.RootElement.TryGetProperty(name, out JsonElement e) || e.ValueKind == JsonValueKind.Null) return null;
                return e.ValueKind == JsonValueKind.String ? e.GetString() : e.ValueKind == JsonValueKind.Object ? e.GetRawText()
                     : e.ValueKind == JsonValueKind.True ? "true" : e.ValueKind == JsonValueKind.False ? "false" : e.GetRawText();
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>A field of the first event of that type on an SSE stream, as <see cref="Field"/> gives it.</summary>
        private static string? Event(string stream, string type, string field)
        {
            foreach (string chunk in stream.Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
            {
                if (!chunk.StartsWith("event: " + type + "\n", StringComparison.Ordinal)) continue;
                int at = chunk.IndexOf("\ndata: ", StringComparison.Ordinal);
                if (at < 0) continue;
                string? v = Field(chunk.Substring(at + 7), field);
                if (v != null) return v;
            }

            return null;
        }

        /// <summary>
        /// A vseed child, with stdin closed - nothing is reading a keyboard, as in a script - and
        /// SEEDLAB_CACHE_DIR at a root nothing is meant to reach.
        /// </summary>
        private static ProcessStartInfo Start(string exe, string workDir, List<string> args, string fallback)
        {
            ProcessStartInfo psi = new ProcessStartInfo(exe)
            {
                WorkingDirectory = workDir,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (string a in args) psi.ArgumentList.Add(a);
            psi.Environment[CacheVariable] = fallback;
            return psi;
        }

        private static (int Exit, string Text) RunToEnd(string exe, string workDir, List<string> args, string fallback,
                                                        bool stdoutOnly = false)
        {
            using Process p = Process.Start(Start(exe, workDir, args, fallback))!;
            p.StandardInput.Close();
            Task<string> stdout = p.StandardOutput.ReadToEndAsync();
            Task<string> stderr = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(180_000))
            {
                try { p.Kill(entireProcessTree: true); } catch (Exception) { }
                p.WaitForExit(10_000);
                return (-1, "(timed out)\n" + stderr.Result + stdout.Result);
            }

            p.WaitForExit();
            return (p.ExitCode, stdoutOnly ? stdout.Result : stderr.Result + "\n" + stdout.Result);
        }

        /// <summary>vseed.log (0) or vseed.log.N, read sharing read and write as a live log needs; "" when absent.</summary>
        private static string ReadLog(string cache, int index)
        {
            string p = Path.Combine(cache, "logs", index == 0 ? "vseed.log" : "vseed.log." + index);
            if (!File.Exists(p)) return "";
            using FileStream fs = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using StreamReader r = new StreamReader(fs, Encoding.UTF8);
            return r.ReadToEnd().Replace("\r\n", "\n");
        }

        private static string Url(string text) => (LineWith(text, "SeedLab is serving at ") ?? "").Replace("SeedLab is serving at ", "").Trim();

        private static string? LineWith(string text, string part)
        {
            foreach (string l in text.Split('\n'))
            {
                if (l.Contains(part, StringComparison.Ordinal)) return l.TrimEnd('\r');
            }

            return null;
        }

        private static string Joined(List<string> lines)
        {
            lock (lines) return string.Join("\n", lines);
        }

        /// <summary>Terminal text with its line wrapping undone, so a sentence can be looked for whole.</summary>
        private static string Flat(string text) => Regex.Replace(text, @"\s+", " ");

        private static string Tail(string text)
            => text.Length <= 600 ? text.Replace('\n', ' ') : "..." + text.Substring(text.Length - 600).Replace('\n', ' ');

        private static bool Empty(string dir)
            => !Directory.Exists(dir) || Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Length == 0;

        private static bool Same(byte[] a, byte[] b) => a.AsSpan().SequenceEqual(b);

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception)
            {
            }
        }

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
    }
}
