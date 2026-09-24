using System;
using System.IO;
using System.Text.RegularExpressions;
using SeedLab.Runtime.Storage;

namespace SeedLab.RuntimeTests
{
    /// <summary>
    /// The web servers' registry (2026-09-24): <c>&lt;cache root&gt;\serve\server-&lt;pid&gt;.json</c>, which
    /// <c>vseed serve --status</c>, <c>--stop</c> and a second <c>vseed serve</c> read. Written and read
    /// back; a stale file (a dead pid, a reused pid) told from a live one; reading creates nothing; the
    /// folder is not a <c>vseed clean</c> category. Everything under a temp folder.
    /// </summary>
    public static class ServerRegistryChecks
    {
        public static void Run(Action<bool, string, string> check)
        {
            string tempRoot = Path.Combine(Path.GetTempPath(), "seedlab-registry-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                // ---- reading an empty or missing root creates nothing ------------------------------------
                string missing = Path.Combine(tempRoot, "not-there");
                ServerScan none = ServerRegistry.Scan(missing);
                check(none.Live.Count == 0 && none.Stale.Count == 0 && !Directory.Exists(missing),
                      "a scan of a cache root that does not exist finds nothing and creates nothing",
                      Directory.Exists(missing) ? "the root was CREATED" : "still absent");

                CacheRoot root = CacheRoot.Open(new CacheRootOptions { Override = Path.Combine(tempRoot, "root") });
                check(!Directory.Exists(root.Serve), "a cache root opened for a command has no serve folder until a server registers",
                      root.Serve);
                bool inClean = false;
                foreach ((string name, string _) in root.Categories) inClean |= name == ServerRegistry.FolderName;
                check(!inClean, "serve is not one of the categories vseed clean walks: a live server's file is never cleaned",
                      root.Categories.Count + " categories");

                // ---- the token ------------------------------------------------------------------------
                string t1 = ServerRegistry.NewToken(), t2 = ServerRegistry.NewToken();
                check(Regex.IsMatch(t1, "^[0-9a-f]{64}$") && t1 != t2, "a token is 32 random bytes as 64 hex digits, new each time",
                      t1.Substring(0, 8) + "...");

                // ---- this process, registered ---------------------------------------------------------
                ServerRecord mine = ServerRegistry.Register(root.Path, 8731, "http://127.0.0.1:8731", "9.9.9", t1);
                string expectFile = Path.Combine(root.Path, "serve", "server-" + Environment.ProcessId + ".json");
                check(mine.File == expectFile && File.Exists(expectFile), "a server registers as serve\\server-<pid>.json", mine.File);

                ServerScan one = ServerRegistry.Scan(root.Path);
                ServerRecord? back = one.Live.Count == 1 ? one.Live[0] : null;
                check(back != null && back.Pid == Environment.ProcessId && back.Port == 8731 && back.Url == "http://127.0.0.1:8731"
                      && back.Token == t1 && back.Version == "9.9.9"
                      && Math.Abs((back.ProcessStartedUtc - ProcessLiveness.CurrentStartTimeUtc).TotalSeconds) < 1,
                      "it reads back whole, and as live: this process, at the start time it recorded",
                      back == null ? one.Live.Count + " live" : back.Url + " pid " + back.Pid);
                check(Directory.GetFiles(Path.Combine(root.Path, "serve"), DurableWrite.TempPrefix + "*").Length == 0,
                      "written by temp-and-rename, nothing left beside it", "");

                // ---- stale files: a dead pid, a reused pid, and one that is not a registry file --------------
                string dead = Path.Combine(root.Path, "serve", "server-999999.json");
                File.WriteAllText(dead, File.ReadAllText(expectFile).Replace("\"pid\": " + Environment.ProcessId, "\"pid\": 999999"));
                string reused = Path.Combine(root.Path, "serve", "server-1.json");
                File.WriteAllText(reused, File.ReadAllText(expectFile)
                    .Replace(mine.ProcessStartedUtc.ToString("o", System.Globalization.CultureInfo.InvariantCulture), "1999-01-01T00:00:00.0000000Z"));
                string junk = Path.Combine(root.Path, "serve", "server-2.json");
                File.WriteAllText(junk, "not json");

                ServerScan mixed = ServerRegistry.Scan(root.Path);
                check(mixed.Live.Count == 1 && mixed.Stale.Count == 2 && mixed.Unreadable.Count == 1,
                      "a dead pid and a pid that was reused (same pid, another start time) are stale; a broken file is unreadable",
                      mixed.Live.Count + " live, " + mixed.Stale.Count + " stale, " + mixed.Unreadable.Count + " unreadable");
                check(File.Exists(dead) && File.Exists(reused), "and a scan deletes nothing", "");

                int reaped = ServerRegistry.ReapStale(root.Path);
                check(reaped == 2 && !File.Exists(dead) && !File.Exists(reused) && File.Exists(expectFile),
                      "a starting server reaps the stale ones and leaves the live one", reaped + " reaped");
                File.Delete(junk);

                // ---- owner-only on Linux and macOS, from the start (review of 2026-09-25) -------------------
                if (OperatingSystem.IsWindows())
                {
                    check(true, "the registry file is owner-only on Linux and macOS (SKIPPED on Windows: no file modes)", "");
                }
                else
                {
                    UnixFileMode fileMode = File.GetUnixFileMode(expectFile);
                    UnixFileMode dirMode = File.GetUnixFileMode(Path.Combine(root.Path, "serve"));
                    check(fileMode == (UnixFileMode.UserRead | UnixFileMode.UserWrite)
                          && dirMode == (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute),
                          "on Linux and macOS the file is 0600 and its folder 0700", fileMode + " / " + dirMode);
                }

                // ---- a clean stop ---------------------------------------------------------------------
                bool gone = ServerRegistry.Unregister(mine);
                check(gone && !File.Exists(expectFile) && ServerRegistry.Scan(root.Path).Live.Count == 0,
                      "a stopped server's file is deleted, and the next scan finds nothing", "");
                check(ServerRegistry.Unregister(mine), "deleting it twice is harmless", "");

                // ---- a left-over file whose pid now belongs to a process this account cannot inspect ---------
                // (review of 2026-09-25): it used to count as a live server - "already running", --stop that
                // could not stop it, an uninstall that refused - because an unreadable start time meant "alive".
                CacheRoot root2 = CacheRoot.Open(new CacheRootOptions { Override = Path.Combine(tempRoot, "root2") });
                string serve2 = Path.Combine(root2.Path, "serve");
                Directory.CreateDirectory(serve2);
                int systemPid = OperatingSystem.IsWindows() ? 4 : 1;
                string planted = Path.Combine(serve2, "server-" + systemPid + ".json");
                File.WriteAllText(planted, Json(systemPid, "2020-01-01T00:00:00.0000000Z", 1, "http://127.0.0.1:1"));
                ServerScan sys = ServerRegistry.Scan(root2.Path);
                check(sys.Live.Count == 0 && sys.Stale.Count == 1 && !ProcessLiveness.IsSameProcessStrict(systemPid, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
                      "a file naming a system process (pid " + systemPid + ") whose start time this account cannot read, or which "
                      + "started at another time, is STALE, not a live server",
                      sys.Live.Count + " live, " + sys.Stale.Count + " stale; the scratch reaper's lenient check says "
                      + (ProcessLiveness.IsSameProcess(systemPid, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc)) ? "alive" : "not alive"));
                File.Delete(planted);

                // ---- the address must be SeedLab's own ----------------------------------------------------
                // A file naming THIS process (so live by pid and start time) with an address that is not
                // http://127.0.0.1:<its port>: a program, another host, another port. Never read as a server.
                string me = ProcessLiveness.CurrentStartTimeUtc.ToString("o", System.Globalization.CultureInfo.InvariantCulture);
                string[] badUrls =
                {
                    @"C:\Windows\System32\calc.exe", "file:///C:/Windows/System32/calc.exe", "http://evil.example:8731",
                    "http://127.0.0.1:9999", "https://127.0.0.1:8731", "http://127.0.0.1:8731/../x", "http://localhost:8731",
                };
                int rejected = 0;
                foreach (string bad in badUrls)
                {
                    string f = Path.Combine(serve2, "server-" + Environment.ProcessId + ".json");
                    File.WriteAllText(f, Json(Environment.ProcessId, me, 8731, bad));
                    ServerScan s = ServerRegistry.Scan(root2.Path);
                    if (s.Live.Count == 0 && s.Unreadable.Count == 1 && ServerRegistry.Read(f) == null) rejected++;
                    File.Delete(f);
                }

                string good = Path.Combine(serve2, "server-" + Environment.ProcessId + ".json");
                File.WriteAllText(good, Json(Environment.ProcessId, me, 8731, "http://127.0.0.1:8731"));
                bool goodLive = ServerRegistry.Scan(root2.Path).Live.Count == 1;
                File.Delete(good);
                check(rejected == badUrls.Length && goodLive,
                      "a record is read only when its url is exactly http://127.0.0.1:<its own port>: a program's path, another "
                      + "host, another port or scheme are refused",
                      rejected + " of " + badUrls.Length + " refused; the right one " + (goodLive ? "read" : "NOT read"));
            }
            finally
            {
                try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true); } catch (Exception) { }
            }
        }

        /// <summary>A registry file as a server writes it, with these values.</summary>
        private static string Json(int pid, string startedIso, int port, string url) =>
            "{\n  \"pid\": " + pid + ",\n  \"process_started_utc\": \"" + startedIso + "\",\n  \"port\": " + port
            + ",\n  \"url\": " + System.Text.Json.JsonSerializer.Serialize(url) + ",\n  \"started_utc\": \"2026-09-25T00:00:00.0000000Z\",\n"
            + "  \"version\": \"0.1.0\",\n  \"token\": \"" + new string('a', 64) + "\"\n}\n";
    }
}
