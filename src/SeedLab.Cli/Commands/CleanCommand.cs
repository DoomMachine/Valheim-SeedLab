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
  per-process scratch and the self-test stamp all live there, and deleting the whole root
  at any moment loses nothing you asked to keep.

Options:
  --dry-run            report only; say what WOULD be removed (this is also the default)
  --yes                actually remove it
  --what <categories>  comma-separated: maps,tiles,scratch,checkpoints,runs,selftest,all
                       (default: maps,tiles,scratch - the caches, not your resume points)
  --json

  Checkpoints are NOT in the default set: one of them may be the resume point of a search
  you are part-way through. 'vseed clean --what checkpoints --yes' removes them.

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

            if (o.Json) return Json(o, rt, report, dumper, categories, dryRun);

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
                    categories.Contains("all") || categories.Contains(e.Name)
                        ? (dryRun ? "would be removed" : "removed")
                        : "kept",
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
            long removed = 0;
            int removedFiles = 0;
            List<string> failed = new List<string>();
            foreach (DiskUsageEntry e in report.Entries)
            {
                if (!categories.Contains("all") && !categories.Contains(e.Name)) continue;
                if (dryRun) { removed += e.Bytes; removedFiles += e.Files; continue; }
                try
                {
                    if (Directory.Exists(e.Path))
                    {
                        foreach (string f in Directory.EnumerateFiles(e.Path, "*", SearchOption.AllDirectories))
                        {
                            try { File.Delete(f); removedFiles++; } catch (Exception) { }
                        }

                        foreach (string d in Directory.GetDirectories(e.Path))
                        {
                            try { Directory.Delete(d, recursive: true); } catch (Exception) { }
                        }
                    }

                    removed += e.Bytes;
                }
                catch (Exception ex)
                {
                    failed.Add(e.Path + " (" + ex.GetType().Name + ")");
                }
            }

            o.Header(dryRun ? "What --yes would remove" : "Removed");
            o.Field(dryRun ? "would free" : "freed", Bytes.Human(removed) + " in "
                    + removedFiles.ToString(CultureInfo.InvariantCulture) + " files");
            if (failed.Count > 0)
            {
                foreach (string f in failed) o.Note("could not remove " + f);
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

        private static int Json(Out o, CliRuntime rt, DiskUsageReport report, DumperFolder dumper,
                                HashSet<string> categories, bool dryRun)
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
