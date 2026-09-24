using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using SeedLab.Cli.Infra;
using SeedLab.Contracts.Dump;
using SeedLab.Data;

namespace SeedLab.Cli.Commands
{
    /// <summary>
    /// <c>vseed data</c> - what game data this build is carrying, which Valheim it came out of, and
    /// whether that is the Valheim installed here.
    ///
    /// <para>It exists because every location answer the tool gives is only as true as the tables
    /// underneath it, and those tables were read out of one specific build of the game. This command
    /// is where that provenance is visible instead of implied: the DATA-STAMP, the measured counts
    /// (measured from the loaded tables, not copied from the manifest, so a truncated file shows up
    /// as a wrong number rather than as a confident one), and the verdict on the installed game.</para>
    ///
    /// <para><c>--verify</c> re-runs the checks that the data has not drifted from everything else
    /// that was measured independently: the manifest's own SHA-256s, the prefab names and quantities
    /// the game printed into <c>LogOutput.log</c>, the hashes in the natives golden, and the four
    /// prefab constants the renderer and the minimap decoder already rely on.</para>
    /// </summary>
    public static class DataCommand
    {
        public const string Help = @"vseed data [--verify] [--goldens] [--names] [--json]

  What game data this build ships, which Valheim build it was dumped from, and whether that
  is the game installed on this machine.

  --verify    re-check the data against everything else that was measured independently:
              the manifest SHA-256s, the prefab names/quantities the game logged, the hash
              golden, and the prefab constants. Exit code 1 if any check fails.
  --goldens   also list the recorded game outputs (per-seed generator dumps, natives).
  --names     list every location prefab with the name a player knows it by, where that
              name came from, and every other spelling it answers to. This is the table
              'vseed locations --name' and a query's 'location:' resolve against, so it
              is also the answer to ""what may I type"". A dash means the dump names the
              place nothing, and the prefab is then what it is called.

  The data is found in SeedLab's data\<version>-<hash>\ folder; SEEDLAB_DATA_DIR overrides.
  The install it is compared against is SEEDLAB_VALHEIM_DIR if that is set, else found by
  walking up from the working directory and from vseed itself, else in the usual Steam
  library folders - the same search tools\check-game-version.ps1 makes without a build.

  Policy when the stamp does not match the installed game: terrain answers (biome, height,
  map, seed arithmetic) continue with a warning, because they are generated from the seed and
  need none of this data. Anything that names a location, dungeon, trader or resource is
  refused, because a table from another build produces coordinates that look right and are not.
  Set SEEDLAB_REQUIRE_GAME_MATCH=1 to refuse those answers when no install can be found either.";

