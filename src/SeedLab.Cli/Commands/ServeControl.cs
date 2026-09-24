using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using SeedLab.Cli.Infra;
using SeedLab.Runtime.Storage;
using SeedLab.Web.Search;

namespace SeedLab.Cli.Commands
{
    /// <summary>
    /// <c>vseed serve --status</c> and <c>vseed serve --stop</c> (2026-09-24): is SeedLab's web server
    /// running, and stop it - for a person in a terminal, and for the scripts behind "SeedLab 3 - Stop web
    /// page" and <c>sh seedlab.sh stop</c>.
    ///
    /// <para><b>They start nothing.</b> Both run BEFORE the command's runtime: no cache root is created,
    /// no session log is opened or rotated, no self-test is run and nothing is reaped. An uninstall runs
    /// them after deleting the cache root, and they must not bring it back; the registry is only read
    /// (<see cref="ServerRegistry.Scan"/>), and a server is only ever asked over its own loopback port.</para>
    ///
    /// <para><b>Which servers.</b> Those registered in THIS cache root: one started with
    /// <c>--cache-dir X</c> is found with the same <c>--cache-dir X</c> (or <c>SEEDLAB_CACHE_DIR</c>). On
    /// Windows a <c>vseed serve</c> the registry does not know - another cache folder, or a build from
    /// before the registry - is listed as well, with how to stop it; it is never stopped from here, because
    /// nothing here can tell it is SeedLab's server rather than a program that happens to have that name.</para>
    ///
    /// <para><b>Exit codes.</b> <c>--status</c>: 0 running (a line with its <c>http://127.0.0.1:port</c>
    /// address per server), 1 not running. <c>--stop</c>: 0 stopped, or nothing was running; 1 not stopped
    /// (the question was declined, or refused because nothing could answer it, or the server did not answer
    /// within 10 seconds and <c>--force</c> was not given).</para>
    ///
    /// <para><b>A file is not a server</b> (review of 2026-09-25). A registry file counts only while its
    /// process is provably the one that wrote it (<see cref="ProcessLiveness.IsSameProcessStrict"/>), and a
    /// server that answers says its own pid, which must be the file's: a left-over file whose port another
    /// program has taken is not reported as SeedLab, asked to stop or opened. A left-over file is named, so
    /// a person can see what it is; the next server removes it.</para>
    /// </summary>
    public static class ServeControl
    {
        private static readonly TimeSpan AnswerTimeout = TimeSpan.FromSeconds(10);

        public static int Run(Args a, bool json, bool status)
        {
            bool yes = a.Flag("yes");
            bool force = a.Flag("force");
            if (status && (yes || force)) throw new CliException("--yes and --force go with --stop, not --status.");
            string? cacheDir = a.Get("cache-dir");
            a.ConsumeGlobals();
            a.RejectUnknown();
            if (a.Positional.Count > 0) throw new CliException("vseed serve takes no positional arguments ('" + a.Positional[0] + "').");

            CacheRoot root = CacheRoot.Open(new CacheRootOptions { Override = cacheDir, Create = false });
            ServerScan scan = ServerRegistry.Scan(root.Path);
            List<int> known = new List<int>();
            foreach (ServerRecord r in scan.Live) known.Add(r.Pid);
            foreach (ServerRecord r in scan.Stale) known.Add(r.Pid);
            List<UnregisteredVseed> unknown = ProcessCommandLine.UnregisteredServers(known);

            return status ? Status(root, scan, unknown, json) : Stop(root, scan, unknown, json, yes, force);
        }

        // =========================================================================================
        // --status

        private static int Status(CacheRoot root, ServerScan scan, List<UnregisteredVseed> unknown, bool json)
        {
            using HttpClient http = Client(TimeSpan.FromSeconds(5));
            List<(ServerRecord Server, ServerState? State)> servers = new List<(ServerRecord, ServerState?)>();
            List<(ServerRecord Server, int OtherPid)> impostors = new List<(ServerRecord, int)>();
            foreach (ServerRecord r in scan.Live)
            {
                ServerState? st = GetState(http, r);
                if (st != null && st.Pid != r.Pid) impostors.Add((r, st.Pid));
                else servers.Add((r, st));
            }

            if (json)
            {
                using System.IO.MemoryStream ms = new System.IO.MemoryStream();
                using Utf8JsonWriter w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true });
                w.WriteStartObject();
                w.WriteString("command", "serve --status");
                w.WriteBoolean("running", servers.Count > 0);
                w.WriteString("cache_root", root.Path);
                w.WriteStartArray("servers");
                foreach ((ServerRecord r, ServerState? st) in servers)
                {
                    w.WriteStartObject();
                    w.WriteNumber("pid", r.Pid);
                    w.WriteString("url", r.Url);
                    w.WriteNumber("port", r.Port);
                    w.WriteString("started_utc", r.StartedUtc.ToString("o", CultureInfo.InvariantCulture));
                    w.WriteString("version", r.Version);
                    w.WriteBoolean("answering", st != null);
                    w.WriteBoolean("stopping", st?.Stopping ?? false);
                    w.WriteStartArray("searches");
                    foreach (SearchRunInfo s in st?.Searches ?? new List<SearchRunInfo>()) WriteSearch(w, s);
                    w.WriteEndArray();
                    w.WriteEndObject();
                }

