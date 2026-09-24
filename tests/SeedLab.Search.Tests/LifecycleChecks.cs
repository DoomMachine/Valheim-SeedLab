using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SeedLab.Search.Execution;

namespace SeedLab.SearchTests
{
    /// <summary>
    /// The web server's lifecycle (2026-09-24, as the user confirmed it), end to end against the built
    /// <c>vseed</c>: <c>--status</c> and <c>--stop</c> with nothing running create nothing; a running server
    /// is found, a second <c>vseed serve</c> opens it and exits 0; the stop endpoint refuses a request
    /// without the token and one from another page; the idle reminder comes after the test's period of
    /// no use, the page's own polling does not hold it off, Keep running re-arms it and use withdraws it;
    /// <c>--stop</c> with a search running refuses without <c>--yes</c> when nothing can answer, and with
    /// it stops the search at once, the checkpoint on disk resuming to the end; the two session logs
    /// (this one and the last one), a live session's <c>.1</c>, and a held <c>vseed-prev.log</c>; and,
    /// on Windows, Ctrl+C pressed once (nothing stops) and twice (the graceful stop) in the server's own
    /// console, and a third time while it stops.
    ///
    /// <para>Since the review of 2026-09-25 also: a left-over registry file is not a server; a run's query
    /// file is saved beside its checkpoint and the resume command names it; starting a query again when a
    /// stopped run of it left a resume point is refused until the user says to replace it; a funnel stopped
    /// in its first stage is warned about as losing its work, never as saved; and, on Windows, the end of the
    /// session (WM_ENDSESSION to the server's hidden window) is the graceful stop.</para>
    ///
    /// <para>Every child gets <c>--cache-dir</c> under a temp folder and <c>SEEDLAB_CACHE_DIR</c> pointing
    /// at a second one that must stay empty; the user's own cache root is never touched. The idle
    /// reminder's period is made seconds with <c>SEEDLAB_TEST_IDLE_REMINDER_SECONDS</c>, which only a
    /// test sets.</para>
    /// </summary>
    public static class LifecycleChecks
    {
        private const string IdleVariable = "SEEDLAB_TEST_IDLE_REMINDER_SECONDS";

        private const string PageGoals =
            @"{""target"":""biome:Meadows"",""metric"":""area_within"",""radius"":2000,""test"":""at_least"",""value"":1000000,""importance"":""must""},
              {""target"":""biome:Swamp"",""metric"":""area_within"",""radius"":2000,""test"":""at_least"",""value"":100000,""importance"":""nice""}";

