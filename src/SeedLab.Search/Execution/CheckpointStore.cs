using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using SeedLab.Runtime.Storage;
using SeedLab.Search.Criteria;

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

    /// <summary>
    /// What became of a funnel stage-two checkpoint an earlier build left at
    /// <see cref="CheckpointStore.LegacyRoot"/> (<see cref="CheckpointStore.AdoptLegacyStageTwo"/>).
    /// </summary>
    public sealed class LegacyCheckpoint
    {
        public string LegacyPath = "";
        public string NewPath = "";

        /// <summary>True when the file was this run's stage two and now lives at <see cref="NewPath"/>.</summary>
        public bool Adopted;

        /// <summary>Why the file was left where it is, when it was not adopted.</summary>
        public string? Reason;

        /// <summary>
        /// Files of the legacy pair that could not be deleted after the move (another program holding
        /// them open, say). The copy at the new path is complete either way, and nothing reads these
        /// again, because the new path is now taken.
        /// </summary>
        public List<string> LeftBehind = new List<string>();
    }

    /// <summary>Finding, cleaning and retiring checkpoint files.</summary>
    public static class CheckpointStore
    {
        /// <summary>
        /// The checkpoints directory of the cache root this process would use with no
        /// <c>--cache-dir</c>: <c>SEEDLAB_CACHE_DIR</c>, else the per-OS location - resolved by
        /// <see cref="CacheRoot"/> itself, so there is one definition of where the tool may write. A
        /// checkpoint is tool state, not the user's data, so it does not belong beside their results.
        ///
        /// <para><b>Only a fallback now</b> (2026-09-24). This was a second, hard-coded definition,
        /// <c>%LOCALAPPDATA%\SeedLab\checkpoints</c>, that ignored <c>SEEDLAB_CACHE_DIR</c> and
        /// <c>--cache-dir</c> - and a funnel's stage two took its path from it on both front ends, so
        /// it checkpointed there whatever cache root the run had been given. <c>--cache-dir</c> is a
        /// per-process option no static can see, so both front ends hand
        /// <see cref="SearchSession.Create"/> their own cache root's directory, and this is what a
        /// library caller that names none gets. The old location is kept, for one purpose only, as
        /// <see cref="LegacyRoot"/>.</para>
        /// </summary>
        public static string Root => CacheRoot.Open(new CacheRootOptions { Create = false }).Checkpoints;

        /// <summary>
        /// Where <see cref="Root"/> pointed before 2026-09-24, resolved the way it was then:
        /// <c>%LOCALAPPDATA%\SeedLab\checkpoints</c> (or <c>~/.cache/SeedLab/checkpoints</c> when
        /// there is no local application data folder), with NO <c>SEEDLAB_CACHE_DIR</c> and NO
        /// <c>--cache-dir</c>. Nothing writes through this property. It exists so that a funnel stage two
        /// an earlier build checkpointed here can be found and moved to the run's own path
        /// (<see cref="AdoptLegacyStageTwo"/>) instead of being silently started again. On Windows
        /// with neither override set it is the same directory as <see cref="Root"/>; on Linux it never
        /// was (<c>~/.local/share</c> against the cache root's <c>~/.cache</c>).
        /// </summary>
        public static string LegacyRoot
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
        /// The checkpoint for a query in a checkpoints directory:
        /// <c>&lt;directory&gt;\&lt;first 16 of the query hash&gt;.ckpt</c>. Keyed by the query so two
        /// searches running side by side cannot overwrite each other's resume point, which a single
        /// <c>vseed-search.ckpt</c> in the CWD could.
        /// </summary>
        public static string PathIn(string directory, string queryHash)
        {
            string key = queryHash.Length >= 16 ? queryHash.Substring(0, 16) : queryHash;
            return System.IO.Path.Combine(directory, key + ".ckpt");
        }

        /// <summary>The checkpoint an earlier build would have used for a query, in <see cref="LegacyRoot"/>.</summary>
        public static string LegacyPath(string queryHash) => PathIn(LegacyRoot, queryHash);

        /// <summary>The kept-set snapshot that travels with a checkpoint.</summary>
        public static string SnapshotPathFor(string checkpointPath) => checkpointPath + ".top";

        /// <summary>
        /// Deletes <c>&lt;ckpt&gt;.tmp</c> orphans left by a kill during a save, beside the checkpoint
        /// and in the run's checkpoints directory (<see cref="Root"/> when none is given). Returns what
        /// it removed, so the run can say so rather than tidying up in secret.
        /// </summary>
        public static List<string> CleanOrphans(string? checkpointPath, string? checkpointsDirectory = null)
        {
            List<string> removed = new List<string>();
            List<string> dirs = new List<string>();
            if (checkpointPath != null)
            {
                string? d = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(checkpointPath));
                if (!string.IsNullOrEmpty(d)) dirs.Add(d);
            }

            // The run's own cache root, not the default one: a run given --cache-dir has nothing to
            // tidy in a cache root it was told not to use.
            string root = System.IO.Path.GetFullPath(checkpointsDirectory ?? Root);
            bool listed = false;
            foreach (string d in dirs) listed |= SamePath(d, root);
            if (Directory.Exists(root) && !listed) dirs.Add(root);

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
        /// Moves a funnel stage-two checkpoint that an earlier build left at <paramref name="legacyPath"/>
        /// (<see cref="LegacyPath"/>) to <paramref name="newPath"/>, the path this run's stage two
        /// checkpoints at - but only a file that IS this stage two's. Returns null when there is
        /// nothing to decide: the two paths are one file, the new path already has a checkpoint (which
        /// is then the one to resume), or there is no file at the legacy path.
        ///
        /// <para><b>Why a move is needed at all</b> (2026-09-24). Until that day stage two checkpointed
        /// at the hard-coded <c>%LOCALAPPDATA%\SeedLab\checkpoints</c> on both front ends, whatever
        /// <c>--cache-dir</c>, <c>SEEDLAB_CACHE_DIR</c> or <c>--checkpoint</c> said, while its survivor
        /// list honoured them. Moving stage two onto the run's own path would otherwise strand every
        /// interrupted stage two a user with one of those overrides already has: its <c>--resume</c>
        /// would find nothing at the new path and place every survivor again.</para>
        ///
        /// <para><b>What "this stage two's" means</b> is exactly what a file at the new path must pass
        /// before <see cref="SearchSession.Start"/> resumes it: it loads, and
        /// <see cref="Checkpoint.MustMatch"/> accepts it against this survivor list at its own block
        /// size - the query hash, the definitions, the grid, the sequential order, the key (a hash of
        /// the survivor list), the range and the count, and a block size of at least one. The block
        /// size is its own because that is the size a resume adopts; a different size asked for is
        /// then refused at the gate, naming the new path, exactly as for a file that was always there.
        /// Anything else - a sample run's checkpoint of the same query, another survivor list, a file
        /// that does not parse - is left exactly as it is, and the reason is returned to be said.</para>
        ///
        /// <para><b>Order, for a kill in the middle.</b> The kept-results snapshot is copied first, then
        /// the checkpoint is written at the new path naming the copy (both temp, flush, rename), and
        /// only then is the legacy pair deleted. A kill before the checkpoint lands leaves the legacy
        /// file in charge and the next resume moves it again; a kill after it leaves at worst a legacy
        /// pair nothing reads, because the new path is then taken.</para>
        /// </summary>
        public static LegacyCheckpoint? AdoptLegacyStageTwo(string legacyPath, string newPath, Query q,
                                                            int[] survivors, string queryHash)
        {
            if (SamePath(legacyPath, newPath) || File.Exists(newPath) || !File.Exists(legacyPath)) return null;

            LegacyCheckpoint r = new LegacyCheckpoint { LegacyPath = legacyPath, NewPath = newPath };
            Checkpoint c;
            try
            {
                c = Checkpoint.Load(legacyPath);
            }
            catch (Exception ex)
            {
                r.Reason = "it could not be read (" + ex.Message + ")";
                return r;
            }

            try
            {
                // At least 1, so a hand-edited block_size of 0 gets MustMatch's own sentence rather than
                // ScanPlan's argument check.
                c.MustMatch(q, ScanPlan.OverSeeds(survivors, Math.Max(1, c.BlockSize)), queryHash);
            }
            catch (InvalidOperationException ex)
            {
                r.Reason = "it is not this run's stage 2 (" + ex.Message + ")";
                return r;
            }

            string? legacySnapshot = c.KeptSnapshot;
            string? copied = null;
            try
            {
                string? dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(newPath));
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                if (legacySnapshot != null && File.Exists(legacySnapshot))
                {
                    copied = SnapshotPathFor(newPath);
                    CopyDurably(legacySnapshot, copied);
                    c.KeptSnapshot = copied;
                }

                c.Save(newPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The legacy pair has not been touched; take back what was written so the new path does
                // not hold a snapshot with no checkpoint.
                List<string> written = new List<string> { newPath + ".tmp", SnapshotPathFor(newPath) + ".tmp" };
                if (copied != null) written.Add(copied);
                foreach (string p in written)
                {
                    try
                    {
                        if (File.Exists(p)) File.Delete(p);
                    }
                    catch (Exception)
                    {
                    }
                }

                r.Reason = "it could not be moved (" + ex.Message + ")";
                return r;
            }

            r.Adopted = true;
            List<string> leftBehind = new List<string>();
            List<string> pair = new List<string> { legacyPath };
            if (legacySnapshot != null && SamePath(legacySnapshot, SnapshotPathFor(legacyPath))) pair.Add(legacySnapshot);
            foreach (string p in pair)
            {
                try
                {
                    if (File.Exists(p)) File.Delete(p);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    leftBehind.Add(p);
                }
            }

            r.LeftBehind = leftBehind;
            return r;
        }

        /// <summary>Copies a file to a temp sibling, flushes it to the device, then renames it into place.</summary>
        private static void CopyDurably(string from, string to)
        {
            string tmp = to + ".tmp";
            using (FileStream src = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (FileStream dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                src.CopyTo(dst);
                dst.Flush(true);
            }

            File.Move(tmp, to, overwrite: true);
        }

        /// <summary>Two spellings of one file, compared the way the file system compares them here.</summary>
        internal static bool SamePath(string a, string b)
        {
            try
            {
                return string.Equals(System.IO.Path.GetFullPath(a), System.IO.Path.GetFullPath(b),
                                     OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
            }
            catch (Exception)
            {
                return false;
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
