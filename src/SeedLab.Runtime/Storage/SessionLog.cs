using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace SeedLab.Runtime.Storage
{
    public enum SessionLogLevel
    {
        Info = 0,
        Warn = 1,
        Error = 2,
    }

    /// <summary>
    /// How much one session may write to its log (<see cref="SessionLog"/>).
    ///
    /// <para><b>Why a cap at all, and why these numbers</b> (2026-09-24). A normal session writes a few
    /// kilobytes: the header (about 40 lines), the lines the user was shown, the retries and the end. A
    /// <c>vseed serve</c> left open for days adds a line per idle reminder and a few per search - still
    /// kilobytes. What CAN grow a log is a fault that repeats: a checkpoint save failing every 30 s for a
    /// week, a warning on every request. Nothing reads a log that size, and the user asked for the logs
    /// to "retain information, but not a lot of information". So:</para>
    /// <list type="bullet">
    /// <item><b>4 MiB of this session's lines</b> - about a thousand times a normal session, and a size
    /// Notepad opens at once - and then ordinary (INFO) lines are dropped, said once. Warnings and errors
    /// are what a fault leaves, and they go on being written.</item>
    /// <item><b>16 MiB in all</b>, and then nothing more, said once. A fault that repeats a warning
    /// forever must not fill the drive; with two logs, SeedLab's logs can never take more than about
    /// 40 MiB.</item>
    /// </list>
    /// <para>The session's last line (<see cref="Last"/>) is written past the INFO cap, because "ended with
    /// exit code N" is the line a report needs most.</para>
    /// </summary>
    public sealed class SessionLogLimits
    {
        public static readonly SessionLogLimits Default = new SessionLogLimits(4L << 20, 16L << 20);

        public SessionLogLimits(long infoBytes, long totalBytes)
        {
            InfoBytes = Math.Max(1, infoBytes);
            TotalBytes = Math.Max(InfoBytes, totalBytes);
        }

        /// <summary>After this many bytes of this session's lines, INFO lines are dropped.</summary>
        public long InfoBytes { get; }

        /// <summary>After this many, nothing more is written.</summary>
        public long TotalBytes { get; }
    }

    /// <summary>
    /// The session logs: <c>&lt;cache root&gt;\logs\vseed.log</c> for this session and
    /// <c>vseed-prev.log</c> for the one before it - exactly two files, the user's decision of
    /// 2026-09-24: <i>"a current log, and a last session log. That way, we can retain information, but
    /// not a lot of information, and there can be redundancy in case of issues."</i>
    ///
    /// <para><b>The model, and where it differs</b> (2026-09-24, BepInEx 5.4.23.3's DiskLogListener
    /// decompiled). BepInEx opens its log with <c>FileMode.Create</c> (so every start empties it),
    /// <c>FileAccess.Write</c> and <c>FileShare.Read</c>; when that fails because another game instance
    /// holds it, it tries <c>LogOutput.log.1</c> .. <c>.4</c>, and after five names it runs with no disk
    /// log. This does the same, with these differences:</para>
    /// <list type="bullet">
    /// <item><b>The last session's log is kept.</b> At the start of a session <c>vseed.log</c> is renamed
    /// <c>vseed-prev.log</c> (replacing the older one), and only then is a fresh <c>vseed.log</c> made.
    /// The rename is retried while another program - a virus scanner, an editor - holds a file. When it is
    /// <c>vseed-prev.log</c> that is held, that file is kept as it is, this session writes on in
    /// <c>vseed.log</c> after the last session's lines rather than delete them, and the log says so.
    /// When <c>vseed.log</c> is held by a LIVE session, nothing is renamed: this session writes
    /// <c>vseed.log.1</c> .. <c>.4</c> as before.</item>
    /// <item><b>Stale numbered logs are deleted.</b> BepInEx never deletes a <c>.N</c> file, so one
    /// left by an old overlap stays forever. After opening its own, a session deletes every numbered log
    /// no live session holds.</item>
    /// <item><b>Every line is flushed.</b> BepInEx flushes on a 2 s timer, so a crash loses up to two
    /// seconds - the part a crash report needs. Here each line goes to the operating system as it is
    /// written, which a crashed or killed process cannot take back. (It is not forced to the disk
    /// itself; a power cut can still lose the tail.)</item>
    /// <item><b>It is capped</b> (<see cref="SessionLogLimits"/>).</item>
    /// <item><b>It never throws.</b> BepInEx catches only the sharing failure; a read-only log file
    /// throws out of its constructor. Here any failure to open a name moves on to the next one, and
    /// any failure to write stops the log quietly - a log must never be why a run fails.</item>
    /// </list>
    ///
    /// <para><b>Who holds a log.</b> The first line names the process that writes it and when that
    /// process started. A log another program holds is told apart from one a live vseed session holds by
    /// that line, so a second command run while <c>vseed serve</c> is open goes straight to
    /// <c>vseed.log.1</c> instead of waiting out the rename's retries. "Held" is asked of the file itself
    /// (an exclusive open), which also works on Linux and macOS, where a rename or a delete never fails
    /// because the file is open - there, without the check, a second session would rename or empty a live
    /// session's log.</para>
    ///
    /// <para><b>Share mode.</b> Read only, deliberately not Delete: a second session cannot empty or
    /// delete a live session's log, and so falls back to the next name. Anything that reads the log
    /// while a session runs - a person, <c>vseed clean</c> - has to open it sharing read AND write.</para>
    ///
    /// <para><b>Line format:</b> <c>yyyy-MM-dd HH:mm:ss.fff zzz  LEVEL  text</c>, local time with its
    /// UTC offset; continuation lines are indented to the text column.</para>
    /// </summary>
    public sealed class SessionLog : IDisposable
    {
        /// <summary>The log's name inside the logs folder.</summary>
        public const string FileName = "vseed.log";

        /// <summary>The last session's log, beside <see cref="FileName"/>.</summary>
        public const string PreviousFileName = "vseed-prev.log";

        /// <summary>How many numbered names follow the plain one: vseed.log.1 .. vseed.log.4.</summary>
        public const int Fallbacks = 4;

        /// <summary>The waits between the rename's attempts while another program holds a log: about 0.8 s.</summary>
        private static readonly int[] RotateWaits = { 10, 25, 50, 100, 200, 400 };

        private static readonly Regex WriterLine = new Regex(
            @"written by process (\d+) \(started (\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}) UTC\)", RegexOptions.CultureInvariant);

        private readonly object _gate = new object();
        private readonly SessionLogLimits _limits;
        private StreamWriter? _writer;
        private long _written;
        private bool _infoDropped;
        private long _droppedLines;

        private SessionLog(string directory, string? path, StreamWriter? writer, string? problem, int index,
                           IReadOnlyList<string> deleted, SessionLogLimits? limits, string? previousPath,
                           IReadOnlyList<string> rotation)
        {
            Directory = directory;
            Path = path;
            _writer = writer;
            Problem = problem;
            Index = index;
            DeletedStale = deleted;
            _limits = limits ?? SessionLogLimits.Default;
            PreviousPath = previousPath;
            Rotation = rotation;
            Started = DateTimeOffset.Now;
        }

        /// <summary>
        /// The log of the session this process is running, for code too deep to be handed one - the
        /// retry helper, the access checks. Set by <see cref="RuntimeContext.Start"/> and put back when
        /// that context is disposed. Null outside a session: every write to it is then skipped.
        /// </summary>
        public static SessionLog? Current { get; set; }

        /// <summary>The folder the log lives in.</summary>
        public string Directory { get; }

        /// <summary>The file this session writes, or null when no name could be opened.</summary>
        public string? Path { get; }

        /// <summary>
        /// The last session's log, <c>vseed-prev.log</c>, when there is one beside this session's -
        /// null when this session writes a numbered log (nothing was renamed) or no log at all.
        /// </summary>
        public string? PreviousPath { get; }

        /// <summary>
        /// What happened to the last session's log at the start of this one, in words - "the last
        /// session's log is now ...vseed-prev.log", or why it could not be moved. Also written into the
        /// log itself, right after its first line.
        /// </summary>
        public IReadOnlyList<string> Rotation { get; }

        /// <summary>True while lines are being written to a file.</summary>
        public bool IsOpen
        {
            get
            {
                lock (_gate) return _writer != null;
            }
        }

        /// <summary>0 for vseed.log, 1..4 for a numbered fallback, -1 for no log.</summary>
        public int Index { get; }

        /// <summary>
        /// Why this session writes a numbered log, or none at all - "vseed.log is in use by another
        /// session" - or null when it writes vseed.log as normal.
        /// </summary>
        public string? Problem { get; }

        /// <summary>Numbered logs from earlier sessions that this one deleted on opening.</summary>
        public IReadOnlyList<string> DeletedStale { get; }

        public DateTimeOffset Started { get; }

        /// <summary>INFO lines not written because the log passed <see cref="SessionLogLimits.InfoBytes"/>.</summary>
        public long DroppedLines
        {
            get
            {
                lock (_gate) return _droppedLines;
            }
        }

        /// <summary>
        /// Opens this session's log in <paramref name="directory"/> (creating the folder): the last
        /// session's <c>vseed.log</c> becomes <c>vseed-prev.log</c> and a fresh <c>vseed.log</c> is
        /// written, or a numbered log while a live session holds <c>vseed.log</c>. Never throws: when
        /// nothing can be opened the result writes nowhere and <see cref="Problem"/> says why.
        /// </summary>
        public static SessionLog Open(string directory, string fileName = FileName, SessionLogLimits? limits = null)
        {
            SessionLogLimits lim = limits ?? SessionLogLimits.Default;
            string dir = directory ?? "";
            try
            {
                dir = System.IO.Path.GetFullPath(dir);
                System.IO.Directory.CreateDirectory(dir);
            }
            catch (Exception ex)
            {
                return new SessionLog(dir, null, null,
                    "the log folder " + dir + " could not be created (" + FileRetry.Describe(ex) + ")", -1,
                    Array.Empty<string>(), lim, null, Array.Empty<string>());
            }

            // ---- the last session's log becomes vseed-prev.log --------------------------------------
            string plain = NameAt(dir, fileName, 0);
            string prev = PreviousName(dir, fileName);
            List<string> rotation = new List<string>();
            string? plainRefused = null;
            FileMode plainMode = FileMode.Create;
            bool rotated = false;
            try
            {
                if (File.Exists(plain))
                {
                    int holder = HolderOf(plain);
                    if (holder > 0)
                    {
                        // A live session writes it: nothing is renamed and nothing is waited for.
                        plainRefused = fileName + " is in use by another session (process "
                                       + holder.ToString(CultureInfo.InvariantCulture) + ")";
                    }
                    else if (TryRotate(plain, prev))
                    {
                        rotated = true;
                        rotation.Add("the last session's log is now " + prev);
                    }
                    else if (Held(plain))
                    {
                        plainRefused = fileName + " is in use by another program";
                    }
                    else
                    {
                        // vseed.log is free, so it is vseed-prev.log that could not be replaced. It is
                        // kept as it is; the last session's lines are kept too, above this session's,
                        // unless they are already past the INFO cap - then keeping them would make one
                        // file of two sessions' worth of a fault.
                        string why = WhyHeld(prev);
                        long size = LengthOf(plain);
                        if (size <= lim.InfoBytes)
                        {
                            plainMode = FileMode.Append;
                            rotation.Add(prev + " could not be replaced - " + why + " - so it was kept as it is, and the "
                                         + "last session's log was not moved there: its lines are above this session's, "
                                         + "in this file.");
                        }
                        else
                        {
                            rotation.Add(prev + " could not be replaced - " + why + " - so it was kept as it is; the last "
                                         + "session's log was " + Hardware.Bytes.Human(size) + ", too big to keep in this "
                                         + "file as well, and was emptied.");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // Never a reason to keep no log: go on and open what can be opened.
                rotation.Add("the last session's log could not be looked at (" + FileRetry.Describe(ex) + ")");
            }

            // ---- this session's log -------------------------------------------------------------------
            List<string> refused = new List<string>();
            for (int i = 0; i <= Fallbacks; i++)
            {
                string path = NameAt(dir, fileName, i);
                if (i == 0 && plainRefused != null)
                {
                    refused.Add(plainRefused);
                    continue;
                }

                // Linux and macOS let a second session empty a live session's log (a sharing mode there is
                // only advisory), so a live one is asked for by name first, as vseed.log was.
                if (i > 0 && File.Exists(path) && HolderOf(path) > 0)
                {
                    refused.Add(System.IO.Path.GetFileName(path) + " is in use by another session");
                    continue;
                }

                FileStream? fs = null;
                try
                {
                    FileMode mode = i == 0 ? plainMode : FileMode.Create;
                    fs = new FileStream(path, mode, FileAccess.Write, FileShare.Read);
                    StreamWriter w = new StreamWriter(fs, DurableWrite.Utf8NoBom) { AutoFlush = true, NewLine = "\n" };
                    List<string> deleted = DeleteStale(dir, fileName, i);
                    string? problem = i == 0
                        ? null
                        : string.Join(", ", refused) + ", so this session writes " + System.IO.Path.GetFileName(path);
                    string? previousPath = i == 0 && (rotated || plainMode == FileMode.Append || File.Exists(prev)) ? prev : null;
                    if (i != 0) rotation.Clear();
                    SessionLog log = new SessionLog(dir, path, w, problem, i, deleted, lim, previousPath, rotation);
                    log.Info(FirstLine(fileName));
                    foreach (string r in rotation) log.Info("log      " + r);
                    return log;
                }
                catch (Exception ex)
                {
                    try
                    {
                        fs?.Dispose();
                    }
                    catch (Exception)
                    {
                    }

                    refused.Add(System.IO.Path.GetFileName(path)
                                + (ex is UnauthorizedAccessException ? " is read-only or not allowed"
                                   : ex.HResult == FileRetry.SharingViolation || ex.HResult == FileRetry.LockViolation
                                       ? " is in use by another session"
                                       : " could not be opened"));
                }
            }

            return new SessionLog(dir, null, null,
                string.Join(", ", refused) + ", so this session keeps no log", -1, Array.Empty<string>(), lim, null,
                Array.Empty<string>());
        }

        /// <summary>A log that writes nowhere, for a session that is not to keep one; <see cref="Problem"/> is <paramref name="why"/>.</summary>
        public static SessionLog Disabled(string directory, string why) =>
            new SessionLog(directory ?? "", null, null, why, -1, Array.Empty<string>(), null, null, Array.Empty<string>());

        /// <summary>vseed.log for 0, vseed.log.N for N.</summary>
        public static string NameAt(string directory, string fileName, int index) =>
            System.IO.Path.Combine(directory, index == 0 ? fileName : fileName + "." + index.ToString(CultureInfo.InvariantCulture));

        /// <summary>vseed-prev.log for vseed.log: the name the last session's log is given.</summary>
        public static string PreviousName(string directory, string fileName = FileName)
        {
            string ext = System.IO.Path.GetExtension(fileName);
            string stem = ext.Length > 0 ? fileName.Substring(0, fileName.Length - ext.Length) : fileName;
            return System.IO.Path.Combine(directory, stem + "-prev" + ext);
        }

        /// <summary>
        /// The first line of every log: who writes it (the pid and the process's start time, which is how a
        /// later session tells a live one from a stale file) and the two-file rule.
        /// </summary>
        private static string FirstLine(string fileName)
        {
            string started = ProcessLiveness.CurrentStartTimeUtc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            string prevName = System.IO.Path.GetFileName(PreviousName("", fileName));
            return "vseed session log, written by process " + Environment.ProcessId.ToString(CultureInfo.InvariantCulture)
                   + " (started " + started + " UTC). At the start of every session the last session's log is renamed "
                   + prevName + " (replacing the one before it) and this file starts again, so there are two: this "
                   + "session's and the last one's. While another session still has " + fileName + " open, a new one "
                   + "writes " + fileName + ".1 (up to ." + Fallbacks + ") instead, and numbered logs no session is using "
                   + "are deleted when the next session starts.";
        }

        public void Info(string text) => Write(SessionLogLevel.Info, text);

        public void Warn(string text) => Write(SessionLogLevel.Warn, text);

        public void Error(string text) => Write(SessionLogLevel.Error, text);

        /// <summary>An exception's type, message and stack trace - for the log only, never for the screen.</summary>
        public void Exception(string what, Exception ex)
        {
            if (ex == null) return;
            Write(SessionLogLevel.Error, what + ": " + ex.GetType().FullName + " 0x"
                                         + ex.HResult.ToString("X8", CultureInfo.InvariantCulture) + ": " + ex.Message
                                         + "\n" + ex);
        }

        /// <summary>
        /// The session's last line, written even past the INFO cap - with a count of the lines the cap
        /// left out, when it left any out. Still subject to the total cap.
        /// </summary>
        public void Last(string text)
        {
            lock (_gate)
            {
                if (_writer == null) return;
                if (_droppedLines > 0)
                {
                    Append(SessionLogLevel.Warn, "log      " + _droppedLines.ToString("N0", CultureInfo.InvariantCulture)
                                                  + " ordinary line" + (_droppedLines == 1 ? " was" : "s were")
                                                  + " left out of this log after it passed " + Hardware.Bytes.Human(_limits.InfoBytes));
                }

                Append(SessionLogLevel.Info, text);
            }
        }

        /// <summary>One entry. Several lines are kept together, indented under the first. Never throws.</summary>
        public void Write(SessionLogLevel level, string text)
        {
            lock (_gate)
            {
                if (_writer == null) return;
                if (level == SessionLogLevel.Info && _written >= _limits.InfoBytes)
                {
                    _droppedLines++;
                    if (_infoDropped) return;
                    _infoDropped = true;
                    Append(SessionLogLevel.Warn, "log      this log has passed " + Hardware.Bytes.Human(_limits.InfoBytes)
                                                  + ", so from here on it keeps only warnings and errors; ordinary lines are "
                                                  + "left out (they are counted at the end)");
                    return;
                }

                Append(level, text);
            }
        }

        /// <summary>Formats and writes one entry, inside the lock; the total cap is applied here.</summary>
        private void Append(SessionLogLevel level, string text)
        {
            if (_writer == null) return;
            try
            {
                string entry = Format(level, text);
                long bytes = DurableWrite.Utf8NoBom.GetByteCount(entry);
                if (_written + bytes > _limits.TotalBytes)
                {
                    string stop = Format(SessionLogLevel.Error, "log      this log has reached its limit of "
                                                                  + Hardware.Bytes.Human(_limits.TotalBytes)
                                                                  + ", so nothing more is written to it in this session. "
                                                                  + "The session itself goes on.");
                    _writer.Write(stop);
                    Close();
                    return;
                }

                _writer.Write(entry);
                _written += bytes;
            }
            catch (Exception)
            {
                // A full disk or a vanished drive: stop logging rather than fail whatever was being logged.
                Close();
            }
        }

        private static string Format(SessionLogLevel level, string text)
        {
            string prefix = Timestamp(DateTimeOffset.Now) + "  " + LevelName(level) + "  ";
            string indent = new string(' ', prefix.Length);
            string[] lines = (text ?? "").Replace("\r\n", "\n").Split('\n');
            StringBuilder sb = new StringBuilder(prefix.Length + (text?.Length ?? 0) + 8);
            for (int i = 0; i < lines.Length; i++)
            {
                sb.Append(i == 0 ? prefix : indent).Append(lines[i]).Append('\n');
            }

            return sb.ToString();
        }

        /// <summary>"2026-09-24 14:03:12.345 +03:00": local time with its UTC offset, as every line starts.</summary>
        public static string Timestamp(DateTimeOffset t) =>
            t.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture);

        private static string LevelName(SessionLogLevel l) => l switch
        {
            SessionLogLevel.Warn => "WARN ",
            SessionLogLevel.Error => "ERROR",
            _ => "INFO ",
        };

        /// <summary>Closes the file. Safe to call twice; never throws.</summary>
        public void Dispose()
        {
            lock (_gate) Close();
        }

        private void Close()
        {
            try
            {
                _writer?.Dispose();
            }
            catch (Exception)
            {
            }

            _writer = null;
        }

        // =========================================================================================
        // Who holds a log, and the rename.

        /// <summary>
        /// The pid of the LIVE vseed process that holds <paramref name="path"/> open as its session log, or
        /// 0: the file is not held, or held by something else, or its writer has ended. Read from the LAST
        /// "written by process" line, and only believed when the file really is held and that process is
        /// alive and started when the line says - a pid on its own is reused.
        ///
        /// <para><b>The last one, not the first</b> (review of 2026-09-25). A session that could not replace
        /// vseed-prev.log appends to vseed.log, so the file can hold several sessions, oldest first; its first
        /// line then names a writer that ended long ago, and a live server writing below it was not recognised
        /// - a concurrent command waited out the rename's retries and logged the wrong cause.</para>
        /// </summary>
        public static int HolderOf(string path)
        {
            try
            {
                if (!Held(path)) return 0;
                Match? m = null;
                using (FileStream fs = SharedRead.Open(path))
                using (StreamReader r = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
                {
                    // A session log is capped (SessionLogLimits), so reading it through is bounded.
                    for (string? line = r.ReadLine(); line != null; line = r.ReadLine())
                    {
                        if (line.IndexOf("written by process", StringComparison.Ordinal) < 0) continue;
                        Match here = WriterLine.Match(line);
                        if (here.Success) m = here;
                    }
                }

                if (m == null) return 0;
                if (!int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int pid)) return 0;
                if (!DateTime.TryParseExact(m.Groups[2].Value, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTime started))
                {
                    return 0;
                }

                return ProcessLiveness.IsSameProcess(pid, started) ? pid : 0;
            }
            catch (Exception)
            {
                return 0;
            }
        }

        /// <summary>
        /// True when something has <paramref name="path"/> open: an exclusive open is refused. On Windows
        /// that is the sharing check itself; on Linux and macOS .NET takes an advisory lock on every file it
        /// opens, so a live vseed's log refuses it there too.
        /// </summary>
        private static bool Held(string path)
        {
            try
            {
                using FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
                return false;
            }
            catch (FileNotFoundException)
            {
                return false;
            }
            catch (DirectoryNotFoundException)
            {
                return false;
            }
            catch (Exception)
            {
                return true;
            }
        }

        /// <summary>Why a file could not be replaced, in words: read-only, or held.</summary>
        private static string WhyHeld(string path)
        {
            try
            {
                if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReadOnly) != 0) return "it is read-only";
            }
            catch (Exception)
            {
            }

            return Held(path) ? "another program has it open" : "it could not be changed";
        }

        private static long LengthOf(string path)
        {
            try
            {
                return new FileInfo(path).Length;
            }
            catch (Exception)
            {
                return long.MaxValue;
            }
        }

        /// <summary>vseed.log onto vseed-prev.log, replacing it, retried for about 0.8 s. True when it moved.</summary>
        private static bool TryRotate(string plain, string prev)
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    File.Move(plain, prev, overwrite: true);
                    return true;
                }
                catch (FileNotFoundException)
                {
                    return false;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    if (attempt >= RotateWaits.Length) return false;
                    Thread.Sleep(RotateWaits[attempt]);
                }
            }
        }

        /// <summary>
        /// Deletes the numbered logs other than <paramref name="own"/>. A live session holds its log, so
        /// its file is left alone - asked for explicitly, because on Linux and macOS a delete of an open
        /// file succeeds. The plain vseed.log is never deleted here: the next session renames it.
        /// </summary>
        private static List<string> DeleteStale(string dir, string fileName, int own)
        {
            List<string> deleted = new List<string>();
            for (int i = 1; i <= Fallbacks; i++)
            {
                if (i == own) continue;
                string p = NameAt(dir, fileName, i);
                try
                {
                    if (!File.Exists(p) || Held(p)) continue;
                    File.Delete(p);
                    deleted.Add(p);
                }
                catch (Exception)
                {
                    // In use by a live session, or not ours to delete.
                }
            }

            return deleted;
        }
    }
}
