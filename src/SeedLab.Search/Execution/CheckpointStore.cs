using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace SeedLab.Search.Execution
{
    /// <summary>
    /// When and where a run's checkpoint lives, and the housekeeping the audit found missing.
    ///
    /// <para>Measured defects this class exists to fix (disk audit, 2026-09-23):</para>
    /// <list type="number">
    /// <item>A checkpoint was written <b>unconditionally into the current directory</b> as
    /// <c>vseed-search.ckpt</c>, even for a ten-second run nobody would resume. Two orphans were
    /// already sitting in the repository.</item>
    /// <item>It was <b>never deleted</b>, so a finished run left one behind for the next run of a
    /// different query to trip over.</item>
    /// <item>A kill during a save left an orphaned <c>&lt;ckpt&gt;.tmp</c>.</item>
    /// </list>
    ///
    /// <para>So: the default path is under the user's cache root, keyed by the query hash, so two
    /// different queries never collide and nothing is written next to the user's work; orphaned temp
    /// files are cleaned up on the next run; and a <b>completed run deletes its own checkpoint and its
    /// kept-set snapshot</b>, which is what keeps the cache root from becoming the litter the CWD was.
    /// A short run is not refused a checkpoint - that would throw away exactly the work a kill takes -
    /// it simply finishes and retires it.</para>
    /// </summary>
    public sealed class CheckpointPolicy
    {
        /// <summary>The file to write, or null to disable checkpointing entirely.</summary>
        public string? Path;

        /// <summary>Seconds between checkpoints. The audit's complaint was that this was not honoured.</summary>
        public TimeSpan Interval = TimeSpan.FromSeconds(30);

        /// <summary>
        /// A grace period before the FIRST periodic checkpoint. Zero by default, deliberately: a
        /// checkpoint exists so that a kill costs at most one interval, and any grace period is that
        /// much work thrown away. Litter is prevented by <see cref="DeleteOnComplete"/> and by keeping
        /// the file in the cache root, not by refusing to write it.
        /// </summary>
        public TimeSpan MinRunTime = TimeSpan.Zero;

        /// <summary>Where a bounded run's kept set is snapshotted. Null when the run is unbounded.</summary>
        public string? SnapshotPath;

        /// <summary>Delete the checkpoint (and its snapshot) when the run finishes the whole plan.</summary>
        public bool DeleteOnComplete = true;
    }

    /// <summary>Finding, cleaning and retiring checkpoint files.</summary>
    public static class CheckpointStore
    {
        /// <summary>
        /// <c>%LOCALAPPDATA%\SeedLab\checkpoints</c> (or <c>~/.cache/SeedLab/checkpoints</c>). A
        /// checkpoint is tool state, not the user's data, so it does not belong beside their results.
        ///
        /// <para><b>Duplication to retire.</b> <c>SeedLab.Runtime.Storage.CacheRoot.Checkpoints</c>
        /// resolves the same directory, with more: an <c>SEEDLAB_CACHE_DIR</c> override, a
        /// <c>--cache-dir</c> override and macOS/XDG layouts. The two agree on Windows, which is why
        /// this one is safe to use today, but <c>SeedLab.Search</c> should take a project reference to
        /// <c>SeedLab.Runtime</c> (it has no dependencies of its own) and delete this property rather
        /// than keep two definitions of where the tool is allowed to write.</para>
        /// </summary>
        public static string Root
        {
            get
            {
                string b = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (string.IsNullOrEmpty(b))
                {
                    string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                    b = System.IO.Path.Combine(home.Length > 0 ? home : ".", ".cache");
                }

                return System.IO.Path.Combine(b, "SeedLab", "checkpoints");
            }
        }

        /// <summary>
        /// The default checkpoint for a query: <c>&lt;cache&gt;\&lt;first 16 of the query hash&gt;.ckpt</c>.
        /// Keyed by the query so two searches running side by side cannot overwrite each other's
        /// resume point, which a single <c>vseed-search.ckpt</c> in the CWD could.
        /// </summary>
        public static string DefaultPath(string queryHash)
        {
            string key = queryHash.Length >= 16 ? queryHash.Substring(0, 16) : queryHash;
            return System.IO.Path.Combine(Root, key + ".ckpt");
        }

        /// <summary>The kept-set snapshot that travels with a checkpoint.</summary>
        public static string SnapshotPathFor(string checkpointPath) => checkpointPath + ".top";

        /// <summary>
        /// Deletes <c>&lt;ckpt&gt;.tmp</c> orphans left by a kill during a save, here and in the cache
        /// root. Returns what it removed, so the run can say so rather than tidying up in secret.
        /// </summary>
        public static List<string> CleanOrphans(string? checkpointPath)
        {
            List<string> removed = new List<string>();
            List<string> dirs = new List<string>();
            if (checkpointPath != null)
            {
                string? d = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(checkpointPath));
                if (!string.IsNullOrEmpty(d)) dirs.Add(d);
            }

            if (Directory.Exists(Root) && !dirs.Contains(Root)) dirs.Add(Root);

            foreach (string dir in dirs)
            {
                if (!Directory.Exists(dir)) continue;
                string[] files;
                try
                {
                    files = Directory.GetFiles(dir, "*.ckpt.tmp");
                }
                catch (IOException)
                {
                    continue;
                }

                foreach (string f in files)
                {
                    // Only ours, and only if it is not being written right now: a .tmp younger than a
                    // minute may belong to another vseed that is mid-save.
                    try
                    {
                        if (DateTime.UtcNow - File.GetLastWriteTimeUtc(f) < TimeSpan.FromMinutes(1)) continue;
                        File.Delete(f);
                        removed.Add(f);
                    }
                    catch (IOException)
                    {
                    }
                    catch (UnauthorizedAccessException)
                    {
                    }
                }
            }

            return removed;
        }

        /// <summary>Deletes a finished run's checkpoint, its temp file and its kept-set snapshot.</summary>
        public static void Retire(string? checkpointPath)
        {
            if (checkpointPath == null) return;
            foreach (string p in new[] { checkpointPath, checkpointPath + ".tmp",
                                         SnapshotPathFor(checkpointPath), SnapshotPathFor(checkpointPath) + ".tmp" })
            {
                try
                {
                    if (File.Exists(p)) File.Delete(p);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }

        /// <summary>
        /// Repairs a results file that a kill left with a torn record: truncates it back to the byte
        /// count the checkpoint recorded as complete. Returns the bytes discarded.
        ///
        /// <para>Measured: a hard kill left 94 % of the file beyond <c>results_length</c>, and every
        /// byte of that is a record the resumed run produces again - so keeping them would duplicate
        /// them, and keeping the torn last line would produce a file that no JSONL reader can parse
        /// to the end.</para>
        /// </summary>
        public static long TruncateTorn(string resultsPath, long completeLength)
        {
            if (completeLength < 0 || !File.Exists(resultsPath)) return 0;
            FileInfo fi = new FileInfo(resultsPath);
            if (fi.Length <= completeLength) return 0;
            long discarded = fi.Length - completeLength;
            using (FileStream fs = new FileStream(resultsPath, FileMode.Open, FileAccess.Write, FileShare.None))
            {
                fs.SetLength(completeLength);
                fs.Flush(true);
            }

            return discarded;
        }

        /// <summary>Every checkpoint in the cache root, newest first, for a "what is resumable" report.</summary>
        public static List<string> List()
        {
            List<string> all = new List<string>();
            if (!Directory.Exists(Root)) return all;
            try
            {
                all.AddRange(Directory.GetFiles(Root, "*.ckpt"));
            }
            catch (IOException)
            {
                return all;
            }

            all.Sort(static (a, b) => File.GetLastWriteTimeUtc(b).CompareTo(File.GetLastWriteTimeUtc(a)));
            return all;
        }

        /// <summary>A human line for the report: "resume with: vseed search q.json --resume".</summary>
        public static string ResumeHint(string checkpointPath, string queryToken)
            => "vseed search " + queryToken + " --resume --checkpoint "
               + (checkpointPath.IndexOf(' ') >= 0 ? "\"" + checkpointPath + "\"" : checkpointPath);

        /// <summary>Bytes free on the volume the path lives on, or -1 when it cannot be read.</summary>
        public static long FreeBytes(string path)
        {
            try
            {
                string full = System.IO.Path.GetFullPath(path);
                string? root = System.IO.Path.GetPathRoot(full);
                if (string.IsNullOrEmpty(root)) return -1;
                return new DriveInfo(root).AvailableFreeSpace;
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
            {
                return -1;
            }
        }

        /// <summary>"1.7 MB", "7.36 TB" - one formatter, so every size in the tool reads the same.</summary>
        public static string Bytes(long b)
        {
            if (b < 0) return "unknown";
            double v = b;
            string[] u = { "B", "KB", "MB", "GB", "TB", "PB" };
            int i = 0;
            while (v >= 1024 && i < u.Length - 1)
            {
                v /= 1024;
                i++;
            }

            return v.ToString(v >= 100 || i == 0 ? "0" : "0.##", CultureInfo.InvariantCulture) + " " + u[i];
        }
    }
}
