using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using SeedLab.Cli.Infra;
using SeedLab.Runtime.Hardware;
using SeedLab.Runtime.Storage;

namespace SeedLab.Cli.Commands
{
    /// <summary>
    /// <c>vseed clean</c> - what SeedLab is using on disk, and removal of the parts it no longer needs.
    ///
    /// <para><b>Why it exists (audit defect 7).</b> The tool writes in exactly one place by design -
    /// the cache root - but nothing ever told the user where that was, how big it had become, or that
    /// the 49 MB dumper folder still sitting in their profile is now a duplicate of <c>data\</c>. A
    /// tool that quietly accumulates and has no way to be asked about it is the thing the whole disk
    /// audit was about.</para>
    ///
    /// <para><b>It reports by default.</b> Nothing is deleted unless <c>--yes</c> is given, and the
    /// dumper folder in the user's profile is NEVER deleted by this command whatever they pass: it is
    /// their data, it lives outside SeedLab's cache root, and it is the only copy of the raw dump if
    /// <c>data\</c> is ever rebuilt wrongly. It is reported, verified byte for byte against
    /// <c>data\</c>, and named as safe to delete BY HAND.</para>
    /// </summary>
    public static class CleanCommand
    {
        public const string Help = @"vseed clean [options]

  Reports what SeedLab holds on disk, and removes what it no longer needs.

  SeedLab writes to exactly one place: the cache root (--cache-dir, or SEEDLAB_CACHE_DIR,
  or %LOCALAPPDATA%\SeedLab). Checkpoints, rendered maps, web tile caches, run manifests,
  per-process scratch, the self-test stamp and the session log all live there, and
  deleting the whole root at any moment loses nothing you asked to keep.

Options:
  --dry-run            report only; say what WOULD be removed (this is also the default)
  --yes                actually remove it
  --what <categories>  comma-separated: maps,tiles,scratch,checkpoints,runs,selftest,logs,all
                       (default: maps,tiles,scratch - the caches, not your resume points)
  --json

  Checkpoints are NOT in the default set: one of them may be the resume point of a search
  you are part-way through. 'vseed clean --what checkpoints --yes' removes them.

  'freed' counts only the files that were really deleted. A file another program has open
  - a vseed that is still running, a viewer, a sync tool - is left alone and listed, and
  this command's own session log is always kept (the next session rewrites it).

  The report also checks %USERPROFILE%\AppData\valheim-dumper, the raw output of the
  dumper plugin, against SeedLab's own data\ folder. It is never deleted by this command.";

        public static int Run(Args a, Out o, CliRuntime rt)
        {
            bool yes = a.Flag("yes");
            bool dryRun = a.Flag("dry-run", !yes);
            string what = a.Get("what") ?? "maps,tiles,scratch";
            a.RejectUnknown();

            HashSet<string> categories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string part in what.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                categories.Add(part.Trim());
            }

            List<string> known = new List<string>();
            foreach ((string name, string _) in rt.Cache.Categories) known.Add(name);
            foreach (string c in categories)
            {
                if (!string.Equals(c, "all", StringComparison.OrdinalIgnoreCase) && !known.Contains(c))
                {
                    throw new CliException("--what: '" + c + "' is not a category.", ExitCodes.Usage,
                        "categories: " + string.Join(", ", known) + ", all");
                }
            }

            DiskUsageReport report = rt.Cache.MeasureUsage();
            DumperFolder dumper = DumperFolder.Inspect();

            // The removal comes first, for both outputs: '--json --yes' used to return before it and
            // delete nothing while reporting nothing either (found 2026-09-24).
            Removal removal = Remove(rt, report, categories, dryRun);
            if (o.Json) return Json(o, rt, report, dumper, categories, dryRun, removal);

            o.Header("SeedLab on disk");
            o.Field("cache root", rt.Cache.Path + "   (" + rt.Cache.SourceDetail + ")");
            List<string[]> rows = new List<string[]>();
            foreach (DiskUsageEntry e in report.Entries)
            {
                rows.Add(new[]
                {
                    e.Name,
                    Bytes.Human(e.Bytes),
                    e.Files.ToString(CultureInfo.InvariantCulture),
                    categories.Contains("all") || categories.Contains(e.Name) ? removal.Outcome(e.Name, dryRun) : "kept",
                    e.Path,
                });
            }

            o.Table(new[] { "category", "size", "files", "this run", "path" }, rows,
                    new[] { false, true, true, false, false });
            o.Field("total", Bytes.Human(report.TotalBytes) + " in "
                             + report.TotalFiles.ToString(CultureInfo.InvariantCulture) + " files");
            o.Field("volume", report.Volume.ToString());

            // ---- what the startup already reaped --------------------------------------------------
            if (rt.Context.Reaped != null && rt.Context.Reaped.DidAnything)
            {
                o.Note("");
                o.Note("at startup: " + rt.Context.Reaped.Line());
            }

