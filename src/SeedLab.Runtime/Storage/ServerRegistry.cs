using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SeedLab.Runtime.Storage
{
    /// <summary>One running <c>vseed serve</c>, as its registry file describes it.</summary>
    public sealed class ServerRecord
    {
        public int Pid { get; set; }

        /// <summary>When the process started (UTC), for <see cref="ProcessLiveness.IsSameProcess"/>: a pid alone is reused.</summary>
        public DateTime ProcessStartedUtc { get; set; }

        public int Port { get; set; }

        /// <summary>"http://127.0.0.1:8731".</summary>
        public string Url { get; set; } = "";

        /// <summary>When the server began listening (UTC).</summary>
        public DateTime StartedUtc { get; set; }

        /// <summary>vseed's version, "0.1.0".</summary>
        public string Version { get; set; } = "";

        /// <summary>
        /// The server's secret: 32 random bytes as hex. Asked for by the endpoints that stop the server
        /// or reset its idle clock, so that only something that can read this file - or the page, which
        /// reads it from its own server - can call them.
        /// </summary>
        public string Token { get; set; } = "";

        /// <summary>The file this record was read from or written to.</summary>
        public string File { get; set; } = "";

        /// <summary>The start time as the local clock reads it, "14:03" - what a person is told.</summary>
        public string SinceLocal => StartedUtc.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
    }

    /// <summary>What a look at the registry found, without changing anything.</summary>
    public sealed class ServerScan
    {
        /// <summary>The servers whose process is alive and is the one that wrote the file, newest first.</summary>
        public List<ServerRecord> Live { get; } = new List<ServerRecord>();

        /// <summary>
        /// Files of servers whose process has ended (a kill, a crash, a power cut) - including one whose pid
        /// now belongs to another process, or to one whose start time this account cannot read: reaped by
        /// the next server.
        /// </summary>
        public List<ServerRecord> Stale { get; } = new List<ServerRecord>();

        /// <summary>Files that could not be read or are not a registry file.</summary>
        public List<string> Unreadable { get; } = new List<string>();

        /// <summary>The folder looked in; it may not exist.</summary>
        public string Folder { get; set; } = "";
    }

    /// <summary>
    /// The web servers that are running, one small file each: <c>&lt;cache root&gt;\serve\server-&lt;pid&gt;.json</c>
    /// (2026-09-24, the web server's lifecycle, decided by the user).
    ///
    /// <para><b>What it is for.</b> "Is SeedLab's web server running, where, and how do I stop it?" had
    /// only one answer before - look for a window - because nothing but the process itself knew its
    /// port. The scripts had to list processes and ask the operating system which ports each one was
    /// listening on. With this file, <c>vseed serve --status</c>, <c>vseed serve --stop</c> and a second
    /// <c>vseed serve</c> find a running server by reading one folder.</para>
    ///
    /// <para><b>Written only after the port is bound, and atomically</b> (<see cref="DurableWrite"/>), so
    /// a file never names a server that is not listening, and a reader never sees half of one. Deleted
    /// when the server stops cleanly. A server that was killed leaves its file; the next reader checks
    /// the pid AND the process start time (<see cref="ProcessLiveness.IsSameProcessStrict"/>) and treats
    /// the file as stale - a pid alone is reused by the operating system - and the next <c>vseed serve</c>
    /// deletes it. A start time that cannot be read counts as stale too: the pid then belongs to a
    /// process of another account or of the system, never to the user's own server (review of
    /// 2026-09-25 - the lenient check locked a user out of starting, stopping and uninstalling).</para>
    ///
    /// <para><b>The address must be a SeedLab address.</b> A record is read only when its <c>url</c> is
    /// exactly <c>http://127.0.0.1:&lt;port&gt;</c> with the file's own port: the url is handed to the
    /// browser (a second <c>vseed serve</c> opens it) and asked for the server's state, and a file that
    /// named a program or another host would otherwise be opened or asked (review of 2026-09-25).</para>
    ///
    /// <para><b>Reading never writes.</b> <see cref="Scan"/> creates nothing and deletes nothing, so
    /// <c>--status</c> and <c>--stop</c> can be run against a cache root that an uninstall has just
    /// removed without bringing it back.</para>
    ///
    /// <para><b>Not a <c>vseed clean</c> category, on purpose.</b> <c>clean</c> deletes what nothing
    /// needs any more; a live server's file is needed for as long as the server runs, and a stale one is
    /// reaped by the next server. An uninstall removes the whole cache root, and this folder with it.</para>
    ///
    /// <para><b>The token is in the file</b>, so anything that can read the user's cache root can stop the
    /// server. That is the same account that could close its window. The token keeps OTHER WEB PAGES out,
    /// and only them: a page cannot read this file, and cannot read the page's own copy of it either (there
    /// is no CORS). It does NOT keep out a program running on this computer, or another account on it: the
    /// page reads the token from <c>GET /api/meta</c> on 127.0.0.1, and anything that can reach 127.0.0.1
    /// can do the same (review of 2026-09-25). On Linux and macOS the folder and the file are created
    /// readable by their owner only, which keeps the file private and nothing more.</para>
    /// </summary>
    public static class ServerRegistry
    {
        /// <summary>The folder's name inside the cache root.</summary>
        public const string FolderName = "serve";

        private const string Prefix = "server-";
        private const string Suffix = ".json";

        public static string FolderFor(string cacheRoot) => Path.Combine(cacheRoot, FolderName);

        public static string FileFor(string cacheRoot, int pid) =>
            Path.Combine(FolderFor(cacheRoot), Prefix + pid.ToString(CultureInfo.InvariantCulture) + Suffix);

        /// <summary>A new secret for a server: 32 random bytes, as 64 hex digits.</summary>
        public static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

        /// <summary>
        /// Writes this process's record into <paramref name="cacheRoot"/> (creating <c>serve\</c>) and
        /// returns it. Call it once the port is bound.
        /// </summary>
        /// <exception cref="FileAccessException">The file could not be written; the message names it.</exception>
        public static ServerRecord Register(string cacheRoot, int port, string url, string version, string token)
        {
            ServerRecord r = new ServerRecord
            {
                Pid = Environment.ProcessId,
                ProcessStartedUtc = ProcessLiveness.CurrentStartTimeUtc,
                Port = port,
                Url = url,
                StartedUtc = DateTime.UtcNow,
                Version = version ?? "",
                Token = token ?? "",
                File = FileFor(cacheRoot, Environment.ProcessId),
            };

            // On Linux and macOS the folder and the file are owner-only FROM THE START (review of
            // 2026-09-25): the file used to be written with the default mode - usually readable by every
            // account - and narrowed afterwards, so for a moment it was not. The temp file is narrowed
            // before the rename puts it in place. On Windows the cache root inherits the account's own
            // folder's permissions. (The token is also on GET /api/meta; see the class comment.)
            byte[] json = DurableWrite.Utf8NoBom.GetBytes(ToJson(r));
            if (OperatingSystem.IsWindows())
            {
                Directory.CreateDirectory(FolderFor(cacheRoot));
                DurableWrite.Bytes(r.File, json, RetrySchedule.Quick);
                return r;
            }

            Directory.CreateDirectory(FolderFor(cacheRoot), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            string temp = DurableWrite.WriteTemp(r.File, s => s.Write(json, 0, json.Length), RetrySchedule.Quick);
            try
            {
                try
                {
                    File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
                catch (Exception)
                {
                    // A file system without modes (a FAT stick): the file is still the user's own.
                }

                DurableWrite.Replace(temp, r.File, RetrySchedule.Quick);
            }
            catch (Exception)
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch (Exception) { }
                throw;
            }

            return r;
        }

        /// <summary>Deletes a record's file. Never throws; true when it is gone.</summary>
        public static bool Unregister(ServerRecord? r)
        {
            if (r == null || string.IsNullOrEmpty(r.File)) return true;
            try
            {
                if (File.Exists(r.File)) File.Delete(r.File);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Every record in <paramref name="cacheRoot"/>, sorted into live and stale. Creates nothing and
        /// deletes nothing; a root or folder that does not exist is simply "no servers".
        /// </summary>
        public static ServerScan Scan(string cacheRoot, Func<int, DateTime, bool>? isSameProcess = null)
        {
            isSameProcess ??= ProcessLiveness.IsSameProcessStrict;
            ServerScan scan = new ServerScan { Folder = FolderFor(cacheRoot) };
            string[] files;
            try
            {
                if (!Directory.Exists(scan.Folder)) return scan;
                files = Directory.GetFiles(scan.Folder, Prefix + "*" + Suffix);
            }
            catch (Exception)
            {
                return scan;
            }

            foreach (string f in files)
            {
                ServerRecord? r = Read(f);
                if (r == null)
                {
                    scan.Unreadable.Add(f);
                    continue;
                }

                if (isSameProcess(r.Pid, r.ProcessStartedUtc)) scan.Live.Add(r);
                else scan.Stale.Add(r);
            }

            scan.Live.Sort((a, b) => b.StartedUtc.CompareTo(a.StartedUtc));
            return scan;
        }

        /// <summary>Deletes the files of servers that have ended; returns how many went. For a starting server only.</summary>
        public static int ReapStale(string cacheRoot)
        {
            int removed = 0;
            // Stale means "not the process that wrote it" - a pid this process now has but an older start
            // time is stale too, and its name is the one this process registers under anyway.
            foreach (ServerRecord r in Scan(cacheRoot).Stale)
            {
                if (Unregister(r)) removed++;
            }

            return removed;
        }

        /// <summary>One record, or null when the file is missing, held, or not a registry file.</summary>
        public static ServerRecord? Read(string path)
        {
            try
            {
                string text = SharedRead.AllText(path);
                using JsonDocument d = JsonDocument.Parse(text);
                JsonElement e = d.RootElement;
                ServerRecord r = new ServerRecord
                {
                    Pid = e.GetProperty("pid").GetInt32(),
                    ProcessStartedUtc = DateTime.Parse(e.GetProperty("process_started_utc").GetString() ?? "",
                        CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal),
                    Port = e.GetProperty("port").GetInt32(),
                    Url = e.GetProperty("url").GetString() ?? "",
                    StartedUtc = DateTime.Parse(e.GetProperty("started_utc").GetString() ?? "",
                        CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal),
                    Version = e.TryGetProperty("version", out JsonElement v) ? v.GetString() ?? "" : "",
                    Token = e.GetProperty("token").GetString() ?? "",
                    File = path,
                };
                return r.Pid > 0 && IsServerUrl(r.Url, r.Port) ? r : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// True when <paramref name="url"/> is exactly what a server writes: <c>http://127.0.0.1:&lt;port&gt;</c>
        /// (a trailing slash allowed) on <paramref name="port"/>, 1-65535 - no path, no query, no other host
        /// and no other scheme. Anything else in a registry file is not a SeedLab server's address.
        /// </summary>
        public static bool IsServerUrl(string? url, int port)
        {
            if (string.IsNullOrEmpty(url) || port < 1 || port > 65535) return false;
            string expected = "http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture);
            return string.Equals(url, expected, StringComparison.Ordinal)
                   || string.Equals(url, expected + "/", StringComparison.Ordinal);
        }

        private static string ToJson(ServerRecord r)
        {
            using MemoryStream ms = new MemoryStream();
            using (Utf8JsonWriter w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
            {
                w.WriteStartObject();
                w.WriteString("what", "a running vseed serve: deleted when it stops; a file whose process has ended is ignored, and removed by the next server");
                w.WriteNumber("pid", r.Pid);
                w.WriteString("process_started_utc", r.ProcessStartedUtc.ToString("o", CultureInfo.InvariantCulture));
                w.WriteNumber("port", r.Port);
                w.WriteString("url", r.Url);
                w.WriteString("started_utc", r.StartedUtc.ToString("o", CultureInfo.InvariantCulture));
                w.WriteString("version", r.Version);
                w.WriteString("token", r.Token);
                w.WriteEndObject();
            }

            return Encoding.UTF8.GetString(ms.ToArray()) + "\n";
        }
    }
}