        public static int Run(Args a, Out o)
        {
            bool verify = a.Flag("verify");
            bool goldens = a.Flag("goldens");
            bool names = a.Flag("names");
            a.RejectUnknown();

            GameData data;
            try
            {
                data = GameData.Load();
            }
            catch (GameDataException ex)
            {
                throw new CliException(ex.Message, ExitCodes.NotFound);
            }

            StampCheck check = data.InstalledGameCheck;

            // Every count below is measured from the loaded table. The manifest's own numbers are
            // printed beside them only where they can disagree, because a manifest that agrees with
            // itself proves nothing.
            int locations = data.Locations.Count;
            int enabled = data.EnabledLocations.Count;
            int placement = data.PlacementOrder.Count;
            int withQuantity = data.QuantityPositiveCount;
            int vegetation = data.Vegetation.Count;
            int vegetationEnabled = 0;
            foreach (VegetationDef v in data.Vegetation)
            {
                if (v.enable) vegetationEnabled++;
            }

            int altBiomes = data.AltBiomes.Count;
            int altBiomesEnabled = 0;
            foreach (AltBiomeDef b in data.AltBiomes)
            {
                if (b.enabled) altBiomesEnabled++;
            }

            int prefabs = data.LocationPrefabs.Count;
            int prefabsLoaded = 0;
            foreach (LocationPrefabDef p in data.LocationPrefabs)
            {
                if (p.loaded) prefabsLoaded++;
            }

            PrefabConstantsFile pc = data.PrefabConstants;
            MinimapConstantsDef mm = pc.minimap ?? throw new CliException("prefab-constants.json has no minimap section.", ExitCodes.CheckFailed);
            ZoneSystemConstantsDef zs = pc.zoneSystem ?? throw new CliException("prefab-constants.json has no zoneSystem section.", ExitCodes.CheckFailed);

            List<Check> results = verify ? Verify(data) : new List<Check>();
            bool allPassed = true;
            foreach (Check c in results)
            {
                if (!c.Passed) allPassed = false;
            }

            if (o.Json)
            {
                WriteJson(o, data, check, locations, enabled, placement, withQuantity, vegetation,
                          vegetationEnabled, altBiomes, altBiomesEnabled, prefabs, prefabsLoaded,
                          mm, zs, goldens, names, verify, results);
                return allPassed ? ExitCodes.Ok : ExitCodes.CheckFailed;
            }

            o.Header("Game data");
            o.Field("folder", data.Directory);
            o.Field("game", "Valheim " + data.Stamp.GameVersion
                            + ", network " + data.Stamp.NetworkVersion.ToString(CultureInfo.InvariantCulture)
                            + ", Unity " + data.Stamp.UnityVersion);
            o.Field("dumped", data.Stamp.Dumped + " by tools\\SeedLab.Dumper " + data.Stamp.DumperVersion
                              + " (" + data.Stamp.Mode + " run), schema "
                              + data.Stamp.Schema.ToString(CultureInfo.InvariantCulture));
            o.Field("world dumped from", (data.Manifest.world?.name ?? "?")
                                         + "  seed text '" + (data.Manifest.world?.seedText ?? "?") + "'"
                                         + "  seed " + (data.Manifest.world?.seed ?? 0).ToString(CultureInfo.InvariantCulture));
            o.Field("assembly_valheim", data.Stamp.AssemblyValheimSha256);
            o.Field("UnityPlayer", data.Stamp.UnityPlayerSha256);

            o.Header("Installed game");
            o.Field("verdict", Verdict(check));
            o.Field("install", check.GameDirectory ?? "(none found)");
            if (check.InstalledSha256 != null) o.Field("assembly_valheim", check.InstalledSha256);
            o.Note(check.Message);
            o.Note("");
            o.Note("terrain answers (at, map, seed, search on terrain): "
                   + (check.IsMatch ? "allowed" : "allowed, with a warning"));
            o.Note("location answers (locations, dungeons, traders, resources): "
                   + (check.IsMatch ? "allowed"
                                    : check.Result == StampMatch.Mismatch
                                        ? "REFUSED - the data is from another build"
                                        : DataPolicy.RequireMatchRequested()
                                            ? "REFUSED - " + GameInstall.RequireMatchEnvironmentVariable + " is set"
                                            : "allowed, unverified"));

            o.Header("Tables (counted from the loaded files)");
            o.Table(
                new[] { "table", "rows", "enabled", "in placement order", "quantity != 0" },
                new List<string[]>
                {
                    new[] { "locations", locations.ToString(CultureInfo.InvariantCulture),
                            enabled.ToString(CultureInfo.InvariantCulture),
                            placement.ToString(CultureInfo.InvariantCulture),
                            withQuantity.ToString(CultureInfo.InvariantCulture) },
                    new[] { "vegetation", vegetation.ToString(CultureInfo.InvariantCulture),
                            vegetationEnabled.ToString(CultureInfo.InvariantCulture), "-", "-" },
                    new[] { "alt biomes", altBiomes.ToString(CultureInfo.InvariantCulture),
                            altBiomesEnabled.ToString(CultureInfo.InvariantCulture), "-", "-" },
                    new[] { "location prefabs", prefabs.ToString(CultureInfo.InvariantCulture),
                            prefabsLoaded.ToString(CultureInfo.InvariantCulture) + " loaded", "-", "-" },
                },
                new[] { false, true, true, true, true });

            o.Note("");
            o.Note("\"enabled\" is m_enable. \"in placement order\" is m_enable && m_quantity != 0 - the");
            o.Note("list GenerateLocationsTimeSliced actually walks, and the number the game's own log");
            o.Note("line reports. The two differ: 3 entries are enabled with quantity 0, and 17 carry a");
            o.Note("quantity while disabled.");

            o.Header("Prefab constants (serialized asset data, not code defaults)");
            o.Field("minimap textureSize", mm.textureSize.ToString(CultureInfo.InvariantCulture));
            o.Field("minimap pixelSize", mm.pixelSize.ToString("R", CultureInfo.InvariantCulture));
            o.Field("zone locationVersion", zs.locationVersion.ToString(CultureInfo.InvariantCulture));
            o.Field("zone waterLevel", zs.waterLevel.ToString("R", CultureInfo.InvariantCulture));
            o.Field("zone size", zs.zoneSize.ToString("R", CultureInfo.InvariantCulture));
            o.Field("heightmap width/scale", (pc.heightmap?.zoneWidth ?? -1).ToString(CultureInfo.InvariantCulture)
                                             + " / " + (pc.heightmap?.zoneScale ?? float.NaN).ToString("R", CultureInfo.InvariantCulture));
            o.Field("seed field limit", data.SeedInput.seedField?.characterLimit.ToString(CultureInfo.InvariantCulture) + " chars, "
                                        + (data.SeedInput.seedField?.characterValidation ?? "?"));

            if (names) WriteNames(o, data);
            if (goldens) WriteGoldens(o, data);

            if (verify)
            {
                o.Header("Verification");
                List<string[]> rows = new List<string[]>();
                foreach (Check c in results) rows.Add(new[] { c.Passed ? "pass" : "FAIL", c.Name, c.Detail });
                o.Table(new[] { "", "check", "measured" }, rows);
                o.Note("");
                o.Note(allPassed
                    ? "all " + results.Count.ToString(CultureInfo.InvariantCulture) + " checks passed."
                    : "SOME CHECKS FAILED - the data disagrees with something already measured. Do not "
                      + "trust a location answer until this is resolved.");
            }

            return allPassed ? ExitCodes.Ok : ExitCodes.CheckFailed;
        }

