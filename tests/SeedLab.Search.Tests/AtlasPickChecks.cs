using System;
using System.IO;
using SeedLab.Data;
using SeedLab.Search.Criteria;
using SeedLab.Search.Evaluation;
using SeedLab.Search.Feasibility;
using SeedLab.Search.Locations;

namespace SeedLab.SearchTests
{
    /// <summary>
    /// The constraint atlas follows the location table's build (2026-09-26). With two builds in
    /// <c>data\</c> - 1.0.15 kept beside 1.0.16, as <c>docs\game-data.md</c> tells users to do -
    /// <c>GameData</c> picked the folder that matches the installed game, but the atlas was the FIRST
    /// folder's, so every 1.0.16 search ran in ADVISORY mode (nothing refused) and D4/D5 quoted the
    /// 1.0.15 sample. <see cref="ConstraintAtlas.PickFrom"/> now prefers the table's build, and the
    /// per-process cache is keyed by that build.
    /// </summary>
    public static class AtlasPickChecks
    {
        public static void Run(Action<bool, string, string> check)
        {
            ThePickRule(check);
            TheCacheDoesNotPin(check);
            TheLiveData(check);
        }

        /// <summary>The rule itself, on a data folder of this test's own.</summary>
        private static void ThePickRule(Action<bool, string, string> check)
        {
            string data = Path.Combine(Path.GetTempPath(), "seedlab-atlaspick-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                string a15 = Atlas(data, "1.0.15-59f53fb5", "1.0.15", "59f53fb5");
                string a16 = Atlas(data, "1.0.16-96cfc004", "1.0.16", "96cfc004");
                Directory.CreateDirectory(Path.Combine(data, "1.0.17-abcdef12"));     // a dump with no atlas
                string? first = null;
                foreach (string d in Directory.GetDirectories(data))
                {
                    string c = Path.Combine(d, ConstraintAtlas.FileName);
                    if (File.Exists(c)) { first = c; break; }
                }

                string? got16 = ConstraintAtlas.PickFrom(data, "1.0.16 / 96cfc004");
                string? got15 = ConstraintAtlas.PickFrom(data, "1.0.15 / 59f53fb5");
                check(got16 == a16 && got15 == a15,
                      "with two builds in data\\, the atlas of the location table's build is picked, whichever folder comes first",
                      "1.0.16 -> " + Rel(data, got16) + ", 1.0.15 -> " + Rel(data, got15));

                check(ConstraintAtlas.PickFrom(data, "1.0.16 / 96CFC004") == a16,
                      "the folder name is matched on the hash case-insensitively, as GameData matches it", "");

                string? none = ConstraintAtlas.PickFrom(data, "");
                string? other = ConstraintAtlas.PickFrom(data, "9.9.9 / 00000000");
                string? noAtlas = ConstraintAtlas.PickFrom(data, "1.0.17 / abcdef12");
                check(first != null && none == first && other == first && noAtlas == first,
                      "with no tag, a build no folder holds, or a matching folder without an atlas, the first folder with an atlas is used as before (the stamp gate then reports the mismatch)",
                      "no tag -> " + Rel(data, none) + ", unknown build -> " + Rel(data, other)
                      + ", 1.0.17 (no atlas) -> " + Rel(data, noAtlas));

                check(ConstraintAtlas.PickFrom(Path.Combine(data, "missing"), "1.0.16 / 96cfc004") == null,
                      "a data folder that does not exist gives no atlas", "");
            }
            finally
            {
                try { if (Directory.Exists(data)) Directory.Delete(data, true); }
                catch (Exception) { }
            }
        }