        public static void Run(Action<bool, string, string> check)
        {
            string dir = Path.Combine(Path.GetTempPath(), "seedlab-lifecycle-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            dir = Path.GetFullPath(dir);
            try
            {
                string? exe = HostChecks.Vseed(check, out string repo);
                if (exe == null) return;
                string env = Path.Combine(dir, "env");

                NothingRunning(check, exe, dir, env);
                Server(check, exe, Path.Combine(dir, "server"), env);
                ResumePoints(check, exe, repo, Path.Combine(dir, "resume"), env);
                if (OperatingSystem.IsWindows()) CtrlC(check, exe, Path.Combine(dir, "ctrlc"), env);
                else check(true, "Ctrl+C in the server's console (SKIPPED: the helper that sends a real Ctrl+C is Windows-only)", "");
                check(HostChecks.Empty(env), "nothing reached the cache root SEEDLAB_CACHE_DIR names", "");
            }
            finally
            {
                HostChecks.DeleteTree(dir);
            }
        }

        // =========================================================================================
        // Nothing running: --status and --stop answer, and create nothing - not even the cache root.

        private static void NothingRunning(Action<bool, string, string> check, string exe, string dir, string env)
        {
            string cache = Path.Combine(dir, "never-made");
            (int se, string st) = HostChecks.RunToEnd(exe, dir, new List<string> { "serve", "--status", "--cache-dir", cache }, env);
            (int je, string js) = HostChecks.RunToEnd(exe, dir, new List<string> { "serve", "--status", "--json", "--cache-dir", cache },
                                                      env, stdoutOnly: true);
            (int pe, string pt) = HostChecks.RunToEnd(exe, dir, new List<string> { "serve", "--stop", "--cache-dir", cache }, env);
            check(se == 1 && st.Contains("SeedLab's web server is not running.", StringComparison.Ordinal),
                  "STATUS: --status with no server says it is not running, exit 1", "exit " + se + ": " + HostChecks.Tail(st.Trim()));
            check(je == 1 && HostChecks.Field(js, "running") == "false", "and --status --json says running: false", "exit " + je);
            check(pe == 0 && pt.Contains("SeedLab's web server is not running - nothing to stop.", StringComparison.Ordinal),
                  "--stop with no server says there is nothing to stop, exit 0", "exit " + pe + ": " + HostChecks.Tail(pt.Trim()));
            check(!Directory.Exists(cache) && HostChecks.Empty(env),
                  "neither created the cache root, a log, a self-test stamp or anything else", Directory.Exists(cache) ? "CREATED " + cache : "nothing");

            // ---- a left-over registry file is not a server (review of 2026-09-25) ---------------------------
            // One naming a system process whose start time this account cannot read (pid 4 on Windows), and one
            // naming this very process - alive, start time right - with an address that is a program, not
            // SeedLab's. Both used to count; the first locked the user out of starting, stopping and uninstalling.
            string planted = Path.Combine(dir, "planted");
            string plantedServe = Path.Combine(planted, "serve");
            Directory.CreateDirectory(plantedServe);
            int systemPid = OperatingSystem.IsWindows() ? 4 : 1;
            string sysFile = Path.Combine(plantedServe, "server-" + systemPid + ".json");
            File.WriteAllText(sysFile, RegistryJson(systemPid, "2020-01-01T00:00:00.0000000Z", 1, "http://127.0.0.1:1"));
            string meStart;
            using (Process me = Process.GetCurrentProcess()) meStart = me.StartTime.ToUniversalTime().ToString("o", System.Globalization.CultureInfo.InvariantCulture);
            string calcFile = Path.Combine(plantedServe, "server-" + Environment.ProcessId + ".json");
            File.WriteAllText(calcFile, RegistryJson(Environment.ProcessId, meStart, 8731, @"C:\Windows\System32\calc.exe"));
            (int ls, string lt) = HostChecks.RunToEnd(exe, dir, new List<string> { "serve", "--status", "--cache-dir", planted }, env);
            (int lp, string lpt) = HostChecks.RunToEnd(exe, dir, new List<string> { "serve", "--stop", "--cache-dir", planted }, env);
            string flatLs = HostChecks.Flat(lt);
            check(ls == 1 && flatLs.Contains("SeedLab's web server is not running.", StringComparison.Ordinal)
                  && !flatLs.Contains("is running at", StringComparison.Ordinal) && !flatLs.Contains("calc.exe", StringComparison.Ordinal)
                  && lt.Contains(sysFile, StringComparison.Ordinal) && lt.Contains(calcFile, StringComparison.Ordinal),
                  "LEFT-OVER FILES: a registry file naming a system process, and one whose address is a program, are no server: "
                  + "--status says not running (exit 1) and names both files as safe to delete",
                  "exit " + ls + ": " + HostChecks.Tail(flatLs));
            check(lp == 0 && HostChecks.Flat(lpt).Contains("nothing to stop", StringComparison.Ordinal)
                  && !HostChecks.Flat(lpt).Contains("did not answer", StringComparison.Ordinal),
                  "and --stop says there is nothing to stop (exit 0) instead of \"did not answer ... add --force\"",
                  "exit " + lp + ": " + HostChecks.Tail(HostChecks.Flat(lpt)));
            check(File.Exists(sysFile) && File.Exists(calcFile) && !Directory.Exists(Path.Combine(planted, "logs")) && HostChecks.Empty(env),
                  "and neither deleted a file nor created anything", "");

            (int he, string help) = HostChecks.RunToEnd(exe, dir, new List<string> { "serve", "--help" }, env, stdoutOnly: true);
            check(he == 0 && help.Contains("  --status  ", StringComparison.Ordinal) && help.Contains("  --stop  ", StringComparison.Ordinal)
                  && help.Contains("--idle-reminder <min>", StringComparison.Ordinal),
                  "vseed serve --help lists --status and --stop literally (the scripts look for them there) and --idle-reminder", "");
        }

        // =========================================================================================
        // One server, from start to stop.

        private static void Server(Action<bool, string, string> check, string exe, string dir, string env)
        {
            Directory.CreateDirectory(dir);
            string cache = Path.Combine(dir, "cache");
            string logs = Path.Combine(cache, "logs");
            List<string> common = new List<string> { "--cache-dir", cache, "--skip-self-test", "--ignore-running-game" };

            // ---- the two logs, before any server --------------------------------------------------------
            (int a1, _) = HostChecks.RunToEnd(exe, dir, With(common, "at", "1", "0", "0"), env);
            (int a2, _) = HostChecks.RunToEnd(exe, dir, With(common, "at", "2", "0", "0"), env);
            check(a1 == 0 && a2 == 0 && HostChecks.ReadLog(cache, 0).Contains("command  vseed at 2 0 0", StringComparison.Ordinal)
                  && Prev(cache).Contains("command  vseed at 1 0 0", StringComparison.Ordinal)
                  && !Prev(cache).Contains("vseed at 2 0 0", StringComparison.Ordinal),
                  "LOGS: each session starts vseed.log and keeps the last one's as vseed-prev.log",
                  "vseed.log: at 2, vseed-prev.log: at 1");

            // ---- the server --------------------------------------------------------------------------
            ProcessStartInfo psi = HostChecks.Start(exe, dir, With(common, "serve", "--port", "0", "--no-browser"), env);
            psi.Environment[IdleVariable] = "3";
            List<string> lines = new List<string>();
            ManualResetEventSlim ready = new ManualResetEventSlim(false);
            using Process serve = new Process { StartInfo = psi };
            serve.OutputDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                lock (lines) lines.Add(e.Data);
                if (e.Data.Contains("SeedLab is ready.", StringComparison.Ordinal)) ready.Set();
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
                string text = HostChecks.Joined(lines);
                check(up, "vseed serve starts (GUARD)", up ? "" : HostChecks.Tail(text));
                if (!up) return;

                string url = (HostChecks.LineWith(text, "SeedLab is serving at ") ?? "").Replace("SeedLab is serving at ", "").Trim();
                check(text.StartsWith(new string('=', 78) + "\n This window IS SeedLab's web server.", StringComparison.Ordinal)
                      && HostChecks.Flat(text).Contains("or press Ctrl+C in THIS window twice.", StringComparison.Ordinal),
                      "BANNER: the first thing the server prints says the window IS the server and how to stop it",
                      HostChecks.Tail(text.Substring(0, Math.Min(text.Length, 330))));

                string[] registry = Directory.GetFiles(Path.Combine(cache, "serve"), "server-*.json");
                string token = registry.Length == 1 ? HostChecks.Field(File.ReadAllText(registry[0]), "token") ?? "" : "";
                check(registry.Length == 1 && HostChecks.Field(File.ReadAllText(registry[0]), "url") == url && token.Length == 64,
                      "REGISTRY: the server wrote serve\\server-<pid>.json with its address and its token",
                      registry.Length + " file(s)");
                check(HostChecks.ReadLog(cache, 0).Contains("command  vseed serve", StringComparison.Ordinal)
                      && Prev(cache).Contains("command  vseed at 2 0 0", StringComparison.Ordinal),
                      "LOGS: the server's session renamed the last log too", "vseed-prev.log: at 2");

                // ---- --status, and a second serve ------------------------------------------------------
                (int s1, string st1) = HostChecks.RunToEnd(exe, dir, With(common, "serve", "--status"), env);
                check(s1 == 0 && HostChecks.Flat(st1).Contains("SeedLab's web server is running at " + url + " since ", StringComparison.Ordinal)
                      && HostChecks.Flat(st1).Contains("no search is running.", StringComparison.Ordinal),
                      "STATUS: --status finds it: its address, since when, its pid, no search running; exit 0",
                      "exit " + s1 + ": " + HostChecks.Flat(st1).Trim());

                string servingLog = HostChecks.ReadLog(cache, 0);
                Stopwatch sw = Stopwatch.StartNew();
                (int s2, string st2) = HostChecks.RunToEnd(exe, dir, With(common, "serve", "--no-browser"), env);
                long secondMs = sw.ElapsedMilliseconds;
                check(s2 == 0 && HostChecks.Flat(st2).Contains("SeedLab is already running at " + url + " (since ", StringComparison.Ordinal)
                      && HostChecks.Flat(st2).Contains("open that address in your browser", StringComparison.Ordinal),
                      "SECOND SERVE: a second 'vseed serve' says where the running one is and exits 0 (with --no-browser it opens nothing)",
                      "exit " + s2 + " in " + secondMs + " ms: " + HostChecks.Flat(st2).Trim());
                check(Directory.GetFiles(Path.Combine(cache, "serve"), "server-*.json").Length == 1
                      && HostChecks.ReadLog(cache, 0) == servingLog && !File.Exists(Path.Combine(logs, "vseed.log.1")),
                      "and it started no runtime: no second registry file, no session log of its own, the server's log untouched", "");

                // ---- a server another cache root does not know about (Windows lists it by its command line) --------
                string otherRoot = Path.Combine(dir, "other-root");
                (int s4, string st4) = HostChecks.RunToEnd(exe, dir, new List<string> { "serve", "--status", "--cache-dir", otherRoot }, env);
                string flat4 = HostChecks.Flat(st4);
                if (OperatingSystem.IsWindows())
                {
                    check(s4 == 1 && flat4.Contains("SeedLab's web server is not running.", StringComparison.Ordinal)
                          && flat4.Contains("does not know about:", StringComparison.Ordinal)
                          && flat4.Contains("pid " + serve.Id + ", started ", StringComparison.Ordinal)
                          && flat4.Contains(" serve ", StringComparison.Ordinal) && !Directory.Exists(otherRoot),
                          "UNREGISTERED: --status with another --cache-dir says not running there, and lists this server by its pid and "
                          + "command line, with how to find or stop it; it creates nothing",
                          "exit " + s4 + ": " + HostChecks.Tail(flat4));
                }
                else
                {
                    check(s4 == 1 && !Directory.Exists(otherRoot),
                          "UNREGISTERED: --status with another --cache-dir says not running there (the process listing is Windows-only)",
                          "exit " + s4);
                }

                // ---- a server that cannot start, alone in a window: it waits for Enter so the reason can be read ----
                if (OperatingSystem.IsWindows()) PauseOnFailure(check, exe, dir, env, new Uri(url).Port);

                // ---- a concurrent session writes .1 and renames nothing ---------------------------------------
                string prevBefore = Prev(cache);
                (int a3, _) = HostChecks.RunToEnd(exe, dir, With(common, "at", "3", "0", "0"), env);
                check(a3 == 0 && HostChecks.ReadLog(cache, 1).Contains("command  vseed at 3 0 0", StringComparison.Ordinal)
                      && HostChecks.ReadLog(cache, 0).Contains("command  vseed serve", StringComparison.Ordinal) && Prev(cache) == prevBefore,
                      "LOGS: a command run while the server runs writes vseed.log.1 and leaves vseed.log and vseed-prev.log alone", "");

                using HttpClient http = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromMinutes(3) };

                // ---- the stop endpoint's guards ----------------------------------------------------------
                int noToken = Post(http, url + "/api/server/stop", null, null, "{\"stopSearch\":true}").Status;
                int wrongToken = Post(http, url + "/api/server/stop", new string('0', 64), null, "{\"stopSearch\":true}").Status;
                int foreign = Post(http, url + "/api/server/stop", token, "https://evil.example", "{\"stopSearch\":true}").Status;
                int keepNoToken = Post(http, url + "/api/server/keep-alive", null, null, null).Status;
                check(noToken == 401 && wrongToken == 403 && foreign == 403 && keepNoToken == 401 && !serve.HasExited,
                      "GUARDS: Stop SeedLab without the token 401, with another token 403, with the token from another page 403; "
                      + "Keep running without the token 401; the server still runs",
                      "no token " + noToken + ", another " + wrongToken + ", another page " + foreign + ", keep-alive " + keepNoToken);

                // ---- the idle reminder (3 s here) ---------------------------------------------------------
                Idle(check, http, url, token, lines);

                // ---- a search running: --status, 409, --stop without and with --yes ------------------------------
                string body = "{\"name\":\"lifecycle-stop\",\"goals\":[" + PageGoals + "],\"budgetSeeds\":20000,\"budgetSeconds\":0,"
                              + "\"keep\":5,\"gridSpacingM\":384,\"threads\":1,\"blockSize\":16,\"order\":\"shuffled\",\"screen\":\"off\","
                              + "\"strategy\":\"sample\",\"confirmed\":true,\"acceptScanOrder\":true,\"ignoreRunningGame\":true}";
                (int ss, string started) = Post(http, url + "/api/search", null, null, body, url);
                string id = HostChecks.Field(started, "id") ?? "";
                check(ss == 200 && id.Length > 0, "a page search starts (GUARD)", ss + " " + HostChecks.Tail(started));
                if (id.Length == 0) return;
                Task<string> stream = http.GetStringAsync(url + "/api/search/" + id + "/stream");

                long scanned = 0;
                Stopwatch w = Stopwatch.StartNew();
                while (scanned < 64 && w.Elapsed < TimeSpan.FromSeconds(30))
                {
                    Thread.Sleep(100);
                    string state = http.GetStringAsync(url + "/api/search/" + id).GetAwaiter().GetResult();
                    long.TryParse(HostChecks.Field(HostChecks.Field(state, "progress") ?? "{}", "scanned") ?? "0", out scanned);
                }

                (int s3, string st3) = HostChecks.RunToEnd(exe, dir, With(common, "serve", "--status"), env);
                check(s3 == 0 && HostChecks.Flat(st3).Contains("a search is running: \"lifecycle-stop\", ", StringComparison.Ordinal)
                      && HostChecks.Flat(st3).Contains(" % done (", StringComparison.Ordinal),
                      "STATUS with a search running names it and how far it has got", HostChecks.Flat(st3).Trim());

                (int c409, string b409) = Post(http, url + "/api/server/stop", token, null, "{\"stopSearch\":false,\"by\":\"page\"}", url);
                string searches = SearchesOf(b409);
                check(c409 == 409 && HostChecks.Field(b409, "kind") == "search-running"
                      && searches.Contains("\"name\":\"lifecycle-stop\"", StringComparison.Ordinal)
                      && searches.Contains("--resume --checkpoint", StringComparison.Ordinal) && !serve.HasExited,
                      "THE SECOND WARNING: Stop SeedLab without stopSearch while a search runs is 409, with its name, progress and resume command",
                      c409 + " " + HostChecks.Tail(b409));

                (int p1, string pt1) = HostChecks.RunToEnd(exe, dir, With(common, "serve", "--stop"), env);
                Thread.Sleep(300);
                check(p1 == 1 && HostChecks.Flat(pt1).Contains("Nothing is here to answer \"stop anyway?\" (stdin is redirected), so SeedLab was left running. Add --yes", StringComparison.Ordinal)
                      && !serve.HasExited,
                      "--stop with a search running and nothing to answer it refuses (exit 1), says to add --yes, and leaves it running",
                      "exit " + p1 + ": " + HostChecks.Tail(HostChecks.Flat(pt1)));

                sw.Restart();
                (int p2, string pt2) = HostChecks.RunToEnd(exe, dir, With(common, "serve", "--stop", "--yes"), env);
                long stopMs = sw.ElapsedMilliseconds;
                bool exited = serve.WaitForExit(15_000);
                string flat2 = HostChecks.Flat(pt2);
                check(p2 == 0 && flat2.Contains("SeedLab's web server has stopped.", StringComparison.Ordinal)
                      && flat2.Contains("The search \"lifecycle-stop\" stopped after", StringComparison.Ordinal)
                      && flat2.Contains("Its checkpoint: ", StringComparison.Ordinal) && exited && serve.ExitCode == 0,
                      "--stop --yes stops it: the search at once, then the server, which exits 0",
                      "exit " + p2 + " in " + stopMs + " ms; server " + (exited ? "exited " + serve.ExitCode : "STILL RUNNING") + ": "
                      + HostChecks.Tail(flat2));
                check(Directory.GetFiles(Path.Combine(cache, "serve"), "server-*.json").Length == 0,
                      "and its registry file is gone", "");

                string streamText = stream.Wait(TimeSpan.FromSeconds(10)) ? stream.Result : "";
                string? doneStatus = HostChecks.Event(streamText, "done", "status");
                string? doneMessage = HostChecks.Event(streamText, "done", "message");
                string? ckpt = HostChecks.Event(streamText, "done", "checkpointPath");
                check(doneStatus == "cancelled" && (doneMessage ?? "").Contains("SeedLab's web server was stopped ('vseed serve --stop' asked it to)", StringComparison.Ordinal),
                      "the page's stream ends with a done event that says the server was stopped, and by what",
                      (doneStatus ?? "(no done)") + ": " + (doneMessage ?? ""));

                Checkpoint? c = null;
                try { c = ckpt != null ? Checkpoint.Load(ckpt) : null; } catch (Exception) { }
                check(c != null && c.NextBlock > 0 && flat2.Contains(ckpt!, StringComparison.Ordinal),
                      "its checkpoint is on disk, at the block the run had reached, and --stop named it",
                      c == null ? "NO CHECKPOINT at " + (ckpt ?? "(none)") : "next block " + c.NextBlock + " of " + (20000 / 16) + ", " + ckpt);

                // ---- the query file beside the checkpoint, named by every resume command (review of 2026-09-25) ----
                string? queryJson = HostChecks.Event(streamText, "started", "queryJson");
                string? resumeCmd = HostChecks.Event(streamText, "done", "resumeCommand");
                string sidecar = (ckpt ?? "") + ".query.json";
                check(c != null && queryJson != null && File.Exists(sidecar) && File.ReadAllText(sidecar) == queryJson
                      && resumeCmd == "vseed search \"" + sidecar + "\" --resume --checkpoint \"" + ckpt + "\""
                      && pt2.Contains(resumeCmd, StringComparison.Ordinal),
                      "QUERY FILE: the run's query file is saved beside its checkpoint, and the resume command the page and --stop "
                      + "print names it - it works as printed, with nothing saved by hand first",
                      resumeCmd ?? "(no resume command)");

                // ---- the checkpoint resumes, the terminal's way, with the command as printed -------------------
                if (c != null && queryJson != null && File.Exists(sidecar))
                {
                    List<string> resume = With(common, "search", sidecar, "--resume", "--checkpoint", ckpt!, "--yes", "--progress", "none", "--json");
                    (int re, string rt) = HostChecks.RunToEnd(exe, dir, resume, env);
                    string flatR = HostChecks.Flat(rt);
                    check(re == 0 && flatR.Contains("resuming from " + ckpt, StringComparison.Ordinal)
                          && flatR.Contains("\"complete\": true", StringComparison.Ordinal) && !File.Exists(ckpt) && !File.Exists(sidecar),
                          "RESUME: that command resumes in the terminal and runs to the end; the checkpoint and its query file are retired",
                          "exit " + re + (re == 0 ? "" : ": " + HostChecks.Tail(flatR)));
                }
                else
                {
                    check(false, "RESUME (SKIPPED: no checkpoint or no query file above)", "");
                }

                // ---- the stopped server's log, and a held vseed-prev.log --------------------------------------
                string prev = Prev(cache);
                check(prev.Contains("serve    stopping: 'vseed serve --stop' asked it to", StringComparison.Ordinal)
                      && prev.Contains("the search \"lifecycle-stop\" stopped after", StringComparison.Ordinal)
                      && prev.Contains("serve    stopped: ", StringComparison.Ordinal),
                      "LOGS: the server's log - now vseed-prev.log - records the stop, why, and the search it stopped", "");

                string heldAbove = HostChecks.ReadLog(cache, 0);
                string prevHeld = Prev(cache);
                int a5;
                using (new FileStream(Path.Combine(logs, "vseed-prev.log"), FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    (a5, _) = HostChecks.RunToEnd(exe, dir, With(common, "at", "5", "0", "0"), env);
                }

                string now = HostChecks.ReadLog(cache, 0);
                check(a5 == 0 && Prev(cache) == prevHeld && now.StartsWith(heldAbove, StringComparison.Ordinal)
                      && now.Contains("could not be replaced - another program has it open - so it was kept as it is", StringComparison.Ordinal)
                      && now.Contains("command  vseed at 5 0 0", StringComparison.Ordinal),
                      "LOGS: with vseed-prev.log held by another program it is kept; the last session's lines stay in vseed.log above the new ones, and the log says why",
                      "vseed-prev.log " + (Prev(cache) == prevHeld ? "unchanged" : "CHANGED"));
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
        }

        /// <summary>
        /// A second server on a port the first one holds, with another cache root (so it is not "already
        /// running"): it cannot bind. Started the way the Windows script starts it - by the shell, alone in a
        /// console of its own (hidden here), whose keyboard is its input - it must not vanish with the
        /// reason: it writes its session log's last line ("with exit code 3") and then waits for Enter. Its
        /// output cannot be read from a shell-started process, so the proof is that log line plus the process
        /// still being there afterwards. With nothing to press Enter (stdin redirected, as everywhere else in
        /// these tests) it exits at once.
        /// </summary>
        private static void PauseOnFailure(Action<bool, string, string> check, string exe, string dir, string env, int port)
        {
            string cache = Path.Combine(dir, "cache-busy-port");
            List<string> args = new List<string>
            {
                "serve", "--port", port.ToString(System.Globalization.CultureInfo.InvariantCulture), "--no-browser",
                "--cache-dir", cache, "--skip-self-test", "--ignore-running-game",
            };

            // Through the shell, as Start-Process does: a new console, its keyboard as stdin. A redirected
            // stream would hand the child this process's own stdin, which is not a keyboard.
            ProcessStartInfo alone = new ProcessStartInfo(exe)
            {
                WorkingDirectory = dir,
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            foreach (string x in args) alone.ArgumentList.Add(x);

            string? before = Environment.GetEnvironmentVariable("SEEDLAB_CACHE_DIR");
            Process? p = null;
            try
            {
                Environment.SetEnvironmentVariable("SEEDLAB_CACHE_DIR", env);
                p = Process.Start(alone);
            }
            finally
            {
                Environment.SetEnvironmentVariable("SEEDLAB_CACHE_DIR", before);
            }

            string log = "";
            Stopwatch sw = Stopwatch.StartNew();
            while (sw.Elapsed < TimeSpan.FromSeconds(60) && !log.Contains("with exit code", StringComparison.Ordinal))
            {
                Thread.Sleep(200);
                log = HostChecks.ReadLog(cache, 0);
            }

            Thread.Sleep(1500);
            bool stillThere = p != null && !p.HasExited;
            try
            {
                if (p != null && !p.HasExited) p.Kill();
                p?.WaitForExit(10_000);
            }
            catch (Exception)
            {
            }
            finally
            {
                p?.Dispose();
            }

            check(log.Contains("could not bind 127.0.0.1:" + port, StringComparison.Ordinal)
                  && log.Contains("with exit code 3", StringComparison.Ordinal) && stillThere,
                  "WINDOW: a server alone in its window that cannot start logs why and ends, then waits for Enter instead of vanishing",
                  (stillThere ? "still waiting 1.5 s after its last log line" : "GONE") + "; log " + (log.Contains("with exit code 3", StringComparison.Ordinal) ? "ends with exit code 3" : "INCOMPLETE"));

            (int code, string t2) = HostChecks.RunToEnd(exe, dir, args, env);
            check(code == 3 && !t2.Contains("Press Enter", StringComparison.Ordinal),
                  "and with no one to press Enter (stdin redirected) it exits at once, 3", "exit " + code);
        }

        private static void Idle(Action<bool, string, string> check, HttpClient http, string url, string token, List<string> lines)
        {
            // Polled the way the page polls: /api/runtime is not use, so the reminder must come all the same.
            Stopwatch sw = Stopwatch.StartNew();
            string rt = "";
            int polls = 0;
            while (sw.Elapsed < TimeSpan.FromSeconds(15))
            {
                rt = http.GetStringAsync(url + "/api/runtime").GetAwaiter().GetResult();
                polls++;
                if (IdleField(rt, "reminder") == "true") break;
                Thread.Sleep(250);
            }

            check(IdleField(rt, "reminder") == "true",
                  "IDLE: after the period (3 s here) with no use, /api/runtime carries the reminder - " + polls + " polls of it did not count as use",
                  "after " + sw.ElapsedMilliseconds + " ms: " + (HostChecks.Field(rt, "idle") ?? "(no idle field)"));
            check(Wait(() => HostChecks.Joined(lines).Contains("SeedLab hasn't been used for ", StringComparison.Ordinal), 3000),
                  "and the server's window prints it", HostChecks.LineWith(HostChecks.Joined(lines), "hasn't been used") ?? "(not printed)");

            (int k, _) = Post(http, url + "/api/server/keep-alive", token, null, null, url);
            string afterKeep = http.GetStringAsync(url + "/api/runtime").GetAwaiter().GetResult();
            Stopwatch again = Stopwatch.StartNew();
            string rt2 = afterKeep;
            while (again.Elapsed < TimeSpan.FromSeconds(15) && IdleField(rt2, "reminder") != "true")
            {
                Thread.Sleep(250);
                rt2 = http.GetStringAsync(url + "/api/runtime").GetAwaiter().GetResult();
            }

            long rearmMs = again.ElapsedMilliseconds;
            check(k == 200 && IdleField(afterKeep, "reminder") == "false" && IdleField(rt2, "reminder") == "true" && rearmMs >= 2000,
                  "Keep running withdraws it and starts the period again: it comes back after another full period",
                  "keep-alive " + k + ", then back after " + rearmMs + " ms");

            // An ignored reminder comes back after another full period, counted.
            Stopwatch ignored = Stopwatch.StartNew();
            string rt3 = rt2;
            while (ignored.Elapsed < TimeSpan.FromSeconds(15) && !(long.TryParse(IdleField(rt3, "count"), out long n) && n >= 2))
            {
                Thread.Sleep(250);
                rt3 = http.GetStringAsync(url + "/api/runtime").GetAwaiter().GetResult();
            }

            check(long.TryParse(IdleField(rt3, "count"), out long count) && count >= 2,
                  "one left unanswered comes back after another period, and so on", "reminder number " + count);

            // Use - a point on the map - withdraws it.
            http.GetStringAsync(url + "/api/at?seed=1&x=0&z=0").GetAwaiter().GetResult();
            string rt4 = http.GetStringAsync(url + "/api/runtime").GetAwaiter().GetResult();
            check(IdleField(rt4, "reminder") == "false" && IdleField(rt4, "count") == "0",
                  "using the page (a point on the map) withdraws the reminder", HostChecks.Field(rt4, "idle") ?? "");
        }

        // =========================================================================================
        // Resume points (review of 2026-09-25): the end of the session saves a running search (Windows: the
        // hidden window's WM_ENDSESSION); the same query started again is refused until the user says to
        // replace that resume point; a funnel stopped in its first stage is warned about as losing its work;
        // and the stopped search resumes from the query file saved beside its checkpoint.

        private static void ResumePoints(Action<bool, string, string> check, string exe, string repo, string dir, string env)
        {
            Directory.CreateDirectory(dir);
            string cache = Path.Combine(dir, "cache");
            List<string> common = new List<string> { "--cache-dir", cache, "--skip-self-test", "--ignore-running-game" };
            string goals2 = PageGoals.Replace("\"value\":100000", "\"value\":200000");
            string body2 = "{\"name\":\"resume-point\",\"goals\":[" + goals2 + "],\"budgetSeeds\":20000,\"budgetSeconds\":0,"
                           + "\"keep\":5,\"gridSpacingM\":384,\"threads\":1,\"blockSize\":16,\"order\":\"shuffled\",\"screen\":\"off\","
                           + "\"strategy\":\"sample\",\"confirmed\":true,\"acceptScanOrder\":true,\"ignoreRunningGame\":true}";
            using HttpClient http = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromMinutes(3) };

            // ---- S2: a search, then the session ends --------------------------------------------------------
            string? ckpt2 = null;
            List<string> lines2 = new List<string>();
            using (Process s2 = StartServer(exe, repo, With(common, "serve", "--port", "0", "--no-browser"), env, lines2, out string url2))
            {
                try
                {
                    check(url2.Length > 0, "a second server starts in the same cache folder (GUARD)", url2.Length > 0 ? url2 : HostChecks.Tail(HostChecks.Joined(lines2)));
                    if (url2.Length == 0) return;
                    (int st2, string started2) = Post(http, url2 + "/api/search", null, null, body2, url2);
                    string id2 = HostChecks.Field(started2, "id") ?? "";
                    check(st2 == 200 && id2.Length > 0, "its search starts (GUARD)", st2 + " " + HostChecks.Tail(started2));
                    if (id2.Length == 0) return;
                    Task<string> stream2 = http.GetStringAsync(url2 + "/api/search/" + id2 + "/stream");
                    WaitScanned(http, url2, id2, 64);

                    if (OperatingSystem.IsWindows())
                    {
                        IntPtr hwnd = FindSessionEndWindow(s2.Id);
                        check(hwnd != IntPtr.Zero, "SESSION END: the server keeps a hidden window (class SeedLabWebServerSessionEnd) to hear "
                                                   + "Windows end the session - a console handler never does once user32.dll is loaded", "");
                        IntPtr answer = IntPtr.Zero;
                        bool asked = hwnd != IntPtr.Zero
                                     && SendMessageTimeoutW(hwnd, 0x0011, IntPtr.Zero, new IntPtr(0x80000000L), 0x0002, 5000, out answer) != IntPtr.Zero;
                        Thread.Sleep(300);
                        bool aliveAfterQuery = !s2.HasExited;
                        Stopwatch sw = Stopwatch.StartNew();
                        if (hwnd != IntPtr.Zero)
                        {
                            SendMessageTimeoutW(hwnd, 0x0016, new IntPtr(1), new IntPtr(0x80000000L), 0x0002, 15000, out _);
                        }

                        long endMs = sw.ElapsedMilliseconds;
                        bool exited = s2.WaitForExit(15_000);
                        string log2 = HostChecks.ReadLog(cache, 0);
                        check(asked && answer == new IntPtr(1) && aliveAfterQuery,
                              "WM_QUERYENDSESSION is answered 'yes' at once and stops nothing - another program may still cancel the shutdown",
                              "answer " + answer + ", still running " + aliveAfterQuery);
                        check(exited && s2.ExitCode == 0 && log2.Contains("serve    stopping: Windows is signing you out", StringComparison.Ordinal)
                              && log2.Contains("the search \"resume-point\" stopped after", StringComparison.Ordinal)
                              && log2.Contains("To continue it from a terminal: vseed search \"", StringComparison.Ordinal)
                              && Directory.GetFiles(Path.Combine(cache, "serve"), "server-*.json").Length == 0,
                              "WM_ENDSESSION (a sign-out) is the graceful stop: the search is stopped with its checkpoint saved and the "
                              + "command that continues it logged, the registry file removed, exit 0 - before the message returns",
                              "message returned after " + endMs + " ms; " + (exited ? "exit " + s2.ExitCode : "STILL RUNNING"));
                    }
                    else
                    {
                        (int ps, string pst) = HostChecks.RunToEnd(exe, dir, With(common, "serve", "--stop", "--yes"), env);
                        check(ps == 0 && s2.WaitForExit(15_000), "the server is stopped with --stop --yes (the session-end window is Windows-only)",
                              "exit " + ps);
                    }

                    string streamText2 = stream2.Wait(TimeSpan.FromSeconds(10)) ? stream2.Result : "";
                    ckpt2 = HostChecks.Event(streamText2, "done", "checkpointPath");
                    check(ckpt2 != null && File.Exists(ckpt2) && File.Exists(ckpt2 + ".query.json"),
                          "the stopped search left its checkpoint and its query file", ckpt2 ?? "(no checkpoint)");
                }
                finally
                {
                    Kill(s2);
                }
            }

            if (ckpt2 == null || !File.Exists(ckpt2)) return;

            // ---- S3: the same query again, and a funnel stopped in its first stage ----------------------------
            List<string> lines3 = new List<string>();
            using (Process s3 = StartServer(exe, repo, With(common, "serve", "--port", "0", "--no-browser"), env, lines3, out string url3))
            {
                try
                {
                    if (url3.Length == 0)
                    {
                        check(false, "a third server starts (GUARD)", HostChecks.Tail(HostChecks.Joined(lines3)));
                        return;
                    }

                    byte[] before = File.ReadAllBytes(ckpt2);
                    (int again, string againBody) = Post(http, url3 + "/api/search", null, null, body2, url3);
                    string expected = "vseed search \"" + ckpt2 + ".query.json\" --resume --checkpoint \"" + ckpt2 + "\"";
                    string error = HostChecks.Field(againBody, "error") ?? "";
                    check(again == 400 && HostChecks.Field(againBody, "kind") == "checkpoint-exists"
                          && HostChecks.Field(againBody, "resumeCommand") == expected
                          && error.Contains(ckpt2, StringComparison.Ordinal) && error.Contains("REPLACES", StringComparison.Ordinal)
                          && Same(before, File.ReadAllBytes(ckpt2)),
                          "SAME QUERY AGAIN: starting it while a stopped run of it left a resume point is refused (checkpoint-exists) "
                          + "with where that resume point is and the command that continues it; the checkpoint is untouched",
                          again + " " + HostChecks.Tail(error));

                    // The funnel's first stage has no resume point: the warning must say so, and never "saved".
                    string funnel = "{\"name\":\"stage-one-stop\",\"goals\":["
                                    + "{\"target\":\"biome:Meadows\",\"metric\":\"area_within\",\"radius\":2000,\"test\":\"at_least\",\"value\":1000000,\"importance\":\"must\"},"
                                    + "{\"target\":\"location:Vendor_BlackForest\",\"metric\":\"nearest_distance\",\"test\":\"near\",\"value\":3000,\"importance\":\"nice\"}],"
                                    + "\"budgetSeeds\":400000,\"budgetSeconds\":0,\"keep\":5,\"gridSpacingM\":384,\"threads\":1,\"order\":\"shuffled\","
                                    + "\"screen\":\"off\",\"strategy\":\"funnel\",\"confirmed\":true,\"acceptScanOrder\":true,\"ignoreRunningGame\":true}";
                    (int fs, string fStarted) = Post(http, url3 + "/api/search", null, null, funnel, url3);
                    string fid = HostChecks.Field(fStarted, "id") ?? "";
                    if (fs != 200 || fid.Length == 0)
                    {
                        check(fs == 400 && (HostChecks.Field(fStarted, "error") ?? "").Contains("location", StringComparison.OrdinalIgnoreCase),
                              "FUNNEL STAGE ONE (SKIPPED: this build cannot place locations here, and says so)", fs + " " + HostChecks.Tail(fStarted));
                    }
                    else
                    {
                        Task<string> fstream = http.GetStringAsync(url3 + "/api/search/" + fid + "/stream");
                        string fmsg = "";
                        Stopwatch w = Stopwatch.StartNew();
                        while (w.Elapsed < TimeSpan.FromSeconds(60) && !fmsg.StartsWith("stage 1 of 2", StringComparison.Ordinal))
                        {
                            Thread.Sleep(200);
                            string state = http.GetStringAsync(url3 + "/api/search/" + fid).GetAwaiter().GetResult();
                            fmsg = HostChecks.Field(HostChecks.Field(state, "progress") ?? "{}", "message") ?? "";
                        }

                        (int f1, string ft1) = HostChecks.RunToEnd(exe, dir, With(common, "serve", "--stop"), env);
                        string flat1 = HostChecks.Flat(ft1);
                        check(fmsg.StartsWith("stage 1 of 2", StringComparison.Ordinal) && f1 == 1
                              && flat1.Contains("\"stage-one-stop\" has no resume point yet (it is in its first stage): stopping it now loses the work it has done so far.", StringComparison.Ordinal)
                              && !flat1.Contains("saved", StringComparison.Ordinal) && !flat1.Contains("vseed search", StringComparison.Ordinal),
                              "FUNNEL STAGE ONE: --stop's warning says that search has no resume point and loses its work - and nowhere that "
                              + "anything is saved or can be continued",
                              "exit " + f1 + ": " + HostChecks.Tail(flat1));

                        (int f2, string ft2) = HostChecks.RunToEnd(exe, dir, With(common, "serve", "--stop", "--yes"), env);
                        bool exited3 = s3.WaitForExit(15_000);
                        string fText = fstream.Wait(TimeSpan.FromSeconds(10)) ? fstream.Result : "";
                        string? fckpt = HostChecks.Event(fText, "started", "checkpoint");
                        check(f2 == 0 && exited3 && HostChecks.Flat(ft2).Contains("It left no checkpoint to continue from.", StringComparison.Ordinal)
                              && fckpt != null && !File.Exists(fckpt) && !File.Exists(fckpt + ".query.json") && File.Exists(ckpt2),
                              "and --stop --yes then says it left no checkpoint; no checkpoint and no query file of it are left behind, "
                              + "and the other search's resume point is still there",
                              "exit " + f2 + "; " + (fckpt ?? "(no checkpoint path)"));
                    }
                }
                finally
                {
                    if (!s3.HasExited) HostChecks.RunToEnd(exe, dir, With(common, "serve", "--stop", "--yes"), env);
                    Kill(s3);
                }
            }

            // ---- the stopped search resumes from the command as printed -------------------------------------
            (int re, string rt) = HostChecks.RunToEnd(exe, repo, With(common, "search", ckpt2 + ".query.json", "--resume", "--checkpoint", ckpt2,
                                                                     "--yes", "--progress", "none", "--json"), env);
            string flatR = HostChecks.Flat(rt);
            check(re == 0 && flatR.Contains("\"complete\": true", StringComparison.Ordinal) && !File.Exists(ckpt2) && !File.Exists(ckpt2 + ".query.json"),
                  "the search stopped by the end of the session resumes from its query file and checkpoint, to the end",
                  "exit " + re + (re == 0 ? "" : ": " + HostChecks.Tail(flatR)));
        }

        private static Process StartServer(string exe, string workDir, List<string> args, string env, List<string> lines, out string url)
        {
            ProcessStartInfo psi = HostChecks.Start(exe, workDir, args, env);
            ManualResetEventSlim ready = new ManualResetEventSlim(false);
            Process p = new Process { StartInfo = psi };
            p.OutputDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                lock (lines) lines.Add(e.Data);
                if (e.Data.Contains("SeedLab is ready.", StringComparison.Ordinal)) ready.Set();
            };
            p.ErrorDataReceived += (_, e) =>
            {
                if (e.Data != null) lock (lines) lines.Add(e.Data);
            };
            p.Start();
            p.StandardInput.Close();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            url = ready.Wait(TimeSpan.FromSeconds(60))
                ? (HostChecks.LineWith(HostChecks.Joined(lines), "SeedLab is serving at ") ?? "").Replace("SeedLab is serving at ", "").Trim()
                : "";
            return p;
        }