        private static string Verdict(StampCheck c) => c.Result switch
        {
            StampMatch.Match => "MATCH - this data describes the installed game",
            StampMatch.Mismatch => "MISMATCH - the installed game is a different build",
            StampMatch.GameNotFound => "UNVERIFIED - no Valheim install found to compare against",
            _ => "UNVERIFIED - the installed assembly could not be read",
        };

        // ---- verification ---------------------------------------------------------------------------

        private sealed class Check
        {
            internal Check(string name, bool passed, string detail)
            {
                Name = name; Passed = passed; Detail = detail;
            }

            internal string Name { get; }
            internal bool Passed { get; }
            internal string Detail { get; }
        }

        private static List<Check> Verify(GameData data)
        {
            List<Check> r = new List<Check>();
            r.Add(ManifestFiles(data));
            r.Add(TablesLoad(data));
            r.Add(LoggedNamesAndHashes(data));
            r.Add(LoggedQuantities(data));
            r.Add(HashGolden(data));
            r.Add(PrefabConstantsCheck(data));
            r.Add(PrefabCoverage(data));
            r.Add(PlacementOrderGolden(data));
            return r;
        }

        private static Check ManifestFiles(GameData data)
        {
            FileEntryDef[] files = data.Manifest.files ?? Array.Empty<FileEntryDef>();
            int ok = 0;
            List<string> bad = new List<string>();
            long bytes = 0;

            foreach (FileEntryDef f in files)
            {
                if (f.path == null || f.sha256 == null) { bad.Add(f.path ?? "(null path)"); continue; }

                string p = Path.Combine(data.Directory, f.path.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(p)) { bad.Add(f.path + " missing"); continue; }

                FileInfo fi = new FileInfo(p);
                if (fi.Length != f.bytes) { bad.Add(f.path + " size " + fi.Length + " != " + f.bytes); continue; }

                string sha = GameInstall.Sha256File(p);
                if (!string.Equals(sha, f.sha256, StringComparison.OrdinalIgnoreCase))
                {
                    bad.Add(f.path + " sha256 " + sha.Substring(0, 12) + "... != " + f.sha256.Substring(0, 12) + "...");
                    continue;
                }

                ok++;
                bytes += fi.Length;
            }

            return new Check(
                "manifest files present, sized and hashed",
                bad.Count == 0,
                ok.ToString(CultureInfo.InvariantCulture) + "/" + files.Length.ToString(CultureInfo.InvariantCulture)
                + " files, " + Out.N(bytes) + " bytes"
                + (bad.Count == 0 ? "" : "; BAD: " + string.Join("; ", bad)));
        }