            // ---- the removal ------------------------------------------------------------------------
            o.Header(dryRun ? "What --yes would remove" : "Removed");
            o.Field(dryRun ? "would free" : "freed", Bytes.Human(removal.Bytes) + " in "
                    + removal.Files.ToString(CultureInfo.InvariantCulture) + " files");
            foreach (string k in removal.Kept) o.Note("kept " + k);
            if (removal.Failed.Count > 0)
            {
                o.Note("");
                o.Note("could not remove " + removal.Failed.Count.ToString(CultureInfo.InvariantCulture)
                       + (removal.Failed.Count == 1 ? " file" : " files") + " - left exactly as they were, and not counted above:");
                foreach ((string path, string why) in removal.Failed)
                {
                    o.Note("  " + path);
                    o.Note("      " + why);
                }

                o.Note("Close whatever has them open and run this again.");
            }

            if (dryRun)
            {
                o.Note("");
                o.Note("nothing was deleted. Add --yes to do it.");
            }

            // ---- the dumper folder, which is now a duplicate ------------------------------------------
            o.Header("The dumper's own output folder");
            if (!dumper.Exists)
            {
                o.Note(dumper.Path + " is not on this machine - nothing to say.");
            }
            else
            {
                o.Field("path", dumper.Path);
                o.Field("size", Bytes.Human(dumper.Bytes) + " in "
                                + dumper.Files.ToString(CultureInfo.InvariantCulture) + " files");
                o.Field("already in data", dumper.Verdict);
                o.Note("");
                if (dumper.FullyDuplicated)
                {
                    o.Note("This folder is where the dumper plugin WRITES, inside the game. SeedLab reads its");
                    o.Note("own data\\ folder and never looks here, and every file above was compared by");
                    o.Note("SHA-256 against the copy in data\\ just now - so this folder is REDUNDANT and you");
                    o.Note("can delete it by hand to get " + Bytes.Human(dumper.Bytes) + " back.");
                    o.Note("");
                    o.Note("'vseed clean' does not delete it for you, deliberately: it is outside SeedLab's");
                    o.Note("cache root, it is your data, and it is the raw dump every copy in data\\ came");
                    o.Note("from. Run the dumper again and it will simply reappear.");
                }
                else
                {
                    o.Note("Some of it is NOT duplicated in data\\ (see above), so it is not redundant yet.");
                    o.Note("Copy the missing files into SeedLab's data\\<version>-<hash>\\ first; 'vseed data'");
                    o.Note("will then report a match.");
                }
            }

            o.Flush();
            return ExitCodes.Ok;
        }

        /// <summary>What a clean removed, or would remove - counted file by file as each delete succeeds.</summary>
        private sealed class Removal
        {
            public long Bytes;
            public int Files;

            /// <summary>Files left on purpose, each with why: this command's own session log.</summary>
            public readonly List<string> Kept = new List<string>();

            /// <summary>Files a delete failed on, each with the probable cause in words.</summary>
            public readonly List<(string Path, string Why)> Failed = new List<(string, string)>();

            /// <summary>Per category: files removed (or, dry, that would be).</summary>
            public readonly Dictionary<string, int> RemovedIn = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            /// <summary>Per category: files left where they were - kept on purpose, or a delete that failed.</summary>
            public readonly Dictionary<string, int> LeftIn = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            public void Count(Dictionary<string, int> into, string category) =>
                into[category] = (into.TryGetValue(category, out int n) ? n : 0) + 1;

            /// <summary>
            /// The table's "this run" column, from what really happened to the category's files: "removed"
            /// only when every one went. It used to be decided by the category alone, so the logs row said
            /// "removed" above "freed 0 B" and "kept ...\vseed.log - this command's own session log"
            /// (review of 2026-09-24) - and this command always keeps its own log.
            /// </summary>
            public string Outcome(string category, bool dryRun)
            {
                int removed = RemovedIn.TryGetValue(category, out int r) ? r : 0;
                int left = LeftIn.TryGetValue(category, out int l) ? l : 0;
                if (removed == 0 && left == 0) return "nothing to remove";
                if (dryRun) return left == 0 ? "would be removed" : removed == 0 ? "would be kept (in use)" : "would be partly removed";
                return left == 0 ? "removed" : removed == 0 ? "kept (in use)" : "partly removed";
            }
        }