        private static void WaitScanned(HttpClient http, string url, string id, long seeds)
        {
            long scanned = 0;
            Stopwatch w = Stopwatch.StartNew();
            while (scanned < seeds && w.Elapsed < TimeSpan.FromSeconds(30))
            {
                Thread.Sleep(100);
                string state = http.GetStringAsync(url + "/api/search/" + id).GetAwaiter().GetResult();
                long.TryParse(HostChecks.Field(HostChecks.Field(state, "progress") ?? "{}", "scanned") ?? "0", out scanned);
            }
        }

        private static void Kill(Process p)
        {
            try
            {
                if (!p.HasExited) p.Kill(entireProcessTree: true);
                p.WaitForExit(10_000);
            }
            catch (Exception)
            {
            }
        }

        private static bool Same(byte[] a, byte[] b) => a.AsSpan().SequenceEqual(b);

        /// <summary>A registry file as a server writes it, with these values.</summary>
        private static string RegistryJson(int pid, string startedIso, int port, string url) =>
            "{\n  \"pid\": " + pid + ",\n  \"process_started_utc\": \"" + startedIso + "\",\n  \"port\": " + port
            + ",\n  \"url\": " + JsonSerializer.Serialize(url) + ",\n  \"started_utc\": \"2026-09-25T00:00:00.0000000Z\",\n"
            + "  \"version\": \"0.1.0\",\n  \"token\": \"" + new string('a', 64) + "\"\n}\n";