        private static Check TablesLoad(GameData data)
        {
            // Touching each table runs the strict structural pass: every declared field present in
            // every object, every float carrying a bit pattern that matches its decimal.
            int n = data.Locations.Count + data.Vegetation.Count + data.AltBiomes.Count
                    + data.LocationPrefabs.Count;
            _ = data.PrefabConstants;
            _ = data.VersionConstants;
            _ = data.SeedInput;

            return new Check(
                "every field of every row present (strict load)",
                true,
                n.ToString(CultureInfo.InvariantCulture) + " rows across 4 tables + 3 constant files");
        }

        private static Check LoggedNamesAndHashes(GameData data)
        {
            List<LoggedName> logged = ReadLoggedNames(out string? where);
            if (logged.Count == 0)
            {
                return new Check("prefab names the game logged", false,
                    "groundtruth\\location-names.csv was not found" + (where == null ? "" : " under " + where));
            }

            int found = 0;
            List<string> bad = new List<string>();
            foreach (LoggedName l in logged)
            {
                LocationDef? d = data.LocationByPrefabName(l.Name);
                if (d == null) { bad.Add(l.Name + " not in the table"); continue; }

                found++;
                if (d.nameHash != l.StableHash)
                {
                    bad.Add(l.Name + " hash " + d.nameHash + " != logged " + l.StableHash);
                }
            }

            return new Check(
                "prefab names + name hashes vs the game's log",
                bad.Count == 0,
                found.ToString(CultureInfo.InvariantCulture) + "/" + logged.Count.ToString(CultureInfo.InvariantCulture)
                + " names found, " + found.ToString(CultureInfo.InvariantCulture) + " hashes equal"
                + (bad.Count == 0 ? "" : "; BAD: " + string.Join("; ", bad)));
        }

        private static Check LoggedQuantities(GameData data)
        {
            List<LoggedName> logged = ReadLoggedNames(out _);
            int checkedCount = 0;
            List<string> bad = new List<string>();

            foreach (LoggedName l in logged)
            {
                if (l.QuantityInLog < 0) continue;

                LocationDef? d = data.LocationByPrefabName(l.Name);
                if (d == null) { bad.Add(l.Name + " not in the table"); continue; }

                checkedCount++;
                if (d.quantity != l.QuantityInLog)
                {
                    bad.Add(l.Name + " m_quantity " + d.quantity + " != logged " + l.QuantityInLog);
                }
            }

            return new Check(
                "m_quantity vs the quantities the game logged",
                bad.Count == 0 && checkedCount > 0,
                checkedCount.ToString(CultureInfo.InvariantCulture) + " types compared"
                + (bad.Count == 0 ? ", all equal" : "; BAD: " + string.Join("; ", bad)));
        }

        private static Check HashGolden(GameData data)
        {
            NativesHashFile golden = data.Goldens.Hash();
            Dictionary<string, int> byString = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (HashSampleDef s in golden.samples ?? Array.Empty<HashSampleDef>())
            {
                if (s.s != null) byString[s.s] = s.hash;
            }

            int compared = 0;
            List<string> bad = new List<string>();

            foreach (LocationDef d in data.Locations) Compare(d.prefabName, d.nameHash, "location");
            foreach (VegetationDef v in data.Vegetation) Compare(v.prefabName, v.nameHash, "vegetation");
            foreach (AltBiomeDef b in data.AltBiomes) Compare(b.name, b.nameHash, "altbiome");

            void Compare(string? name, int hash, string kind)
            {
                if (name == null) { bad.Add("(null " + kind + " name)"); return; }
                if (!byString.TryGetValue(name, out int golden2)) { bad.Add(kind + " '" + name + "' not in the golden"); return; }

                compared++;
                if (golden2 != hash) bad.Add(kind + " '" + name + "' " + hash + " != golden " + golden2);
            }

            return new Check(
                "name hashes vs goldens\\natives-hash.json",
                bad.Count == 0,
                compared.ToString(CultureInfo.InvariantCulture) + " table rows compared against "
                + (golden.samples?.Length ?? 0).ToString(CultureInfo.InvariantCulture) + " recorded hashes"
                + (bad.Count == 0 ? ", all equal" : "; BAD: " + string.Join("; ", bad)));
        }