                w.WriteEndArray();
                WriteUnknown(w, unknown);
                w.WriteStartArray("left_over_files");
                foreach (string f in LeftOver(scan, impostors)) w.WriteStringValue(f);
                w.WriteEndArray();
                w.WriteEndObject();
                w.Flush();
                Console.Out.WriteLine(Encoding.UTF8.GetString(ms.ToArray()));
                return servers.Count > 0 ? ExitCodes.Ok : ExitCodes.CheckFailed;
            }

            foreach ((ServerRecord r, ServerState? st) in servers)
            {
                string line = "SeedLab's web server is running at " + r.Url + " since " + r.SinceLocal + " (pid "
                              + r.Pid.ToString(CultureInfo.InvariantCulture) + ")";
                if (st == null) line += ", but it did not answer just now - it may be busy starting or stopping.";
                else if (st.Stopping) line += "; it is stopping.";
                else if (st.Searches.Count == 0) line += "; no search is running.";
                else line += "; " + (st.Searches.Count == 1 ? "a search is" : st.Searches.Count + " searches are") + " running: "
                             + ServeConsole.Describe(st.Searches) + ".";
                Print(line);
            }

            if (servers.Count == 0) Print("SeedLab's web server is not running.");
            SayLeftOver(scan, impostors);
            SayUnknown(unknown, root, servers.Count == 0);
            return servers.Count > 0 ? ExitCodes.Ok : ExitCodes.CheckFailed;
        }

        /// <summary>The registry files that name no running SeedLab server, for a person to see.</summary>
        private static List<string> LeftOver(ServerScan scan, List<(ServerRecord Server, int OtherPid)> impostors)
        {
            List<string> files = new List<string>();
            foreach (ServerRecord r in scan.Stale) files.Add(r.File);
            foreach ((ServerRecord r, int _) in impostors) files.Add(r.File);
            files.AddRange(scan.Unreadable);
            return files;
        }

        private static void SayLeftOver(ServerScan scan, List<(ServerRecord Server, int OtherPid)> impostors)
        {
            List<string> files = LeftOver(scan, impostors);
            if (files.Count == 0) return;
            bool one = files.Count == 1;
            bool allReaped = impostors.Count == 0 && scan.Unreadable.Count == 0;
            Print("");
            Print((one ? "A file" : files.Count + " files") + " in the cache folder's serve folder " + (one ? "names" : "name")
                  + " no running SeedLab web server - left behind by a server that ended without stopping (a crash, a "
                  + "power cut, its process ended by hand), or not a file SeedLab wrote. " + (one ? "It is" : "They are")
                  + " ignored, and deleting " + (one ? "it" : "them") + " by hand is safe"
                  + (allReaped ? " (the next 'vseed serve' removes " + (one ? "it" : "them") + " by itself)" : "") + ":");
            foreach (string f in files) Print("  " + f, wrap: false);
        }

        // =========================================================================================
        // --stop

        private static int Stop(CacheRoot root, ServerScan scan, List<UnregisteredVseed> unknown, bool json, bool yes, bool force)
        {
            List<(ServerRecord Server, int OtherPid)> impostors = new List<(ServerRecord, int)>();
            if (scan.Live.Count == 0)
            {
                Print("SeedLab's web server is not running - nothing to stop.");
                SayLeftOver(scan, impostors);
                SayUnknown(unknown, root, true);
                return ExitCodes.Ok;
            }

            bool interactive = !json && !Console.IsInputRedirected;
            int result = ExitCodes.Ok;
            int stoppedOrAsked = 0;
            using HttpClient http = Client(AnswerTimeout);
            foreach (ServerRecord r in scan.Live)
            {
                ServerState? state = GetState(http, r);
                if (state != null && state.Pid != r.Pid)
                {
                    // Its port answers for another process: the file is left over, and that other program is
                    // not asked to stop with this file's token (review of 2026-09-25).
                    impostors.Add((r, state.Pid));
                    continue;
                }

                stoppedOrAsked++;
                int one = StopOne(http, r, state, yes, force, interactive);
                if (one != ExitCodes.Ok) result = one;
            }

            if (stoppedOrAsked == 0) Print("SeedLab's web server is not running - nothing to stop.");
            SayLeftOver(scan, impostors);
            SayUnknown(unknown, root, stoppedOrAsked == 0);
            return result;
        }

        private static int StopOne(HttpClient http, ServerRecord r, ServerState? state, bool yes, bool force, bool interactive)
        {
            string who = "SeedLab's web server at " + r.Url + " (pid " + r.Pid.ToString(CultureInfo.InvariantCulture) + ")";
            bool stopSearch = yes;

            // The second warning: a running search is stopped only when the user says so.
            if (state != null && state.Searches.Count > 0 && !yes)
            {
                if (!Confirm(state.Searches, interactive)) return ExitCodes.CheckFailed;
                stopSearch = true;
            }

            Print("Stopping " + who + "...");
            (int code, string body) = PostStop(http, r, stopSearch);
            if (code == 409 && !stopSearch)
            {
                // A search was started between the look and the request.
                List<SearchRunInfo> now = SearchesFrom(body, "searches");
                if (!Confirm(now, interactive)) return ExitCodes.CheckFailed;
                (code, body) = PostStop(http, r, true);
            }

            if (code == 200)
            {
                bool ended = WaitForExit(r, AnswerTimeout);
                if (!ended && !force)
                {
                    Print(who + " said it was stopping but is still running after 10 seconds. Try again in a moment, or "
                          + "add --force to end its process.");
                    return ExitCodes.CheckFailed;
                }

                if (!ended) Kill(r, who);
                Print("SeedLab's web server has stopped.");
                foreach (SearchRunInfo s in SearchesFrom(body, "searches")) SayStopped(s);
                return ExitCodes.Ok;
            }

            if (code == 401 || code == 403)
            {
                Print(who + " refused the request: " + (ErrorFrom(body) ?? "its token did not match") + " It was left running.");
                return ExitCodes.CheckFailed;
            }

            // No answer at all within 10 s, or one that makes no sense.
            if (!ProcessLiveness.IsSameProcessStrict(r.Pid, r.ProcessStartedUtc))
            {
                Print("SeedLab's web server has stopped.");
                return ExitCodes.Ok;
            }

            if (!force)
            {
                Print(who + " did not answer within 10 seconds"
                      + (code > 0 ? " (it replied " + code.ToString(CultureInfo.InvariantCulture) + ")" : "") + ". It may be busy; "
                      + "try again, or add --force to end its process - a running search then loses what its last checkpoint did "
                      + "not hold.");
                return ExitCodes.CheckFailed;
            }

            Kill(r, who);
            Print("SeedLab's web server has stopped (its process was ended, --force). A search that was running lost what its "
                  + "last checkpoint did not hold.");
            return ExitCodes.Ok;
        }

        /// <summary>
        /// The second warning, in the terminal: asks when someone can answer, refuses (false) when nothing
        /// can - a prompt nobody reads is a hang, and a default of "yes" would break someone's work.
        /// </summary>
        private static bool Confirm(IReadOnlyList<SearchRunInfo> searches, bool interactive)
        {
            string what = (searches.Count == 1 ? "A search is" : searches.Count + " searches are") + " running: "
                          + ServeConsole.Describe(searches) + ".";

            // What stopping costs, said for EACH search (review of 2026-09-25): "the finished part is saved"
            // only beside a search that has a resume point, and "loses its work" beside one in a funnel's
            // first stage - never one sentence for all of them, which promised a command it then did not print.
            if (!interactive)
            {
                Print("vseed serve --stop: " + what + " Nothing is here to answer \"stop anyway?\" (stdin is redirected), so "
                      + "SeedLab was left running. Add --yes to stop it anyway. Stopping it now means:");
                foreach (string line in ServeConsole.StopCosts(searches)) Print(line, wrap: false);
                return false;
            }

            Print(what + " Stopping SeedLab stops " + (searches.Count == 1 ? "it" : "them") + ":");
            foreach (string line in ServeConsole.StopCosts(searches)) Print(line, wrap: false);
            Console.Error.Write("Stop anyway? [y/N] ");
            string answer = (Console.ReadLine() ?? "").Trim().ToLowerInvariant();
            if (answer == "y" || answer == "yes") return true;
            Print("Left running.");
            return false;
        }

        private static void SayStopped(SearchRunInfo s)
        {
            string line = "The search \"" + s.Name + "\" " + (s.StillStopping ? "was still stopping" : "stopped") + " after "
                          + s.Scanned.ToString("N0", CultureInfo.InvariantCulture) + " of "
                          + s.Limit.ToString("N0", CultureInfo.InvariantCulture) + " seeds.";
            if (s.CheckpointPath != null)
            {
                Print(line);
                Print("  Its checkpoint: " + s.CheckpointPath, wrap: false);
                Print("  To continue it: " + s.ResumeCommand, wrap: false);
            }
            else
            {
                Print(line + " It left no checkpoint to continue from.");
            }
        }

        private static bool WaitForExit(ServerRecord r, TimeSpan timeout)
        {
            Stopwatch sw = Stopwatch.StartNew();
            while (sw.Elapsed < timeout)
            {
                if (!ProcessLiveness.IsSameProcessStrict(r.Pid, r.ProcessStartedUtc)) return true;
                Thread.Sleep(100);
            }

            return !ProcessLiveness.IsSameProcessStrict(r.Pid, r.ProcessStartedUtc);
        }

        /// <summary>
        /// Ends the server's process - only once it is proved to be the one that registered: the pid AND a
        /// start time that can be read and matches (<see cref="ProcessLiveness.IsSameProcessStrict"/>). A
        /// process whose start time cannot be read is never ended from here. It is not asked over HTTP
        /// first: --force is for a server that does not answer.
        /// </summary>
        private static void Kill(ServerRecord r, string who)
        {
            try
            {
                if (!ProcessLiveness.IsSameProcessStrict(r.Pid, r.ProcessStartedUtc)) return;
                using Process p = Process.GetProcessById(r.Pid);
                p.Kill();
                p.WaitForExit(10_000);
                ServerRegistry.Unregister(r);
            }
            catch (Exception ex)
            {
                throw new CliException("could not end the process of " + who + ": " + ex.Message, ExitCodes.CheckFailed,
                                       "close its window instead");
            }
        }

        // =========================================================================================
        // The wire.

        internal sealed class ServerState
        {
            /// <summary>The pid the server says it has - a registry file's server is the one only when they match.</summary>
            public int Pid;
            public bool Stopping;
            public List<SearchRunInfo> Searches = new List<SearchRunInfo>();
        }

        internal static HttpClient Client(TimeSpan timeout) =>
            new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = timeout };

        /// <summary>
        /// GET /api/server/state from the server a record names, or null when nothing answers there as
        /// SeedLab. Only a record whose url is a loopback SeedLab address is ever asked
        /// (<see cref="ServerRegistry.IsServerUrl"/>); the caller compares <see cref="ServerState.Pid"/> with the record's.
        /// </summary>
        internal static ServerState? GetState(HttpClient http, ServerRecord r)
        {
            if (!ServerRegistry.IsServerUrl(r.Url, r.Port)) return null;
            try
            {
                using HttpResponseMessage res = http.Send(new HttpRequestMessage(HttpMethod.Get, r.Url.TrimEnd('/') + "/api/server/state"));
                if (!res.IsSuccessStatusCode) return null;
                string body = res.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                using JsonDocument d = JsonDocument.Parse(body);
                if (!d.RootElement.TryGetProperty("pid", out JsonElement pid) || pid.ValueKind != JsonValueKind.Number) return null;
                return new ServerState
                {
                    Pid = pid.GetInt32(),
                    Stopping = d.RootElement.TryGetProperty("stopping", out JsonElement st) && st.ValueKind == JsonValueKind.True,
                    Searches = SearchesFrom(body, "searches"),
                };
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static (int Code, string Body) PostStop(HttpClient http, ServerRecord r, bool stopSearch)
        {
            if (!ServerRegistry.IsServerUrl(r.Url, r.Port)) return (0, "");
            try
            {
                using HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Post, r.Url.TrimEnd('/') + "/api/server/stop")
                {
                    Content = new StringContent("{\"stopSearch\":" + (stopSearch ? "true" : "false") + ",\"by\":\"cli\"}",
                                                Encoding.UTF8, "application/json"),
                };
                req.Headers.TryAddWithoutValidation("X-SeedLab-Token", r.Token);
                using HttpResponseMessage res = http.Send(req);
                return ((int)res.StatusCode, res.Content.ReadAsStringAsync().GetAwaiter().GetResult());
            }
            catch (Exception)
            {
                return (0, "");
            }
        }

        private static List<SearchRunInfo> SearchesFrom(string body, string field)
        {
            List<SearchRunInfo> l = new List<SearchRunInfo>();
            try
            {
                using JsonDocument d = JsonDocument.Parse(body);
                if (!d.RootElement.TryGetProperty(field, out JsonElement arr) || arr.ValueKind != JsonValueKind.Array) return l;
                foreach (JsonElement e in arr.EnumerateArray())
                {
                    l.Add(new SearchRunInfo
                    {
                        Id = Str(e, "id") ?? "",
                        Name = Str(e, "name") ?? "(unnamed search)",
                        Status = Str(e, "status") ?? "",
                        Scanned = Long(e, "scanned"),
                        Limit = Long(e, "limit"),
                        Passed = Long(e, "passed"),
                        Percent = e.TryGetProperty("percent", out JsonElement p) && p.ValueKind == JsonValueKind.Number ? p.GetDouble() : 0,
                        Message = Str(e, "message"),
                        CheckpointPath = Str(e, "checkpointPath"),
                        ResumeCommand = Str(e, "resumeCommand"),
                        StillStopping = e.TryGetProperty("stillStopping", out JsonElement ss) && ss.ValueKind == JsonValueKind.True,
                    });
                }
            }
            catch (Exception)
            {
            }

            return l;
        }

        private static string? ErrorFrom(string body)
        {
            try
            {
                using JsonDocument d = JsonDocument.Parse(body);
                return Str(d.RootElement, "error");
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string? Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        private static long Long(JsonElement e, string name) =>
            e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;

        // =========================================================================================
        // Servers the registry does not know.

        private static void SayUnknown(List<UnregisteredVseed> unknown, CacheRoot root, bool noneRegistered)
        {
            if (unknown.Count == 0) return;
            Print("");
            Print((noneRegistered ? "But " : "") + (unknown.Count == 1 ? "a vseed that looks like a web server is" : unknown.Count
                  + " vseed processes that look like web servers are") + " running that " + root.Path
                  + " does not know about:");
            foreach (UnregisteredVseed u in unknown)
            {
                Print("  pid " + u.Pid.ToString(CultureInfo.InvariantCulture)
                      + (u.StartedLocal.HasValue ? ", started " + u.StartedLocal.Value.ToString("HH:mm", CultureInfo.InvariantCulture) : "")
                      + ": " + (u.CommandLine ?? "(its command line could not be read)"));
            }

            Print("It was started with another cache folder (then 'vseed serve --status --cache-dir <that folder>' finds "
                  + "it), or by a SeedLab from before this command existed. To stop it, press Ctrl+C twice in its window, or "
                  + "close its window. Nothing here stops it: nothing here can prove it is SeedLab's.");
        }

        private static void WriteUnknown(Utf8JsonWriter w, List<UnregisteredVseed> unknown)
        {
            w.WriteStartArray("unregistered");
            foreach (UnregisteredVseed u in unknown)
            {
                w.WriteStartObject();
                w.WriteNumber("pid", u.Pid);
                if (u.StartedLocal.HasValue) w.WriteString("started_utc", u.StartedLocal.Value.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture));
                if (u.CommandLine != null) w.WriteString("command_line", u.CommandLine);
                else w.WriteNull("command_line");
                w.WriteEndObject();
            }

            w.WriteEndArray();
        }

        private static void WriteSearch(Utf8JsonWriter w, SearchRunInfo s)
        {
            w.WriteStartObject();
            w.WriteString("name", s.Name);
            w.WriteNumber("percent", Math.Round(s.Percent, 2));
            w.WriteNumber("scanned", s.Scanned);
            w.WriteNumber("limit", s.Limit);
            w.WriteNumber("passed", s.Passed);
            if (s.CheckpointPath != null) w.WriteString("checkpoint", s.CheckpointPath);
            if (s.ResumeCommand != null) w.WriteString("resume_command", s.ResumeCommand);
            w.WriteEndObject();
        }

        private static void Print(string line, bool wrap = true)
        {
            if (line.Length == 0 || !wrap)
            {
                Console.Out.WriteLine(line);
                return;
            }

            // Wrapped, and continued under the first word, so a long checkpoint path stays readable.
            List<string> lines = ServeConsole.Wrap(line, 100);
            for (int i = 0; i < lines.Count; i++) Console.Out.WriteLine((i == 0 ? "" : "  ") + lines[i]);
        }
    }
}