        /// <summary>
        /// <see cref="ConstraintAtlas.Load"/> end to end through <c>SEEDLAB_DATA_DIR</c>, on two invented
        /// builds so the process-wide cache entries of the real build are never touched: the first
        /// build loaded must not be handed to a caller asking for the other.
        /// </summary>
        private static void TheCacheDoesNotPin(Action<bool, string, string> check)
        {
            string data = Path.Combine(Path.GetTempPath(), "seedlab-atlascache-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            string? before = Environment.GetEnvironmentVariable(ConstraintAtlas.DirectoryEnvironmentVariable);
            try
            {
                Atlas(data, "9.9.1-aaaaaaa1", "9.9.1", "aaaaaaa1");
                Atlas(data, "9.9.2-bbbbbbb2", "9.9.2", "bbbbbbb2");
                Environment.SetEnvironmentVariable(ConstraintAtlas.DirectoryEnvironmentVariable, data);

                ConstraintAtlas second = ConstraintAtlas.Load("9.9.2 / bbbbbbb2");
                ConstraintAtlas firstBuild = ConstraintAtlas.Load("9.9.1 / aaaaaaa1");
                ConstraintAtlas again = ConstraintAtlas.Load("9.9.2 / bbbbbbb2");
                check(second.Available && second.BuildTag == "9.9.2 / bbbbbbb2"
                      && firstBuild.Available && firstBuild.BuildTag == "9.9.1 / aaaaaaa1"
                      && ReferenceEquals(second, again),
                      "Load(tag) returns each build's own atlas, and caches per build rather than pinning the first one loaded",
                      second.BuildTag + " then " + firstBuild.BuildTag + "; the repeat is the cached object: "
                      + ReferenceEquals(second, again));
            }
            finally
            {
                Environment.SetEnvironmentVariable(ConstraintAtlas.DirectoryEnvironmentVariable, before);
                try { if (Directory.Exists(data)) Directory.Delete(data, true); }
                catch (Exception) { }
            }
        }

        /// <summary>
        /// The data as this process finds it (the working directory's <c>data\</c>, or
        /// <c>SEEDLAB_DATA_DIR</c>): when the location table's own folder holds an atlas, that is the
        /// atlas the checker uses, and a location query is checked with refusals enabled.
        /// </summary>
        private static void TheLiveData(Action<bool, string, string> check)
        {
            ILocationOracle oracle = SeedLab.LocationOracle.DumpedLocationOracle.Create(out string? problem);
            GameData? data = GameData.Loaded;
            check(oracle.Available && data != null, "the dumped location table opens (GUARD)",
                  oracle.Available ? oracle.Provenance : "SKIPPED, not passed: " + (problem ?? oracle.UnavailableReason));
            if (!oracle.Available || data == null) return;

            string p = oracle.Provenance;
            int paren = p.IndexOf('(');
            string tag = (paren > 0 ? p.Substring(0, paren) : p).Trim();
            string own = Path.GetFullPath(Path.Combine(data.Directory, ConstraintAtlas.FileName));
            if (!File.Exists(own))
            {
                check(true, "the location table's folder holds no atlas, so there is no pairing to check",
                      "NOT APPLICABLE: " + data.Directory);
                return;
            }

            ConstraintAtlas atlas = ConstraintAtlas.Load(tag);
            check(atlas.Available && atlas.BuildTag == tag
                  && string.Equals(Path.GetFullPath(atlas.Path), own, StringComparison.OrdinalIgnoreCase),
                  "the atlas the checker loads is the one beside the location table in use",
                  "table " + tag + " in " + data.Directory + "; atlas " + atlas.BuildTag + " at " + atlas.Path);

            Query q = QueryReader.Parse(
                @"{""version"":1,""goals"":[{""id"":""g"",""target"":""location:Eikthyrnir"",
                   ""metric"":""count"",""test"":""at_least"",""value"":1,""importance"":""must""}]}", "atlas-pick");
            QueryCheckReport r = QueryCheck.Run(q, CompiledQuery.Compile(q, oracle), oracle);
            check(r.AtlasAvailable && r.RefusalsEnabled && r.StampMismatch == null
                  && ConstraintAtlas.BuildTagOf(r.AtlasStamp) == r.DataBuildTag,
                  "a location query is checked against the same build's atlas, with refusals enabled (not ADVISORY)",
                  "atlas " + ConstraintAtlas.BuildTagOf(r.AtlasStamp) + ", table " + r.DataBuildTag
                  + (r.StampMismatch != null ? "; " + r.StampMismatch : ""));
        }

        /// <summary>A minimal atlas: a stamp and one location row, which is what makes it Available.</summary>
        private static string Atlas(string data, string folder, string version, string sha8)
        {
            string dir = Path.Combine(data, folder);
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, ConstraintAtlas.FileName);
            File.WriteAllText(path,
                "{\"stamp\": \"DATA-STAMP game-version=" + version + " network=40 assembly_valheim-sha256=" + sha8
                + new string('0', 56) + " dumped=2026-09-26 schema=1\", \"atlasVersion\": 1,"
                + " \"locations\": [{\"prefabName\": \"Eikthyrnir\", \"orderedIndex\": 0, \"quantity\": 3}]}");
            return path;
        }

        private static string Rel(string data, string? path)
            => path == null ? "(none)" : Path.GetFileName(Path.GetDirectoryName(path) ?? "") ?? "(none)";
    }
}