        private static Check PrefabConstantsCheck(GameData data)
        {
            PrefabConstantsFile pc = data.PrefabConstants;
            MinimapConstantsDef mm = pc.minimap!;
            ZoneSystemConstantsDef zs = pc.zoneSystem!;

            List<string> bad = new List<string>();
            if (mm.textureSize != 2048) bad.Add("minimap.textureSize " + mm.textureSize + " != 2048");
            if (mm.pixelSize != 12f) bad.Add("minimap.pixelSize " + mm.pixelSize.ToString("R", CultureInfo.InvariantCulture) + " != 12");
            if (zs.locationVersion != 32) bad.Add("zoneSystem.locationVersion " + zs.locationVersion + " != 32");
            if (zs.waterLevel != 30f) bad.Add("zoneSystem.waterLevel " + zs.waterLevel.ToString("R", CultureInfo.InvariantCulture) + " != 30");

            return new Check(
                "prefab constants vs what the tool measured independently",
                bad.Count == 0,
                "textureSize " + mm.textureSize.ToString(CultureInfo.InvariantCulture)
                + ", pixelSize " + mm.pixelSize.ToString("R", CultureInfo.InvariantCulture)
                + ", locationVersion " + zs.locationVersion.ToString(CultureInfo.InvariantCulture)
                + ", waterLevel " + zs.waterLevel.ToString("R", CultureInfo.InvariantCulture)
                + (bad.Count == 0 ? "" : "; BAD: " + string.Join("; ", bad)));
        }

        private static Check PrefabCoverage(GameData data)
        {
            HashSet<string> prefabs = new HashSet<string>(StringComparer.Ordinal);
            foreach (LocationPrefabDef p in data.LocationPrefabs)
            {
                if (p.prefabName != null) prefabs.Add(p.prefabName);
            }

            List<string> missing = new List<string>();
            foreach (LocationDef d in data.EnabledLocations)
            {
                if (d.prefabName != null && !prefabs.Contains(d.prefabName)) missing.Add(d.prefabName);
            }

            return new Check(
                "every enabled location has its prefab walked",
                missing.Count == 0,
                data.EnabledLocations.Count.ToString(CultureInfo.InvariantCulture) + " enabled locations, "
                + prefabs.Count.ToString(CultureInfo.InvariantCulture) + " prefabs"
                + (missing.Count == 0 ? "" : "; MISSING: " + string.Join(", ", missing)));
        }

        private static Check PlacementOrderGolden(GameData data)
        {
            LocationInstancesFile? golden = null;
            foreach (WorldGenGoldenId id in data.Goldens.WorldGenIds())
            {
                if (!data.Goldens.Has("goldens/locationinstances-" + id.SeedHex + ".json")) continue;
                golden = data.Goldens.LocationInstances(id.SeedHex);
                break;
            }

            if (golden?.orderedPrefabNames == null)
            {
                return new Check("placement order vs the game's own ordered list", false,
                    "no locationinstances golden in this dump");
            }

            string[] recorded = golden.orderedPrefabNames;
            IReadOnlyList<LocationDef> ours = data.PlacementOrder;
            if (ours.Count != recorded.Length)
            {
                return new Check("placement order vs the game's own ordered list", false,
                    "the table gives " + ours.Count + " entries, the game recorded " + recorded.Length);
            }

            for (int i = 0; i < recorded.Length; i++)
            {
                if (!string.Equals(ours[i].prefabName, recorded[i], StringComparison.Ordinal))
                {
                    return new Check("placement order vs the game's own ordered list", false,
                        "entry " + i + " is '" + ours[i].prefabName + "', the game recorded '" + recorded[i] + "'");
                }
            }

            return new Check(
                "placement order vs the game's own ordered list",
                true,
                recorded.Length.ToString(CultureInfo.InvariantCulture) + "/"
                + recorded.Length.ToString(CultureInfo.InvariantCulture) + " prefab names in the same order");
        }