        /// <summary>
        /// Deletes the selected categories' files (or, for a dry run, counts them), adding each file's
        /// size to what was freed only when ITS delete succeeded.
        ///
        /// <para><b>Why file by file</b> (2026-09-24). This used to delete what it could, swallow every
        /// failure, and then add the category's whole measured size to "freed" - so a file another
        /// program held was skipped in silence and still counted. Now: a held file is listed with its
        /// probable cause, and not counted. This session's own log is never deleted - it is open for
        /// writing right now, and the next session empties it anyway.</para>
        /// </summary>
        private static Removal Remove(CliRuntime rt, DiskUsageReport report, HashSet<string> categories, bool dryRun)
        {
            Removal r = new Removal();
            string? ownLog = rt.Log.Path;
            foreach (DiskUsageEntry e in report.Entries)
            {
                if (!categories.Contains("all") && !categories.Contains(e.Name)) continue;
                if (!Directory.Exists(e.Path)) continue;

                List<string> files;
                try
                {
                    files = new List<string>(Directory.EnumerateFiles(e.Path, "*", SearchOption.AllDirectories));
                }
                catch (Exception ex)
                {
                    r.Failed.Add((e.Path, "the folder could not be listed: " + (FileRetry.DiagnoseEscaped(ex, "list")?.Cause ?? ex.Message)));
                    r.Count(r.LeftIn, e.Name);
                    continue;
                }

                foreach (string f in files)
                {
                    if (ownLog != null && string.Equals(Path.GetFullPath(f), ownLog, StringComparison.OrdinalIgnoreCase))
                    {
                        r.Kept.Add(f + " - this command's own session log, open while it runs; the next session rewrites it");
                        r.Count(r.LeftIn, e.Name);
                        continue;
                    }

                    long len = 0;
                    try
                    {
                        len = new FileInfo(f).Length;
                    }
                    catch (Exception)
                    {
                    }

                    if (dryRun)
                    {
                        r.Bytes += len;
                        r.Files++;
                        r.Count(r.RemovedIn, e.Name);
                        continue;
                    }

                    try
                    {
                        File.Delete(f);
                        r.Bytes += len;
                        r.Files++;
                        r.Count(r.RemovedIn, e.Name);
                    }
                    catch (Exception ex)
                    {
                        r.Failed.Add((f, WhyNot(f, ex, e.Name)));
                        r.Count(r.LeftIn, e.Name);
                    }
                }

                if (dryRun) continue;

                // The folders the files were in, deepest first; one that still holds a file it could not
                // delete stays, which is correct.
                List<string> dirs;
                try
                {
                    dirs = new List<string>(Directory.GetDirectories(e.Path, "*", SearchOption.AllDirectories));
                }
                catch (Exception)
                {
                    dirs = new List<string>();
                }

                dirs.Sort((x, y) => y.Length.CompareTo(x.Length));
                foreach (string d in dirs)
                {
                    try
                    {
                        Directory.Delete(d, recursive: false);
                    }
                    catch (Exception)
                    {
                    }
                }
            }

            if (!dryRun)
            {
                rt.Log.Info("clean    removed " + r.Files.ToString(CultureInfo.InvariantCulture) + " files, "
                            + Bytes.Human(r.Bytes) + "; could not remove " + r.Failed.Count.ToString(CultureInfo.InvariantCulture));
                foreach ((string path, string why) in r.Failed) rt.Log.Warn("clean    could not remove " + path + " - " + why);
            }

            return r;
        }

        /// <summary>Why a delete failed, in words - from the file itself, the way a failed save is diagnosed.</summary>
        private static string WhyNot(string file, Exception ex, string category)
        {
            FileDiagnosis d = FileRetry.Diagnose(file, ex, null, "delete");
            if (d.Problem == FileProblem.InUse && string.Equals(category, "logs", StringComparison.OrdinalIgnoreCase))
            {
                return "in use - most likely another vseed that is still running writes this log";
            }

            return d.Cause;
        }