        /// <summary>The server's hidden session-end window: its class name, in that process. Zero when there is none.</summary>
        private static IntPtr FindSessionEndWindow(int pid)
        {
            IntPtr found = IntPtr.Zero;
            Stopwatch w = Stopwatch.StartNew();
            while (found == IntPtr.Zero && w.Elapsed < TimeSpan.FromSeconds(5))
            {
                EnumWindows((h, _) =>
                {
                    GetWindowThreadProcessId(h, out uint owner);
                    if (owner != (uint)pid) return true;
                    StringBuilder cls = new StringBuilder(256);
                    GetClassNameW(h, cls, cls.Capacity);
                    if (cls.ToString() != "SeedLabWebServerSessionEnd") return true;
                    found = h;
                    return false;
                }, IntPtr.Zero);
                if (found == IntPtr.Zero) Thread.Sleep(100);
            }

            return found;
        }

        private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr param);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc proc, IntPtr param);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassNameW(IntPtr hwnd, StringBuilder name, int max);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessageTimeoutW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeoutMs,
                                                         out IntPtr result);

        // =========================================================================================
        // Ctrl+C in the server's own console, sent for real (Windows).
        //
        // A console Ctrl+C can only be sent to the processes on the SENDER's console, and never to a process
        // on the test runner's own (it would stop the runner too). So this program starts itself again as a
        // helper with a console of its own, hidden (CreateNoWindow gives a child a new console with no window);
        // the helper starts vseed on that console, and only THEN switches Ctrl+C off for itself - the switch (a
        // NULL handler routine) is inherited by processes started after it - and sends the events.

        private static void CtrlC(Action<bool, string, string> check, string exe, string dir, string env)
        {
            Directory.CreateDirectory(dir);
            string self = Environment.ProcessPath ?? "";
            ProcessStartInfo psi;
            if (Path.GetFileNameWithoutExtension(self).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            {
                psi = new ProcessStartInfo(self);
                psi.ArgumentList.Add(typeof(LifecycleChecks).Assembly.Location);
            }
            else
            {
                psi = new ProcessStartInfo(self);
            }

            psi.ArgumentList.Add("--ctrlc-helper");
            psi.ArgumentList.Add(exe);
            psi.ArgumentList.Add(Path.Combine(dir, "cache"));
            psi.ArgumentList.Add(dir);
            psi.ArgumentList.Add(Path.Combine(dir, "cache-break"));
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.RedirectStandardInput = true;
            psi.Environment["SEEDLAB_CACHE_DIR"] = env;

            using Process helper = Process.Start(psi)!;
            helper.StandardInput.Close();
            Task<string> outTask = helper.StandardOutput.ReadToEndAsync();
            Task<string> errTask = helper.StandardError.ReadToEndAsync();
            if (!helper.WaitForExit(120_000))
            {
                try { helper.Kill(entireProcessTree: true); } catch (Exception) { }
                check(false, "CTRL+C: the helper did not finish within 2 minutes", "");
                return;
            }

            string o = outTask.Result + errTask.Result;
            string R(string key) => (HostChecks.LineWith(o, "RESULT " + key + "=") ?? "").Replace("RESULT " + key + "=", "").Trim();

            if (R("ready") != "true")
            {
                check(false, "CTRL+C: the server started on the helper's console (GUARD)", HostChecks.Tail(o));
                return;
            }

            check(R("alive-after-first") == "true" && R("first-message") == "true",
                  "CTRL+C: the first press in the server's console stops nothing and says 'Press Ctrl+C again within 10 seconds'",
                  "still running: " + R("alive-after-first"));
            check(R("alive-after-late-second") == "true" && R("first-messages") == "2",
                  "a second press more than 10 seconds later is a new first press: still running, the message again",
                  R("first-messages") + " first-press messages");
            check(R("exited") == "true" && R("exit-code") == "0" && R("stop-message") == "true",
                  "a press within 10 seconds of that one is the graceful stop: 'Stopping SeedLab: Ctrl+C was pressed twice in its window.', exit 0",
                  "exit code " + R("exit-code") + ", " + R("stop-ms") + " ms after that press (-1073741510 would mean no handler ran)");
            check(R("registry-left") == "0", "and it removed its registry file", R("registry-left") + " left");
            check(R("first-messages-final") == "2" && (R("already-stopping") == "true" || R("alive-at-third") == "false"),
                  "a third press right behind it, while it stops, is not a new first press: it says SeedLab is already stopping",
                  "first-press messages " + R("first-messages-final") + ", 'already stopping' " + R("already-stopping")
                  + ", server alive at the third press " + R("alive-at-third"));
            check(R("inherited-heard") == "true",
                  "a server started with the ignore-Ctrl+C flag inherited (as a script can pass it on) still hears Ctrl+C: it switches the flag off for itself",
                  "first-press message " + (R("inherited-heard") == "true" ? "printed" : "NOT printed"));
            check(R("break-exited") == "true" && R("break-exit-code") == "0" && R("break-message") == "true",
                  "CTRL+BREAK is a stop already confirmed: one press, 'Stopping SeedLab: Ctrl+Break was pressed in its window.', exit 0",
                  "exit code " + R("break-exit-code") + ", " + R("break-ms") + " ms");
        }

        /// <summary>The helper process: runs on a hidden console of its own; see <see cref="CtrlC"/>.</summary>
        public static int CtrlCHelper(string[] args)
        {
            string exe = args[1], cache = args[2], dir = args[3];
            ProcessStartInfo psi = new ProcessStartInfo(exe)
            {
                WorkingDirectory = dir,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (string a in new[] { "serve", "--port", "0", "--no-browser", "--cache-dir", cache, "--skip-self-test", "--ignore-running-game" })
            {
                psi.ArgumentList.Add(a);
            }

            List<string> lines = new List<string>();
            ManualResetEventSlim ready = new ManualResetEventSlim(false);
            using Process p = new Process { StartInfo = psi };
            p.OutputDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                lock (lines) lines.Add(e.Data);
                if (e.Data.Contains("SeedLab is ready.", StringComparison.Ordinal)) ready.Set();
            };
            p.ErrorDataReceived += (_, e) =>
            {
                if (e.Data != null) lock (lines) lines.Add(e.Data);
            };
            p.Start();
            p.StandardInput.Close();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();

            bool up = ready.Wait(TimeSpan.FromSeconds(60));
            Console.WriteLine("RESULT ready=" + (up ? "true" : "false"));
            if (!up)
            {
                try { p.Kill(); } catch (Exception) { }
                Console.WriteLine(Text(lines));
                return 1;
            }

            // AFTER the child exists: the ignore flag is inherited by children started later, not earlier.
            SetConsoleCtrlHandler(IntPtr.Zero, true);

            GenerateConsoleCtrlEvent(0, 0);
            Thread.Sleep(1500);
            Console.WriteLine("RESULT alive-after-first=" + (!p.HasExited ? "true" : "false"));
            Console.WriteLine("RESULT first-message=" + (Text(lines).Contains("Press Ctrl+C again within 10 seconds to stop SeedLab.", StringComparison.Ordinal) ? "true" : "false"));

            Thread.Sleep(10_500);
            GenerateConsoleCtrlEvent(0, 0);
            Thread.Sleep(1500);
            Console.WriteLine("RESULT alive-after-late-second=" + (!p.HasExited ? "true" : "false"));
            Console.WriteLine("RESULT first-messages=" + Count(Text(lines), "nothing has been stopped yet"));

            // The late press was a new FIRST press; one more, 1.5 s after it, is the second within 10 seconds -
            // and a third right behind it, while the stop is under way (review of 2026-09-25), must not count
            // as a new first press. Back to back: a stop with no search takes only milliseconds.
            Stopwatch sw = Stopwatch.StartNew();
            GenerateConsoleCtrlEvent(0, 0);
            bool aliveAtThird = !p.HasExited;
            GenerateConsoleCtrlEvent(0, 0);
            bool exited = p.WaitForExit(15_000);
            long ms = sw.ElapsedMilliseconds;
            if (exited) p.WaitForExit();
            Console.WriteLine("RESULT exited=" + (exited ? "true" : "false"));
            Console.WriteLine("RESULT exit-code=" + (exited ? p.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture) : "none"));
            Console.WriteLine("RESULT stop-ms=" + ms);
            Console.WriteLine("RESULT stop-message=" + (Text(lines).Contains("Stopping SeedLab: Ctrl+C was pressed twice in its window.", StringComparison.Ordinal) ? "true" : "false"));
            Console.WriteLine("RESULT alive-at-third=" + (aliveAtThird ? "true" : "false"));
            Console.WriteLine("RESULT first-messages-final=" + Count(Text(lines), "nothing has been stopped yet"));
            Console.WriteLine("RESULT already-stopping=" + (Text(lines).Contains("Ctrl+C - SeedLab is already stopping; wait a moment.", StringComparison.Ordinal) ? "true" : "false"));
            string serveDir = Path.Combine(cache, "serve");
            Console.WriteLine("RESULT registry-left=" + (Directory.Exists(serveDir) ? Directory.GetFiles(serveDir, "server-*.json").Length : 0));
            if (!exited)
            {
                try { p.Kill(); } catch (Exception) { }
            }

            // ---- Ctrl+Break: a stop already confirmed ---------------------------------------------------------
            // The NULL flag set above covers Ctrl+C only; the helper survives its own Ctrl+Break with a handler
            // routine that swallows it (a routine, unlike the flag, is not inherited).
            SetConsoleCtrlHandler(BreakSwallower, true);
            RunBreak(exe, args.Length > 4 ? args[4] : cache + "-break", dir);

            Console.WriteLine("---- the server's output ----");
            Console.WriteLine(Text(lines));
            return 0;
        }

        private static void RunBreak(string exe, string cache, string dir)
        {
            ProcessStartInfo psi = new ProcessStartInfo(exe)
            {
                WorkingDirectory = dir,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (string a in new[] { "serve", "--port", "0", "--no-browser", "--cache-dir", cache, "--skip-self-test", "--ignore-running-game" })
            {
                psi.ArgumentList.Add(a);
            }

            List<string> lines = new List<string>();
            ManualResetEventSlim ready = new ManualResetEventSlim(false);
            using Process p = new Process { StartInfo = psi };
            p.OutputDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                lock (lines) lines.Add(e.Data);
                if (e.Data.Contains("SeedLab is ready.", StringComparison.Ordinal)) ready.Set();
            };
            p.ErrorDataReceived += (_, e) =>
            {
                if (e.Data != null) lock (lines) lines.Add(e.Data);
            };
            p.Start();
            p.StandardInput.Close();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            if (!ready.Wait(TimeSpan.FromSeconds(60)))
            {
                try { p.Kill(); } catch (Exception) { }
                Console.WriteLine("RESULT break-exited=false");
                return;
            }

            // This vseed was started AFTER the helper set the "ignore Ctrl+C" flag, which it inherited; it
            // switches the flag back off for itself (ServeConsole.PrepareWindow), so it still hears Ctrl+C.
            GenerateConsoleCtrlEvent(0, 0);
            Thread.Sleep(1500);
            Console.WriteLine("RESULT inherited-heard=" + (!p.HasExited && Text(lines).Contains("nothing has been stopped yet", StringComparison.Ordinal) ? "true" : "false"));

            Stopwatch sw = Stopwatch.StartNew();
            GenerateConsoleCtrlEvent(1, 0);   // CTRL_BREAK_EVENT
            bool exited = p.WaitForExit(15_000);
            long ms = sw.ElapsedMilliseconds;
            if (exited)
            {
                p.WaitForExit();
            }
            else
            {
                try { p.Kill(); } catch (Exception) { }
            }

            Console.WriteLine("RESULT break-exited=" + (exited ? "true" : "false"));
            Console.WriteLine("RESULT break-exit-code=" + (exited ? p.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture) : "none"));
            Console.WriteLine("RESULT break-ms=" + ms);
            Console.WriteLine("RESULT break-message=" + (Text(lines).Contains("Stopping SeedLab: Ctrl+Break was pressed in its window.", StringComparison.Ordinal) ? "true" : "false"));
        }

        private delegate bool HandlerRoutine(uint ctrlType);

        /// <summary>Swallows CTRL_BREAK_EVENT (1) for the helper itself; kept in a field so it is never collected.</summary>
        private static readonly HandlerRoutine BreakSwallower = ctrlType => ctrlType == 1;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetConsoleCtrlHandler(HandlerRoutine handler, bool add);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetConsoleCtrlHandler(IntPtr handler, bool add);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GenerateConsoleCtrlEvent(uint ctrlEvent, uint processGroupId);

        // =========================================================================================
        // Helpers.

        private static string Text(List<string> lines)
        {
            lock (lines) return string.Join("\n", lines);
        }

        private static int Count(string text, string part)
        {
            int n = 0;
            for (int at = text.IndexOf(part, StringComparison.Ordinal); at >= 0; at = text.IndexOf(part, at + part.Length, StringComparison.Ordinal)) n++;
            return n;
        }

        private static bool Wait(Func<bool> condition, int ms)
        {
            Stopwatch sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ms)
            {
                if (condition()) return true;
                Thread.Sleep(100);
            }

            return condition();
        }

        private static string Prev(string cache)
        {
            string p = Path.Combine(cache, "logs", "vseed-prev.log");
            if (!File.Exists(p)) return "";
            using FileStream fs = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using StreamReader r = new StreamReader(fs, Encoding.UTF8);
            return r.ReadToEnd().Replace("\r\n", "\n");
        }

        private static string? IdleField(string runtimeJson, string name) => HostChecks.Field(HostChecks.Field(runtimeJson, "idle") ?? "{}", name);

        /// <summary>The "searches" array of a reply as compact JSON, for a substring check.</summary>
        private static string SearchesOf(string json)
        {
            try
            {
                using JsonDocument d = JsonDocument.Parse(json);
                return d.RootElement.TryGetProperty("searches", out JsonElement s) ? JsonSerializer.Serialize(s) : "";
            }
            catch (Exception)
            {
                return "";
            }
        }

        private static List<string> With(List<string> common, params string[] args)
        {
            List<string> l = new List<string>(args);
            l.AddRange(common);
            return l;
        }

        /// <summary>A POST with the server's token (or none), and as the page sends it when <paramref name="origin"/> is given.</summary>
        private static (int Status, string Body) Post(HttpClient http, string url, string? token, string? foreignOrigin, string? json,
                                                      string? origin = null)
        {
            using HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Post, url);
            if (json != null) req.Content = new StringContent(json, Encoding.UTF8, "application/json");
            if (token != null) req.Headers.TryAddWithoutValidation("X-SeedLab-Token", token);
            if (foreignOrigin != null) req.Headers.TryAddWithoutValidation("Origin", foreignOrigin);
            else if (origin != null)
            {
                req.Headers.TryAddWithoutValidation("Origin", origin);
                req.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "same-origin");
            }

            using HttpResponseMessage res = http.Send(req);
            return ((int)res.StatusCode, res.Content.ReadAsStringAsync().GetAwaiter().GetResult());
        }
    }
}