        // ---- groundtruth\location-names.csv --------------------------------------------------------

        private readonly struct LoggedName
        {
            internal LoggedName(string name, int stableHash, int quantityInLog)
            {
                Name = name; StableHash = stableHash; QuantityInLog = quantityInLog;
            }

            internal string Name { get; }
            internal int StableHash { get; }

            /// <summary>-1 when the log line did not carry a quantity for this type.</summary>
            internal int QuantityInLog { get; }
        }

        private static List<LoggedName> ReadLoggedNames(out string? groundTruth)
        {
            groundTruth = Verified.FindGroundTruth();
            List<LoggedName> list = new List<LoggedName>();
            if (groundTruth == null) return list;

            string path = Path.Combine(groundTruth, "location-names.csv");
            if (!File.Exists(path)) return list;

            bool header = true;
            foreach (string line in File.ReadAllLines(path))
            {
                if (header) { header = false; continue; }
                if (line.Length == 0) continue;

                string[] parts = line.Split(',');
                if (parts.Length < 4) continue;
                if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int hash)) continue;

                int quantity = int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int q) ? q : -1;
                list.Add(new LoggedName(parts[0], hash, quantity));
            }

            return list;
        }

        // ---- name listing ----------------------------------------------------------------------------

        /// <summary>
        /// The whole derived name table, in the dump's own order.
        ///
        /// <para>It is the answer to "what may I type", which until now had no answer at all: a user who
        /// did not already know that Haldor's camp is called <c>Vendor_BlackForest</c> had nowhere to
        /// look it up. Every row's <c>from</c> column names the rule that produced the name, because a
        /// name with no stated provenance is the thing this project refuses to ship - and one of them
        /// (the Bog Witch) is a join a human made rather than one the dump contains, which is exactly
        /// what the column exists to make visible.</para>
        /// </summary>
        private static void WriteNames(Out o, GameData data)
        {
            LocationDisplayNames names = data.DisplayNames;

            o.Header("Location names  (" + names.Language + ", from the " + data.Stamp.GameVersion + " dump)");

            List<string[]> rows = new List<string[]>();
            foreach (LocationDisplayName n in names.All)
            {
                // Aliases[0] is always the prefab and Aliases[1] the display name when there is one,
                // so "also answers to" is what is left after the two the row already shows.
                List<string> others = new List<string>();
                for (int i = n.DisplayName == null ? 1 : 2; i < n.Aliases.Count; i++) others.Add(n.Aliases[i]);

                rows.Add(new[]
                {
                    n.Prefab,
                    n.DisplayName ?? "-",
                    n.DisplayName == null ? "-" : n.Source.ToString(),
                    n.NameToken ?? "-",
                    others.Count == 0 ? "-" : string.Join(", ", others.ToArray()),
                });
            }

            o.Table(new[] { "prefab", "name", "from", "token", "also answers to" }, rows,
                    new[] { false, false, false, false, false });

            o.Note("");
            o.Note(names.All.Count.ToString(CultureInfo.InvariantCulture) + " prefabs, of which "
                   + Named(names).ToString(CultureInfo.InvariantCulture) + " carry a name this dump can "
                   + "produce: " + names.BossAltarPrefabs.Count.ToString(CultureInfo.InvariantCulture)
                   + " boss altars, " + names.TraderPrefabs.Count.ToString(CultureInfo.InvariantCulture)
                   + " traders and the rest from Location.m_discoverLabel. The others are named nothing");
            o.Note("in the data, and SeedLab shows the prefab rather than inventing a name for them.");
            o.Note("");
            o.Note("'also answers to' and the two name columns are all accepted as INPUT by 'vseed");
            o.Note("locations --name' and by a query's 'location:' target, with case and spacing ignored");
            o.Note("and a leading \"the\" optional. Everything this tool WRITES - --json, CSV, a results");
            o.Note("file, a run hash - says the prefab, so the two can never be confused.");

            foreach (string note in names.Notes)
            {
                o.Note("");
                o.Note(SearchCommand.Wrap(note, "  "));
            }
        }

        private static int Named(LocationDisplayNames names)
        {
            int n = 0;
            foreach (LocationDisplayName d in names.All)
            {
                if (d.DisplayName != null) n++;
            }

            return n;
        }

        // ---- goldens listing -------------------------------------------------------------------------

        private static void WriteGoldens(Out o, GameData data)
        {
            o.Header("Goldens (what the running game produced)");

            List<string[]> rows = new List<string[]>();
            foreach (WorldGenGoldenId id in data.Goldens.WorldGenIds())
            {
                WorldGenDumpFile g = data.Goldens.WorldGen(id);
                rows.Add(new[]
                {
                    id.SeedHex,
                    id.Source,
                    g.seed.ToString(CultureInfo.InvariantCulture),
                    g.seedText ?? "",
                    (g.lakes?.Length ?? 0).ToString(CultureInfo.InvariantCulture),
                    (g.rivers?.Length ?? 0).ToString(CultureInfo.InvariantCulture),
                    (g.streams?.Length ?? 0).ToString(CultureInfo.InvariantCulture),
                    Out.N(g.riverPointTotal),
                });
            }

            o.Table(new[] { "seed hex", "source", "seed", "seed text", "lakes", "rivers", "streams", "river points" },
                    rows, new[] { false, false, true, false, true, true, true, true });

            NativesPerlinIndexFile perlin = data.Goldens.PerlinIndex();
            int perlinSamples = 0;
            foreach (PerlinBlockDef b in perlin.blocks ?? Array.Empty<PerlinBlockDef>()) perlinSamples += b.sampleCount;

            NativesRandomFile random = data.Goldens.Random();

            o.Note("");
            const int pad = 26;
            o.Field("perlin samples", Out.N(perlinSamples) + " in "
                                      + (perlin.blocks?.Length ?? 0).ToString(CultureInfo.InvariantCulture) + " blocks", pad);
            o.Field("random InitState seeds", (random.initStates?.Length ?? 0).ToString(CultureInfo.InvariantCulture), pad);
            o.Field("random draw traces", (random.traces?.Length ?? 0).ToString(CultureInfo.InvariantCulture), pad);
            o.Field("half samples", (data.Goldens.Half().samples?.Length ?? 0).ToString(CultureInfo.InvariantCulture), pad);
            o.Field("libm samples", (data.Goldens.Libm().samples?.Length ?? 0).ToString(CultureInfo.InvariantCulture), pad);
            o.Field("hash vectors", (data.Goldens.Hash().samples?.Length ?? 0).ToString(CultureInfo.InvariantCulture), pad);
        }

        // ---- json ------------------------------------------------------------------------------------

        private static void WriteJson(Out o, GameData data, StampCheck check, int locations, int enabled,
                                      int placement, int withQuantity, int vegetation, int vegetationEnabled,
                                      int altBiomes, int altBiomesEnabled, int prefabs, int prefabsLoaded,
                                      MinimapConstantsDef mm, ZoneSystemConstantsDef zs, bool goldens,
                                      bool names, bool verify, List<Check> results)
        {
            o.J.WriteStartObject();
            o.J.WriteString("directory", data.Directory);
            o.J.WriteString("stamp", data.Stamp.Raw);

            o.J.WriteStartObject("game");
            o.J.WriteString("version", data.Stamp.GameVersion);
            o.J.WriteNumber("network_version", data.Stamp.NetworkVersion);
            o.J.WriteString("unity_version", data.Stamp.UnityVersion);
            o.J.WriteString("assembly_valheim_sha256", data.Stamp.AssemblyValheimSha256);
            o.J.WriteString("unityplayer_sha256", data.Stamp.UnityPlayerSha256);
            o.J.WriteString("dumped", data.Stamp.Dumped);
            o.J.WriteString("dumper_version", data.Stamp.DumperVersion);
            o.J.WriteNumber("schema", data.Stamp.Schema);
            o.J.WriteEndObject();

            o.J.WriteStartObject("installed_game");
            o.J.WriteString("result", check.Result.ToString());
            o.J.WriteBoolean("match", check.IsMatch);
            o.J.WriteString("directory", check.GameDirectory);
            o.J.WriteString("assembly_valheim_sha256", check.InstalledSha256);
            o.J.WriteString("message", check.Message);
            o.J.WriteBoolean("terrain_answers_allowed", true);
            o.J.WriteBoolean("location_answers_allowed", LocationAnswersAllowed(check));
            o.J.WriteEndObject();

            o.J.WriteStartObject("counts");
            o.J.WriteNumber("locations", locations);
            o.J.WriteNumber("locations_enabled", enabled);
            o.J.WriteNumber("locations_in_placement_order", placement);
            o.J.WriteNumber("locations_with_quantity", withQuantity);
            o.J.WriteNumber("vegetation", vegetation);
            o.J.WriteNumber("vegetation_enabled", vegetationEnabled);
            o.J.WriteNumber("alt_biomes", altBiomes);
            o.J.WriteNumber("alt_biomes_enabled", altBiomesEnabled);
            o.J.WriteNumber("location_prefabs", prefabs);
            o.J.WriteNumber("location_prefabs_loaded", prefabsLoaded);
            o.J.WriteEndObject();

            o.J.WriteStartObject("constants");
            o.J.WriteNumber("minimap_texture_size", mm.textureSize);
            o.J.WriteNumber("minimap_pixel_size", mm.pixelSize);
            o.J.WriteNumber("zone_location_version", zs.locationVersion);
            o.J.WriteNumber("zone_water_level", zs.waterLevel);
            o.J.WriteNumber("zone_size", zs.zoneSize);
            o.J.WriteEndObject();

            if (names)
            {
                LocationDisplayNames table = data.DisplayNames;
                o.J.WriteStartObject("names");
                o.J.WriteString("language", table.Language);
                o.J.WriteStartArray("notes");
                foreach (string note in table.Notes) o.J.WriteStringValue(note);
                o.J.WriteEndArray();
                o.J.WriteStartArray("locations");
                foreach (LocationDisplayName n in table.All)
                {
                    o.J.WriteStartObject();
                    o.J.WriteString("prefab", n.Prefab);
                    if (n.DisplayName == null) o.J.WriteNull("display_name");
                    else o.J.WriteString("display_name", n.DisplayName);
                    if (n.DisplayName == null) o.J.WriteNull("display_name_source");
                    else o.J.WriteString("display_name_source", n.Source.ToString());
                    if (n.NameToken == null) o.J.WriteNull("name_token");
                    else o.J.WriteString("name_token", n.NameToken);
                    o.J.WriteString("provenance", n.Provenance);
                    o.J.WriteBoolean("derived_by_convention", n.IsDerivedByConvention);
                    o.J.WriteStartArray("aliases");
                    foreach (string alias in n.Aliases) o.J.WriteStringValue(alias);
                    o.J.WriteEndArray();
                    o.J.WriteEndObject();
                }

                o.J.WriteEndArray();
                o.J.WriteEndObject();
            }

            if (goldens)
            {
                o.J.WriteStartArray("goldens");
                foreach (WorldGenGoldenId id in data.Goldens.WorldGenIds())
                {
                    WorldGenDumpFile g = data.Goldens.WorldGen(id);
                    o.J.WriteStartObject();
                    o.J.WriteString("seed_hex", id.SeedHex);
                    o.J.WriteString("source", id.Source);
                    o.J.WriteNumber("seed", g.seed);
                    o.J.WriteString("seed_text", g.seedText);
                    o.J.WriteNumber("lakes", g.lakes?.Length ?? 0);
                    o.J.WriteNumber("rivers", g.rivers?.Length ?? 0);
                    o.J.WriteNumber("streams", g.streams?.Length ?? 0);
                    o.J.WriteNumber("river_points", g.riverPointTotal);
                    o.J.WriteEndObject();
                }

                o.J.WriteEndArray();
            }

            if (verify)
            {
                o.J.WriteStartArray("checks");
                foreach (Check c in results)
                {
                    o.J.WriteStartObject();
                    o.J.WriteString("name", c.Name);
                    o.J.WriteBoolean("passed", c.Passed);
                    o.J.WriteString("measured", c.Detail);
                    o.J.WriteEndObject();
                }

                o.J.WriteEndArray();
            }

            o.J.WriteEndObject();
        }

        private static bool LocationAnswersAllowed(StampCheck check)
        {
            if (check.Result == StampMatch.Match) return true;
            if (check.Result == StampMatch.Mismatch) return false;
            return !DataPolicy.RequireMatchRequested();
        }
    }
}
