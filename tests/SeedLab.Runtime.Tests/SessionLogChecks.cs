using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using SeedLab.Runtime;
using SeedLab.Runtime.Storage;

namespace SeedLab.RuntimeTests
{
    /// <summary>
    /// The follow-up to a run that died of "Access to the path is denied." (2026-09-24): the session
    /// log, the access checks and their ledger, and the retry that turns a busy file into a wait and a
    /// lasting one into a sentence naming the file.
    ///
    /// <para>Every file here is under a temp folder; the user's cache root is never opened. The
    /// "other program" holding a file is a FileStream in this process: a rename, a delete and an open
    /// collide with a handle whichever process owns it.</para>
    /// </summary>
    public static class SessionLogChecks
    {
        public static void Run(Action<bool, string, string> check)
        {
            string tempRoot = Path.Combine(Path.GetTempPath(),
                "seedlab-runtime-log-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                Purge(check, Path.Combine(tempRoot, "purge"));
                Fallbacks(check, Path.Combine(tempRoot, "fallback"));
                NeverThrows(check, Path.Combine(tempRoot, "never"));
                Format(check, Path.Combine(tempRoot, "format"));
                Context(check, Path.Combine(tempRoot, "context"));
                Access(check, Path.Combine(tempRoot, "access"));
                Retry(check, Path.Combine(tempRoot, "retry"));
            }
            finally
            {
                SessionLog.Current = null;
                ClearReadOnly(tempRoot);
                try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true); } catch (Exception) { }
            }
        }

        // =========================================================================================
        // A new session empties the log.

        private static void Purge(Action<bool, string, string> check, string dir)
        {
            using (SessionLog a = SessionLog.Open(dir))
            {
                a.Info("first session's marker");
            }

            string path = Path.Combine(dir, SessionLog.FileName);
            string first = SharedRead.AllText(path);

            using SessionLog b = SessionLog.Open(dir);
            b.Info("second session's line");
            string second = SharedRead.AllText(path);   // read while b is still open: every line is flushed

            check(first.Contains("first session's marker", StringComparison.Ordinal)
                  && !second.Contains("first session's marker", StringComparison.Ordinal)
                  && second.Contains("second session's line", StringComparison.Ordinal),
                  "a new session empties vseed.log, the way BepInEx empties LogOutput.log",
                  "the first session's line is gone, the second's is there");
            check(b.Index == 0 && b.Problem == null && b.Path == path,
                  "and writes the plain name when nothing else holds it", b.Path ?? "(no path)");
            check(second.Contains("second session's line", StringComparison.Ordinal),
                  "every line reaches the file as it is written, readable by a reader that shares read and write",
                  "read back while the log was still open");
        }

        // =========================================================================================
        // Overlapping sessions, stale numbered logs, and all five names taken.

        private static void Fallbacks(Action<bool, string, string> check, string dir)
        {
            string plain = Path.Combine(dir, SessionLog.FileName);
            using (SessionLog a = SessionLog.Open(dir))
            using (SessionLog b = SessionLog.Open(dir))
            {
                a.Info("from the first session");
                b.Info("from the second session");
                check(b.Index == 1 && b.Path == plain + ".1"
                      && (b.Problem ?? "").Contains("vseed.log is in use by another session", StringComparison.Ordinal),
                      "a second live session cannot empty the first one's log, and writes vseed.log.1",
                      b.Problem ?? "(no problem said)");
                check(!SharedRead.AllText(plain).Contains("from the second session", StringComparison.Ordinal)
                      && SharedRead.AllText(plain + ".1").Contains("from the second session", StringComparison.Ordinal),
                      "and each session's lines go to its own file", "");
            }

            // Stale numbered logs: .1 from the overlap above, .2 and .3 left by earlier ones, .4 held by a
            // live session.
            File.WriteAllText(plain + ".2", "stale");
            File.WriteAllText(plain + ".3", "stale");
            List<string> deleted;
            bool fourKept;
            using (FileStream live = new FileStream(plain + ".4", FileMode.Create, FileAccess.Write, FileShare.Read))
            using (SessionLog c = SessionLog.Open(dir))
            {
                deleted = new List<string>(c.DeletedStale);
                fourKept = File.Exists(plain + ".4");
                check(c.Index == 0, "the next session takes vseed.log again", c.Path ?? "");
            }

            check(deleted.Count == 3 && !File.Exists(plain + ".1") && !File.Exists(plain + ".2") && !File.Exists(plain + ".3"),
                  "it deletes the numbered logs no session is using (BepInEx never does)",
                  deleted.Count + " deleted");
            check(fourKept, "and leaves alone the one a live session still holds", "vseed.log.4 kept");

            // All five names held the way a live session holds its log.
            List<FileStream> holders = new List<FileStream>();
            try
            {
                for (int i = 0; i <= SessionLog.Fallbacks; i++)
                {
                    string p = SessionLog.NameAt(dir, SessionLog.FileName, i);
                    FileStream h = new FileStream(p, FileMode.Create, FileAccess.Write, FileShare.Read);
                    byte[] mark = System.Text.Encoding.UTF8.GetBytes("held " + i + "\n");
                    h.Write(mark, 0, mark.Length);
                    h.Flush();
                    holders.Add(h);
                }

                bool threw = false;
                SessionLog? d = null;
                try
                {
                    d = SessionLog.Open(dir);
                    d.Info("nowhere");
                    d.Warn("nowhere");
                    d.Dispose();
                }
                catch (Exception)
                {
                    threw = true;
                }

                check(!threw && d != null && !d.IsOpen && d.Path == null && d.Index == -1
                      && (d.Problem ?? "").Contains("keeps no log", StringComparison.Ordinal),
                      "with all five names in use the session runs with no log, says why, and never throws",
                      d?.Problem ?? "(threw)");
                bool intact = true;
                for (int i = 0; i <= SessionLog.Fallbacks; i++)
                {
                    intact &= SharedRead.AllText(SessionLog.NameAt(dir, SessionLog.FileName, i)) == "held " + i + "\n";
                }

                check(intact, "and no live session's log was emptied or deleted", "");
            }
            finally
            {
                foreach (FileStream h in holders) h.Dispose();
            }
        }

        // =========================================================================================
        // A log must never be why a run fails.

        private static void NeverThrows(Action<bool, string, string> check, string dir)
        {
            Directory.CreateDirectory(dir);

            // A folder that cannot be made: its parent is a file.
            string blocker = Path.Combine(dir, "a-file");
            File.WriteAllText(blocker, "in the way");
            bool threw = false;
            SessionLog? log = null;
            try
            {
                log = SessionLog.Open(Path.Combine(blocker, "logs"));
                log.Info("nowhere");
                log.Dispose();
            }
            catch (Exception)
            {
                threw = true;
            }

            check(!threw && log != null && !log.IsOpen && (log.Problem ?? "").Contains("could not be created", StringComparison.Ordinal),
                  "a log folder that cannot be created gives no log and no exception", log?.Problem ?? "(threw)");

            // A read-only vseed.log: BepInEx catches only the sharing failure, and this one escapes it.
            string ro = Path.Combine(dir, "readonly");
            Directory.CreateDirectory(ro);
            string plain = Path.Combine(ro, SessionLog.FileName);
            File.WriteAllText(plain, "old");
            File.SetAttributes(plain, FileAttributes.ReadOnly);
            threw = false;
            SessionLog? fallback = null;
            try
            {
                fallback = SessionLog.Open(ro);
                fallback.Info("written anyway");
            }
            catch (Exception)
            {
                threw = true;
            }

            check(!threw && fallback != null && fallback.Index == 1
                  && (fallback.Problem ?? "").Contains("read-only", StringComparison.Ordinal),
                  "a read-only vseed.log moves the session on to vseed.log.1 instead of throwing",
                  fallback?.Problem ?? "(threw)");
            fallback?.Dispose();

            threw = false;
            try
            {
                fallback?.Info("after dispose");
                fallback?.Dispose();
                fallback?.Exception("after dispose", new InvalidOperationException("x"));
            }
            catch (Exception)
            {
                threw = true;
            }

            check(!threw, "writing to a closed log, or disposing it twice, does nothing and throws nothing", "");
        }

        // =========================================================================================
        // The line format.

        private static void Format(Action<bool, string, string> check, string dir)
        {
            using (SessionLog log = SessionLog.Open(dir))
            {
                log.Info("an ordinary line");
                log.Warn("a warning");
                log.Error("an error\nwith a second line");
                log.Exception("while testing", new InvalidOperationException("boom"));
            }

            string path = Path.Combine(dir, SessionLog.FileName);
            byte[] raw = File.ReadAllBytes(path);
            string[] lines = SharedRead.AllLines(path);
            Regex head = new Regex(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} [+-]\d{2}:\d{2}  (INFO |WARN |ERROR)  \S");
            int prefix = "2026-09-24 14:03:12.345 +03:00  INFO   ".Length;
            bool allFormatted = lines.Length > 0;
            foreach (string l in lines)
            {
                bool ok = head.IsMatch(l) || (l.Length > prefix && l.Substring(0, prefix).Trim().Length == 0);
                allFormatted &= ok;
            }

            check(allFormatted, "every line is 'yyyy-MM-dd HH:mm:ss.fff zzz  LEVEL  text' or indented under one",
                  lines.Length + " lines");

            string offset = lines[0].Substring(24, 6);
            TimeSpan local = TimeZoneInfo.Local.GetUtcOffset(DateTime.Now);
            string expected = (local < TimeSpan.Zero ? "-" : "+") + local.ToString(@"hh\:mm");
            check(offset == expected, "the time is local, with its UTC offset", offset + " (this machine: " + expected + ")");

            check(lines[0].Contains("emptied at the start of every session", StringComparison.Ordinal)
                  && lines[0].Contains("vseed.log.1", StringComparison.Ordinal),
                  "the first line says it is rewritten every session and names the fallback rule",
                  lines[0].Length > 120 ? lines[0].Substring(0, 120) + "..." : lines[0]);
            string all = string.Join("\n", lines);
            check(all.Contains("  WARN   a warning", StringComparison.Ordinal)
                  && all.Contains("  ERROR  an error", StringComparison.Ordinal)
                  && all.Contains("\n" + new string(' ', prefix) + "with a second line", StringComparison.Ordinal),
                  "levels are INFO, WARN and ERROR, and a second line is indented to the text", "");
            check(all.Contains("System.InvalidOperationException 0x80131509: boom", StringComparison.Ordinal),
                  "an exception keeps its type and HResult - in the log, which is where they belong", "");
            check(raw.Length >= 3 && !(raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF),
                  "UTF-8 with no BOM", "");
        }

        // =========================================================================================
        // RuntimeContext.Start opens it first and writes the session's header.

        private static void Context(Action<bool, string, string> check, string root)
        {
            string text;
            string logPath;
            IReadOnlyList<string> startup;
            int checks;
            bool allOk;
            bool current;
            using (RuntimeContext ctx = RuntimeContext.Start(new RuntimeOptions
                   {
                       CacheDirectory = root,
                       AutoThrottle = false,
                       CommandLine = "vseed search q.json --dry-run",
                       Program = "vseed 9.9.9-test",
                   }))
            {
                logPath = ctx.SessionLog.Path ?? "";
                startup = ctx.StartupLines();
                checks = ctx.AccessChecks.Count;
                allOk = true;
                foreach (AccessResult r in ctx.AccessChecks) allOk &= r.Ok;
                current = ReferenceEquals(SessionLog.Current, ctx.SessionLog);
                ctx.ExitCode = 0;
            }

            text = SharedRead.AllText(logPath);
            string expectedLog = Path.Combine(Path.GetFullPath(root), "logs", SessionLog.FileName);
            check(logPath == expectedLog && current,
                  "RuntimeContext.Start opens <cache root>\\logs\\vseed.log and makes it the session's current log", logPath);
            bool listed = false;
            foreach (string l in startup) listed |= l == "log         " + expectedLog;
            check(listed, "the startup lines name the log", "log         " + expectedLog);

            string[] expected =
            {
                "session  started ", "program  vseed 9.9.9-test", "command  vseed search q.json --dry-run",
                "process  pid " + Environment.ProcessId + ";", "cache    " + Path.GetFullPath(root) + " (--cache-dir)",
                "access   ok    folder  " + Path.Combine(Path.GetFullPath(root), "logs"),
                "access   file access checked: 8 paths OK", "reap     ", "selftest ", "session  ended after ",
                " with exit code 0",
            };
            List<string> missing = new List<string>();
            foreach (string e in expected)
            {
                if (!text.Contains(e, StringComparison.Ordinal)) missing.Add(e.Trim());
            }

            check(missing.Count == 0,
                  "the header: start time, program, command line, pid and platform, the cache root, every folder's access check, the reap, the self-test, and the end with its exit code",
                  missing.Count == 0 ? "all present" : "MISSING: " + string.Join(" | ", missing));
            check(checks == 8 && allOk, "the cache root and all seven folders - logs among them - pass their access check",
                  checks + " checked");
            check(SessionLog.Current == null, "disposing the context puts the previous current log back (none here)", "");
        }

        // =========================================================================================
        // The access checks and their ledger.

        private static void Access(Action<bool, string, string> check, string dir)
        {
            string folder = Path.Combine(dir, "new-folder");
            AccessResult made = AccessCheck.Directory(folder);
            check(made.Ok && Directory.Exists(folder) && Directory.GetFileSystemEntries(folder).Length == 0,
                  "a folder check creates the folder and leaves nothing in it", made.Line());

            string kept = Path.Combine(folder, "results.csv");
            File.WriteAllText(kept, "keep me");
            AccessResult write = AccessCheck.FileForWrite(kept);
            check(write.Ok && File.ReadAllText(kept) == "keep me",
                  "a write check of an existing file passes and changes nothing in it", write.Line());

            string missingFile = Path.Combine(folder, "not-yet", "out.jsonl");
            AccessResult later = AccessCheck.FileForWrite(missingFile);
            check(later.Ok && !File.Exists(missingFile) && !Directory.Exists(Path.GetDirectoryName(missingFile)),
                  "a file that does not exist yet is checked through the nearest folder, and nothing is created",
                  later.Line());

            string held = Path.Combine(folder, "open-in-a-spreadsheet.csv");
            File.WriteAllText(held, "a,b\n");
            AccessResult busy;
            using (new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                busy = AccessCheck.FileForWrite(held);
            }

            check(busy.Problem == FileProblem.InUse && busy.Message.Contains(held, StringComparison.Ordinal)
                  && busy.Message.Contains("another program has it open", StringComparison.Ordinal),
                  "a file another program holds without sharing writes fails as in use, named",
                  busy.Message);
            check(!busy.Message.Contains("0x8007", StringComparison.Ordinal) && !busy.Message.Contains("Exception", StringComparison.Ordinal),
                  "and the sentence carries no HResult and no exception name", "");

            string ro = Path.Combine(folder, "read-only.jsonl");
            File.WriteAllText(ro, "x");
            File.SetAttributes(ro, FileAttributes.ReadOnly);
            AccessResult roResult = AccessCheck.FileForWrite(ro);
            File.SetAttributes(ro, FileAttributes.Normal);
            check(roResult.Problem == FileProblem.ReadOnly && roResult.Message.Contains("read-only", StringComparison.Ordinal),
                  "a read-only file fails as read-only", roResult.Message);

            AccessResult absent = AccessCheck.FileForRead(Path.Combine(folder, "absent.ckpt"));
            AccessResult locked;
            using (new FileStream(kept, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                locked = AccessCheck.FileForRead(kept);
            }

            check(absent.Problem == FileProblem.Missing && locked.Problem == FileProblem.InUse,
                  "a read check tells a missing file from one held exclusively", absent.Problem + ", " + locked.Problem);

            // What the ledger says about a path, and whether it is the file itself or only its folder: a
            // later failure may claim "something changed" only from a check that looked at what failed.
            StartCheck? own = AccessCheck.StartCheckFor(kept);
            StartCheck? inside = AccessCheck.StartCheckFor(Path.Combine(folder, "never-checked.txt"));
            StartCheck? notYet = AccessCheck.StartCheckFor(missingFile);
            StartCheck? never = AccessCheck.StartCheckFor(Path.Combine(dir, "elsewhere", "x.txt"));
            check(own != null && own.FileItself && own.When == write.When && !own.FailsNow
                  && inside != null && !inside.FileItself && inside.When == made.When && !inside.FailsNow
                  && notYet == null && never == null,
                  "the ledger answers 'was this allowed at the start?': the file itself when it was opened, only its folder for a "
                  + "file in a checked folder, and nothing for a file its check found missing or one nobody checked",
                  "own " + (own == null ? "no" : own.FileItself ? "the file" : "FOLDER") + ", in folder "
                  + (inside == null ? "no" : inside.FileItself ? "THE FILE" : "the folder") + ", checked while missing "
                  + (notYet == null ? "no" : "YES") + ", elsewhere " + (never == null ? "no" : "YES"));

            string summary = AccessCheck.Summary(new[] { made, write, later, busy, roResult });
            check(summary.StartsWith("file access checked: 3 of 5 paths OK - ", StringComparison.Ordinal)
                  && summary.Contains(held + ": in use by another program", StringComparison.Ordinal)
                  && summary.Contains(ro + ": marked read-only", StringComparison.Ordinal),
                  "the summary line counts the passes and names every failure", summary);

            // A folder check that must leave nothing behind (a --dry-run, a results folder the web server
            // creates only when a run names a file): the folder is not created, the one above is probed.
            string notMade = Path.Combine(folder, "results-later", "deeper");
            AccessResult noCreate = AccessCheck.Directory(notMade, create: false);
            check(noCreate.Ok && !Directory.Exists(Path.Combine(folder, "results-later"))
                  && noCreate.Note.Contains(folder, StringComparison.Ordinal),
                  "a folder check told not to create checks the nearest folder above instead, and creates nothing",
                  noCreate.Line());
            AccessResult existing = AccessCheck.Directory(folder, create: false);
            check(existing.Ok && existing.Note.Length == 0, "and an existing folder is probed as it is", existing.Line());
        }

        // =========================================================================================
        // The retry: a brief holder is waited out, a lasting one is named.

        private static void Retry(Action<bool, string, string> check, string dir)
        {
            Directory.CreateDirectory(dir);
            AccessCheck.Directory(dir);   // so a later failure can say the folder was fine at the start

            check(FileRetry.IsTransient(new UnauthorizedAccessException())
                  && FileRetry.IsTransient(new IOException("sharing", FileRetry.SharingViolation))
                  && FileRetry.IsTransient(new IOException("lock", FileRetry.LockViolation))
                  && !FileRetry.IsTransient(new IOException("disk full", unchecked((int)0x80070070)))
                  && !FileRetry.IsTransient(new FileNotFoundException("gone"))
                  && !FileRetry.IsTransient(new FileAccessException(FileRetry.Diagnose(Path.Combine(dir, "x")))),
                  "transient: an access denial, a sharing and a lock violation; not a full disk, a missing file or a diagnosed failure",
                  "");

            using SessionLog log = SessionLog.Open(Path.Combine(dir, "logs"));
            SessionLog.Current = log;
            try
            {
                // A reader that lets go after about 120 ms, against the quick schedule (about 1.6 s).
                string target = Path.Combine(dir, "run.ckpt");
                string temp = target + ".tmp";
                File.WriteAllText(target, "old");
                File.WriteAllText(temp, "new");
                List<RetryAttempt> attempts = new List<RetryAttempt>();
                FileStream holder = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read);
                using (Timer release = new Timer(_ => holder.Dispose(), null, 120, Timeout.Infinite))
                {
                    DurableWrite.Replace(temp, target, RetrySchedule.Quick, attempts.Add);
                }

                holder.Dispose();
                int failed = attempts.FindAll(a => !a.Succeeded).Count;
                bool succeeded = attempts.Count > 0 && attempts[attempts.Count - 1].Succeeded;
                check(File.ReadAllText(target) == "new" && !File.Exists(temp) && failed >= 1 && succeeded,
                      "a rename blocked by a reader for 120 ms is retried and succeeds, the temp file becoming the file",
                      failed + " failed attempt(s), then success after "
                      + (attempts.Count > 0 ? (long)attempts[attempts.Count - 1].Elapsed.TotalMilliseconds : -1) + " ms");

                // A reader that shares everything still blocks the rename; only an exclusive probe sees it.
                string polite = Path.Combine(dir, "polite.ckpt");
                File.WriteAllText(polite, "old");
                File.WriteAllText(polite + ".tmp", "new");
                FileAccessException? inUse = null;
                using (new FileStream(polite, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    try
                    {
                        DurableWrite.Replace(polite + ".tmp", polite, new RetrySchedule("test", 5, 5));
                    }
                    catch (FileAccessException ex)
                    {
                        inUse = ex;
                    }
                }

                check(inUse != null && inUse.Problem == FileProblem.InUse && inUse.Path == Path.GetFullPath(polite)
                      && inUse.Message.Contains(Path.GetFullPath(polite), StringComparison.Ordinal)
                      && inUse.Message.Contains("another program has it open", StringComparison.Ordinal)
                      && File.ReadAllText(polite) == "old",
                      "a holder that shares read, write AND delete still blocks the rename - and is diagnosed as in use, by name",
                      inUse?.Message ?? "(no exception)");

                // A read-only file: the same bare exception, a different cause.
                string ro = Path.Combine(dir, "read-only.ckpt");
                File.WriteAllText(ro, "old");
                File.WriteAllText(ro + ".tmp", "new");
                File.SetAttributes(ro, FileAttributes.ReadOnly);
                FileAccessException? readOnly = null;
                try
                {
                    DurableWrite.Replace(ro + ".tmp", ro, new RetrySchedule("test", 5, 5));
                }
                catch (FileAccessException ex)
                {
                    readOnly = ex;
                }

                File.SetAttributes(ro, FileAttributes.Normal);
                string m = readOnly?.Message ?? "";
                check(readOnly != null && readOnly.Problem == FileProblem.ReadOnly
                      && m.Contains(Path.GetFullPath(ro), StringComparison.Ordinal) && m.Contains("read-only", StringComparison.Ordinal)
                      && File.ReadAllText(ro) == "old" && File.Exists(ro + ".tmp"),
                      "a read-only destination gives up with a diagnosis naming the file; the old file and the temp are untouched",
                      m);
                check(m.Length > 0 && !m.Contains("0x8007", StringComparison.Ordinal) && !m.Contains("Exception", StringComparison.Ordinal)
                      && !m.Contains("Access to the path", StringComparison.Ordinal),
                      "the sentence has no HResult, no exception name and not the bare system message", "");
                check(!m.Contains("access check", StringComparison.Ordinal),
                      "only its FOLDER was checked at the start, and that still passes, so it claims nothing about the start "
                      + "(it used to say 'so something changed after that' here)", "");

                // The file itself passed its write check, and is made read-only after: the same check fails
                // now, so the change is real and said.
                string changed = Path.Combine(dir, "changed.ckpt");
                File.WriteAllText(changed, "old");
                File.WriteAllText(changed + ".tmp", "new");
                AccessResult changedAtStart = AccessCheck.FileForWrite(changed);
                File.SetAttributes(changed, FileAttributes.ReadOnly);
                string changedMessage = "";
                try
                {
                    DurableWrite.Replace(changed + ".tmp", changed, new RetrySchedule("test", 5));
                }
                catch (FileAccessException ex)
                {
                    changedMessage = ex.Message;
                }

                File.SetAttributes(changed, FileAttributes.Normal);
                check(changedAtStart.Ok && changedAtStart.Examined
                      && changedMessage.Contains("It passed SeedLab's access check at", StringComparison.Ordinal)
                      && changedMessage.Contains("so something changed after that", StringComparison.Ordinal),
                      "a file that passed its own check and is read-only now: 'It passed ... so something changed after that'",
                      changedMessage);

                // The file itself passed, then a holder that lets others write opens it: the check would
                // pass again (it cannot see such a holder), so the note says that instead of a change.
                string blind = Path.Combine(dir, "blind.ckpt");
                File.WriteAllText(blind, "old");
                File.WriteAllText(blind + ".tmp", "new");
                AccessCheck.FileForWrite(blind);
                string blindMessage = "";
                using (new FileStream(blind, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    try
                    {
                        DurableWrite.Replace(blind + ".tmp", blind, new RetrySchedule("test", 5));
                    }
                    catch (FileAccessException ex)
                    {
                        blindMessage = ex.Message;
                    }
                }

                check(blindMessage.Contains("passes that check and still stops SeedLab replacing it", StringComparison.Ordinal)
                      && !blindMessage.Contains("so something changed after that", StringComparison.Ordinal),
                      "a holder that lets others write, which the check cannot see: said so, and no change is claimed",
                      blindMessage);

                // The file itself passed, then a holder that lets nobody write opens it: the check fails
                // now, so something did change.
                string denied = Path.Combine(dir, "denied.ckpt");
                File.WriteAllText(denied, "old");
                File.WriteAllText(denied + ".tmp", "new");
                AccessCheck.FileForWrite(denied);
                string deniedMessage = "";
                using (new FileStream(denied, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    try
                    {
                        DurableWrite.Replace(denied + ".tmp", denied, new RetrySchedule("test", 5));
                    }
                    catch (FileAccessException ex)
                    {
                        deniedMessage = ex.Message;
                    }
                }

                check(deniedMessage.Contains("another program has it open", StringComparison.Ordinal)
                      && deniedMessage.Contains("so something changed after that", StringComparison.Ordinal),
                      "a holder that lets nobody write, opened after the check passed: in use, and 'so something changed after that'",
                      deniedMessage);

                // A full drive: said as one, of the file its message names, never retried and never a probe.
                string full = Path.Combine(dir, "results.jsonl");
                IOException diskFull = new IOException("There is not enough space on the disk. : '" + full + "'",
                                                       FileRetry.DiskFullError);
                FileDiagnosis? fd = FileRetry.DiagnoseEscaped(diskFull);
                FileDiagnosis? fdNoPath = FileRetry.DiagnoseEscaped(new IOException("There is not enough space on the disk.",
                                                                                    FileRetry.HandleDiskFull));
                check(FileRetry.IsDiskFull(diskFull) && !FileRetry.IsTransient(diskFull)
                      && fd != null && fd.Problem == FileProblem.DiskFull && fd.Message.Contains(full, StringComparison.Ordinal)
                      && fd.Message.Contains("drive it is on is full", StringComparison.Ordinal)
                      && FileProblems.Name(fd.Problem) == "disk_full" && fdNoPath == null,
                      "a full drive is diagnosed as one, naming the file its message names - not retried, not 'this is a bug'",
                      fd?.Message ?? "(no diagnosis)");

                // DurableWrite.Stream onto a held file: the old file stays whole and no temp is left.
                string map = Path.Combine(dir, "map.png");
                File.WriteAllText(map, "old picture");
                FileAccessException? viewer = null;
                using (new FileStream(map, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    try
                    {
                        DurableWrite.Text(map, "new picture", new RetrySchedule("test", 5));
                    }
                    catch (FileAccessException ex)
                    {
                        viewer = ex;
                    }
                }

                check(viewer != null && viewer.Problem == FileProblem.InUse && File.ReadAllText(map) == "old picture"
                      && Directory.GetFiles(dir, DurableWrite.TempPrefix + "*").Length == 0,
                      "a durable write over a file a viewer holds leaves the old file whole and no temp file behind",
                      viewer?.Problem.ToString() ?? "(no exception)");

                string logged = SharedRead.AllText(log.Path!);
                check(logged.Contains("retry    save " + Path.GetFullPath(target) + ": attempt 1 of 8 failed (UnauthorizedAccessException 0x80070005",
                                      StringComparison.Ordinal)
                      && logged.Contains("succeeded on attempt", StringComparison.Ordinal)
                      && logged.Contains("gave up after 3 attempts", StringComparison.Ordinal),
                      "each episode is in the session log: the first failure with its exception and HResult, then the outcome",
                      "");

                // A failure that reached a host without going through the retry: a raw sharing violation
                // names its file in its message, so it can still be diagnosed in words; the rename's
                // path-less access denial cannot, and says so with null rather than guess.
                string raw = Path.Combine(dir, "raw-held.jsonl");
                File.WriteAllText(raw, "x");
                FileDiagnosis? escaped = null;
                using (new FileStream(raw, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    try
                    {
                        using (new FileStream(raw, FileMode.Open, FileAccess.Write, FileShare.Read))
                        {
                        }
                    }
                    catch (IOException ex)
                    {
                        escaped = FileRetry.DiagnoseEscaped(ex, "open");
                    }
                }

                FileDiagnosis? pathless = FileRetry.DiagnoseEscaped(new UnauthorizedAccessException("Access to the path is denied."));
                FileDiagnosis? other = FileRetry.DiagnoseEscaped(new InvalidOperationException("not a file problem"));
                check(escaped != null && escaped.Problem == FileProblem.InUse && escaped.Message.Contains(raw, StringComparison.Ordinal)
                      && pathless == null && other == null,
                      "an escaped sharing violation is diagnosed from the file its message names; a path-less denial and a non-file error give none",
                      escaped?.Message ?? "(no diagnosis)");
            }
            finally
            {
                SessionLog.Current = null;
            }
        }

        private static void ClearReadOnly(string dir)
        {
            try
            {
                if (!Directory.Exists(dir)) return;
                foreach (string f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                {
                    try { File.SetAttributes(f, FileAttributes.Normal); } catch (Exception) { }
                }
            }
            catch (Exception)
            {
            }
        }
    }
}
