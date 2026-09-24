using System;
using System.Collections.Generic;
using System.IO;
using SeedLab.Data;

namespace SeedLab.SearchTests
{
    /// <summary>
    /// What a session log can say about the game data without hashing it a second time (2026-09-24):
    /// <see cref="GameData.VerifiedFileCount"/> counts the files this process read and found identical
    /// to their SHA-256 in <c>manifest.json</c>. The sections before this one loaded the dumped
    /// location table, so the count is already there to read.
    /// </summary>
    public static class IntegrityChecks
    {
        public static void Run(Action<bool, string, string> check)
        {
            GameData? data = GameData.Loaded;
            int listed = data?.Manifest.files?.Length ?? 0;
            IReadOnlyList<string> files = GameData.VerifiedFiles;
            check(data != null && listed > 0,
                  "the dumped game data was loaded by the sections before this one (GUARD)",
                  data == null ? "no data loaded - SKIPPED, not passed" : data.Directory + ", " + listed + " files listed");
            if (data == null) return;

            string dir = Path.GetFullPath(data.Directory);
            bool inside = true;
            HashSet<string> distinct = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string f in files)
            {
                inside &= f.StartsWith(dir, StringComparison.OrdinalIgnoreCase) && File.Exists(f);
                distinct.Add(f);
            }

            check(GameData.VerifiedFileCount > 0 && GameData.VerifiedFileCount == files.Count && distinct.Count == files.Count
                  && files.Count <= listed && inside,
                  "the files this process matched against manifest.json's SHA-256 are counted once each, by full path",
                  GameData.VerifiedFileCount + " of the " + listed + " listed files were read and verified");
            check(!distinct.Contains(Path.Combine(dir, "manifest.json")),
                  "and the manifest, which holds the hashes and so cannot check itself, is not among them", "");
        }
    }
}