        private static int Json(Out o, CliRuntime rt, DiskUsageReport report, DumperFolder dumper,
                                HashSet<string> categories, bool dryRun, Removal removal)
        {
            o.J.WriteStartObject();
            o.J.WriteString("cache_root", rt.Cache.Path);
            o.J.WriteString("cache_root_source", rt.Cache.SourceDetail);
            o.J.WriteBoolean("dry_run", dryRun);
            o.J.WriteStartArray("categories");
            foreach (DiskUsageEntry e in report.Entries)
            {
                o.J.WriteStartObject();
                o.J.WriteString("name", e.Name);
                o.J.WriteString("path", e.Path);
                o.J.WriteNumber("bytes", e.Bytes);
                o.J.WriteNumber("files", e.Files);
                o.J.WriteBoolean("selected", categories.Contains("all") || categories.Contains(e.Name));
                o.J.WriteEndObject();
            }

            o.J.WriteEndArray();
            o.J.WriteNumber("total_bytes", report.TotalBytes);
            o.J.WriteNumber("total_files", report.TotalFiles);

            // What was really deleted (or, dry, would be), file by file, and what was not and why.
            o.J.WriteNumber(dryRun ? "would_free_bytes" : "freed_bytes", removal.Bytes);
            o.J.WriteNumber(dryRun ? "would_remove_files" : "removed_files", removal.Files);
            o.J.WriteStartArray("kept");
            foreach (string k in removal.Kept) o.J.WriteStringValue(k);
            o.J.WriteEndArray();
            o.J.WriteStartArray("could_not_remove");
            foreach ((string path, string why) in removal.Failed)
            {
                o.J.WriteStartObject();
                o.J.WriteString("path", path);
                o.J.WriteString("why", why);
                o.J.WriteEndObject();
            }

            o.J.WriteEndArray();
            o.J.WriteNumber("free_bytes", report.Volume.FreeBytes);
            o.J.WriteStartObject("dumper_folder");
            o.J.WriteString("path", dumper.Path);
            o.J.WriteBoolean("exists", dumper.Exists);
            o.J.WriteNumber("bytes", dumper.Bytes);
            o.J.WriteNumber("files", dumper.Files);
            o.J.WriteBoolean("redundant", dumper.FullyDuplicated);
            o.J.WriteString("verdict", dumper.Verdict);
            o.J.WriteEndObject();
            o.J.WriteEndObject();
            o.Flush();
            return ExitCodes.Ok;
        }

        /// <summary>
        /// The dumper plugin's raw output in the user's profile, and whether every byte of it is
        /// already in SeedLab's <c>data\</c>.
        ///
        /// <para>"Redundant" is a claim about the user's disk, so it is MEASURED - every file compared
        /// by SHA-256 against its counterpart in <c>data\</c> - and never inferred from the two folders
        /// having the same name.</para>
        /// </summary>
        private sealed class DumperFolder
        {
            public string Path = "";
            public bool Exists;
            public long Bytes;
            public int Files;
            public bool FullyDuplicated;
            public string Verdict = "";

            public static DumperFolder Inspect()
            {
                // The dumper writes to Application.persistentDataPath's parent: on Windows that is
                // %USERPROFILE%\AppData\LocalLow\.. -> %USERPROFILE%\AppData\valheim-dumper.
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                string root = System.IO.Path.Combine(home, "AppData", "valheim-dumper");
                DumperFolder f = new DumperFolder { Path = root };

                if (!Directory.Exists(root))
                {
                    f.Verdict = "the folder is not here";
                    return f;
                }

                f.Exists = true;
                string? dataDir = FindDataDirectory();
                int same = 0, missing = 0, differing = 0;

                foreach (string file in Safe(root))
                {
                    long len;
                    try { len = new FileInfo(file).Length; } catch (Exception) { continue; }
                    f.Bytes += len;
                    f.Files++;

                    if (dataDir == null) { missing++; continue; }
                    string rel = System.IO.Path.GetRelativePath(root, file);
                    string mirror = System.IO.Path.Combine(dataDir, rel);
                    if (!File.Exists(mirror)) { missing++; continue; }
                    if (Sha(file) == Sha(mirror)) same++;
                    else differing++;
                }

                f.FullyDuplicated = f.Files > 0 && missing == 0 && differing == 0;
                f.Verdict = dataDir == null
                    ? "SeedLab's data\\ folder was not found beside this build - nothing to compare against"
                    : same.ToString(CultureInfo.InvariantCulture) + " of "
                      + f.Files.ToString(CultureInfo.InvariantCulture) + " files byte-identical in "
                      + dataDir + (missing > 0 ? ", " + missing + " not there" : "")
                      + (differing > 0 ? ", " + differing + " DIFFERENT" : "");
                return f;
            }

            /// <summary>
            /// SeedLab's <c>data\</c>, found by walking up from the working directory and the binary -
            /// the same rule <see cref="Verified.FindGroundTruth"/> uses, so the two agree about what
            /// "beside this build" means.
            /// </summary>
            private static string? FindDataDirectory()
            {
                foreach (string start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
                {
                    DirectoryInfo? d = new DirectoryInfo(start);
                    for (int i = 0; i < 12 && d != null; i++, d = d.Parent)
                    {
                        string cand = System.IO.Path.Combine(d.FullName, "data");
                        if (Directory.Exists(cand)) return cand;
                    }
                }

                return null;
            }

            private static IEnumerable<string> Safe(string dir)
            {
                string[] files;
                try { files = Directory.GetFiles(dir, "*", SearchOption.AllDirectories); }
                catch (Exception) { files = Array.Empty<string>(); }
                return files;
            }

            private static string Sha(string path)
            {
                try
                {
                    using FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
                    using SHA256 sha = SHA256.Create();
                    return Convert.ToHexString(sha.ComputeHash(fs));
                }
                catch (Exception)
                {
                    return Guid.NewGuid().ToString();   // unreadable: never equal to anything
                }
            }
        }
    }
}
