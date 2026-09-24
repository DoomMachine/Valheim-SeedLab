using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using SeedLab.Cli.Infra;
using SeedLab.Saves;
using SeedLab.Seeds;

namespace SeedLab.Cli.Commands
{
    /// <summary>
    /// <c>vseed worlds</c> and <c>vseed world</c>. Both are strictly read-only: the tool opens save
    /// files with FileShare.Read and never writes into a save folder, because Steam Cloud syncs both
    /// <c>worlds\</c> and <c>worlds_local\</c> and the game deletes files it does not expect.
    /// </summary>
    public static class WorldsCommand
    {
        public const string Help = @"vseed worlds [options]

  Lists the Valheim worlds found on this machine. Read-only: nothing is ever written to a
  save folder or to Steam Cloud.

Options:
  --root <dir>    look under this save root instead of searching the usual places
  --json

Search order: SEEDLAB_SAVES_DIR, the game data folder (%USERPROFILE%\AppData\LocalLow\IronGate\Valheim
on Windows, ~/.config/unity3d/IronGate/Valheim on Linux, ~/Library/Application Support/... on macOS)
and the Steam Cloud 'remote' folders.";

        public static int Run(Args a, Out o)
        {
            string? root = a.Get("root");
            a.RejectUnknown();

            SteamProbe.EnsureSteamRoot();
            IReadOnlyList<ValheimSaveRoot> roots = SaveDiscovery.FindSaveRoots(root);
            List<(string Dir, string Name, WorldMeta? Meta, string? Error, bool Cache)> found =
                new List<(string, string, WorldMeta?, string?, bool)>();

            foreach (ValheimSaveRoot r in roots)
            {
                foreach (string dir in SaveDiscovery.WorldDirectories(r))
                {
                    string name = new DirectoryInfo(dir).Name;
                    WorldMeta? meta = null;
                    string? error = null;
                    try
                    {
                        WorldSaveSet? set = SaveDiscovery.ResolveNewestSaveSet(dir);
                        if (set?.FwlPath == null)
                        {
                            error = MinimapCacheReader.Exists(dir)
                                ? "map cache only - the save itself is elsewhere (Steam Cloud)"
                                : "no _main.N.fwl2";
                        }
                        else meta = WorldMetaReader.Read(set.FwlPath);
                    }
                    catch (Exception ex)
                    {
                        error = ex.Message;
                    }

                    found.Add((dir, name, meta, error, MinimapCacheReader.Exists(dir)));
                }
            }

            found = MergeByName(found);

            if (o.Json)
            {
                var j = o.J;
                j.WriteStartObject();
                j.WriteString("command", "worlds");
                j.WriteStartArray("roots");
                foreach (ValheimSaveRoot r in roots)
                {
                    j.WriteStartObject();
                    j.WriteString("path", r.Path);
                    j.WriteString("kind", r.Kind.ToString());
                    j.WriteEndObject();
                }

                j.WriteEndArray();
                j.WriteStartArray("worlds");
                foreach (var w in found)
                {
                    j.WriteStartObject();
                    j.WriteString("name", w.Name);
                    j.WriteString("directory", w.Dir);
                    if (w.Meta != null)
                    {
                        j.WriteString("seed_name", w.Meta.SeedName);
                        j.WriteNumber("seed", w.Meta.Seed);
                        j.WriteNumber("worldgen_version", w.Meta.WorldGenVersion);
                        j.WriteNumber("file_version", w.Meta.FileVersion);
                        j.WriteBoolean("seed_matches_seed_name", w.Meta.SeedMatchesSeedName);
                    }

                    if (w.Error != null) j.WriteString("error", w.Error);
                    j.WriteBoolean("has_minimap_cache", w.Cache);
                    j.WriteEndObject();
                }

                j.WriteEndArray();
                j.WriteEndObject();
                return found.Count > 0 ? ExitCodes.Ok : ExitCodes.NotFound;
            }

            o.Header("Save roots");
            if (roots.Count == 0) o.Note("none found - set SEEDLAB_SAVES_DIR or pass --root <dir>");
            foreach (ValheimSaveRoot r in roots) o.Note(r.Kind + "  " + r.Path);

            o.Header("Worlds  (" + found.Count + ")");
            if (found.Count == 0)
            {
                o.Note("no world folders under those roots");
                return ExitCodes.NotFound;
            }

            List<string[]> rows = new List<string[]>();
            foreach (var w in found)
            {
                rows.Add(new[]
                {
                    w.Name,
                    w.Meta?.SeedName ?? "-",
                    w.Meta?.Seed.ToString(CultureInfo.InvariantCulture) ?? "-",
                    w.Meta?.WorldGenVersion.ToString(CultureInfo.InvariantCulture) ?? "-",
                    w.Cache ? "yes" : "no",
                    w.Error ?? "",
                });
            }

            o.Table(new[] { "world", "seed text", "int32 seed", "gen", "map cache", "note" }, rows,
                    new[] { false, false, true, true, false, false });
            o.Line();
            o.Note("'map cache' is the minimap cache the game writes the first time the map is opened;");
            o.Note("it is a 4.19 M sample oracle for that world, which is what 'vseed selftest' compares against.");
            return ExitCodes.Ok;
        }

        /// <summary>
        /// One row per world name. The game always writes the minimap cache under
        /// <c>worlds_local\&lt;name&gt;\</c> even when the save itself lives in Steam Cloud, so the same
        /// world legitimately turns up in two folders: one with the cache and one with the .fwl2.
        /// Listing it twice would read as two worlds.
        /// </summary>
        private static List<(string Dir, string Name, WorldMeta? Meta, string? Error, bool Cache)> MergeByName(
            List<(string Dir, string Name, WorldMeta? Meta, string? Error, bool Cache)> rows)
        {
            Dictionary<string, int> index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            List<(string Dir, string Name, WorldMeta? Meta, string? Error, bool Cache)> merged =
                new List<(string, string, WorldMeta?, string?, bool)>();

            foreach (var r in rows)
            {
                if (!index.TryGetValue(r.Name, out int at))
                {
                    index[r.Name] = merged.Count;
                    merged.Add(r);
                    continue;
                }

                var e = merged[at];
                merged[at] = (
                    e.Meta != null ? e.Dir : (r.Meta != null ? r.Dir : e.Dir),
                    e.Name,
                    e.Meta ?? r.Meta,
                    (e.Meta ?? r.Meta) != null ? null : (e.Error ?? r.Error),
                    e.Cache || r.Cache);
            }

            return merged;
        }
    }

    /// <summary><c>vseed world &lt;name&gt;</c> - one save, read-only.</summary>
    public static class WorldCommand
    {
        public const string Help = @"vseed world <name-or-directory> [options]

  Reads one world save (read-only) and reports its seed, world-gen version, modifiers and
  what the save holds.

Options:
  --root <dir>    look under this save root instead of searching the usual places
  --locations     also count the location instances stored in the .db2 by prefab hash
  --json

Examples:
  vseed world asdasdasd
  vseed world ./groundtruth/worlds/testworldclaude --locations";

        public static int Run(Args a, Out o)
        {
            if (a.Positional.Count < 1) throw new CliException("give a world name or a world folder.", ExitCodes.Usage, Help);
            string want = a.Positional[0];
            string? root = a.Get("root");
            bool locations = a.Flag("locations");
            a.RejectUnknown();

            string? dir = ResolveWorldDirectory(want, root);
            if (dir == null)
            {
                throw new CliException($"no world called '{want}' was found.", ExitCodes.NotFound,
                    "run 'vseed worlds' to see what is on this machine, or pass the world folder directly");
            }

            WorldSave save;
            try
            {
                save = WorldSaveReader.OpenWorldDirectory(dir, new WorldSaveOptions { ReadDb = true });
            }
            catch (FileNotFoundException ex)
            {
                throw new CliException(ex.Message, ExitCodes.NotFound);
            }

            WorldMeta m = save.Meta;

            // Minimap.Start always builds the cache path from the LOCAL save directory, so a Steam
            // Cloud world's cache sits under worlds_local\<name>\ and not beside the save.
            string? cacheDir = MinimapCacheReader.Exists(dir)
                ? dir
                : SaveDiscovery.FindMinimapCacheDirectory(m.Name, SaveDiscovery.FindSaveRoots(root));
            MinimapCacheMeta? cacheMeta = null;
            if (cacheDir != null)
            {
                try { cacheMeta = MinimapCacheReader.ReadMeta(cacheDir); }
                catch (Exception ex) { Out.Warn("minimap cache unreadable: " + ex.Message); }
            }

            Dictionary<int, int> byPrefab = new Dictionary<int, int>();
            int placed = 0;
            if (save.Db != null)
            {
                foreach (LocationInstance li in save.Db.ZoneSystem.Locations)
                {
                    byPrefab.TryGetValue(li.PrefabHash, out int n);
                    byPrefab[li.PrefabHash] = n + 1;
                    if (li.Placed) placed++;
                }
            }

            if (o.Json)
            {
                var j = o.J;
                j.WriteStartObject();
                j.WriteString("command", "world");
                j.WriteString("name", m.Name);
                j.WriteString("directory", dir);
                j.WriteString("save_file", save.SaveSet.FwlPath ?? "");
                j.WriteNumber("save_number", save.SaveSet.SaveNumber);
                j.WriteString("seed_name", m.SeedName);
                j.WriteNumber("seed", m.Seed);
                j.WriteNumber("seed_from_seed_name", m.SeedFromSeedName);
                j.WriteBoolean("seed_matches_seed_name", m.SeedMatchesSeedName);
                j.WriteNumber("uid", m.Uid);
                j.WriteNumber("worldgen_version", m.WorldGenVersion);
                j.WriteNumber("file_version", m.FileVersion);
                j.WriteBoolean("needs_db", m.NeedsDb);
                j.WriteStartArray("starting_global_keys");
                foreach (string k in m.StartingGlobalKeys) j.WriteStringValue(k);
                j.WriteEndArray();
                j.WriteNumber("player_history_count", m.PlayerHistory.Count);
                if (save.Db != null)
                {
                    j.WriteStartObject("db");
                    j.WriteNumber("file_version", save.Db.FileVersion);
                    j.WriteNumber("net_time", save.Db.NetTime);
                    j.WriteNumber("generated_zones", save.Db.ZoneSystem.GeneratedZones.Count);
                    j.WriteNumber("location_version", save.Db.ZoneSystem.LocationVersion);
                    j.WriteBoolean("locations_generated", save.Db.ZoneSystem.LocationsGenerated);
                    j.WriteNumber("location_instances", save.Db.ZoneSystem.Locations.Count);
                    j.WriteNumber("location_instances_placed", placed);
                    j.WriteNumber("distinct_location_prefabs", byPrefab.Count);
                    j.WriteStartArray("global_keys");
                    foreach (string k in save.Db.ZoneSystem.GlobalKeys) j.WriteStringValue(k);
                    j.WriteEndArray();
                    j.WriteEndObject();
                }
                else
                {
                    j.WriteNull("db");
                }

                if (cacheMeta != null)
                {
                    j.WriteStartObject("minimap_cache");
                    j.WriteString("directory", cacheDir ?? "");
                    j.WriteNumber("seed", cacheMeta.Seed);
                    j.WriteNumber("cache_version", cacheMeta.CacheVersion);
                    j.WriteBoolean("matches_world_seed", cacheMeta.Seed == m.Seed);
                    j.WriteEndObject();
                }
                else
                {
                    j.WriteNull("minimap_cache");
                }

                if (locations)
                {
                    j.WriteStartArray("locations_by_prefab_hash");
                    foreach (KeyValuePair<int, int> kv in SortedByCount(byPrefab))
                    {
                        j.WriteStartObject();
                        j.WriteNumber("prefab_hash", kv.Key);
                        j.WriteNumber("count", kv.Value);
                        j.WriteEndObject();
                    }

                    j.WriteEndArray();
                }

                j.WriteStartArray("warnings");
                foreach (string w in save.Warnings) j.WriteStringValue(w);
                j.WriteEndArray();
                j.WriteEndObject();
                return ExitCodes.Ok;
            }

            o.Header("World  " + m.Name);
            o.Field("folder", dir);
            o.Field("save group", "_main." + save.SaveSet.SaveNumber + "   world file version " + m.FileVersion);
            o.Field("seed text", "\"" + m.SeedName + "\"");
            o.Field("int32 seed", m.Seed.ToString(CultureInfo.InvariantCulture)
                    + (m.SeedMatchesSeedName
                        ? "   (= GetStableHashCode of the seed text)"
                        : "   MISMATCH: the text hashes to " + m.SeedFromSeedName + "; generation follows the stored seed"));
            o.Field("world uid", m.Uid.ToString(CultureInfo.InvariantCulture));
            o.Field("worldGenVersion", m.WorldGenVersion.ToString(CultureInfo.InvariantCulture)
                    + (m.WorldGenVersion == Verified.WorldGenVersion ? "   (the version this build reproduces)"
                       : "   NOT the version this build reproduces (" + Verified.WorldGenVersion + ")"));
            o.Field("needs db", m.NeedsDb ? "yes" : "no");
            o.Field("players seen", m.PlayerHistory.Count.ToString(CultureInfo.InvariantCulture));

            o.Header("Modifiers  (m_startingGlobalKeys in the .fwl2)");
            if (m.StartingGlobalKeys.Count == 0) o.Note("none - default world settings");
            foreach (string k in m.StartingGlobalKeys) o.Note(k);
            o.Note("");
            o.Note("World generation ignores these: biomes and terrain follow the seed and worldGenVersion only.");

            if (save.Db != null)
            {
                o.Header("Save contents  (_main." + save.SaveSet.SaveNumber + ".db2)");
                o.Field("db file version", save.Db.FileVersion.ToString(CultureInfo.InvariantCulture));
                o.Field("net time", Out.F(save.Db.NetTime, 1) + " s");
                o.Field("generated zones", Out.N(save.Db.ZoneSystem.GeneratedZones.Count) + "   64 m zones the game has built");
                o.Field("locations", Out.N(save.Db.ZoneSystem.Locations.Count) + " instances, "
                        + Out.N(placed) + " placed, " + Out.N(byPrefab.Count) + " distinct prefabs");
                o.Field("locations generated", (save.Db.ZoneSystem.LocationsGenerated ? "yes" : "no")
                        + "   (location version " + save.Db.ZoneSystem.LocationVersion + ")");
                o.Field("global keys", save.Db.ZoneSystem.GlobalKeys.Count == 0
                        ? "none" : string.Join(", ", save.Db.ZoneSystem.GlobalKeys));
                o.Note("");
                o.Note("The .db2 global keys are the live set with server-option keys filtered out -");
                o.Note("a different list from the .fwl2 starting keys above.");
            }
            else
            {
                o.Header("Save contents");
                o.Note("no .db2 beside the .fwl2");
            }

            o.Header("Minimap cache");
            if (cacheMeta == null)
            {
                o.Note("none - the game writes it the first time the map is opened in this world");
            }
            else
            {
                o.Field("seed in cache", cacheMeta.Seed.ToString(CultureInfo.InvariantCulture)
                        + (cacheMeta.Seed == m.Seed ? "   (matches)" : "   STALE - does not match this world"));
                o.Field("cache version", cacheMeta.CacheVersion.ToString(CultureInfo.InvariantCulture));
                o.Field("cache folder", cacheDir ?? "");
                o.Note("4,194,304 biome and height samples the game itself computed - usable as an oracle.");
            }

            if (locations && byPrefab.Count > 0)
            {
                o.Header("Location instances by prefab hash");
                List<string[]> rows = new List<string[]>();
                foreach (KeyValuePair<int, int> kv in SortedByCount(byPrefab))
                {
                    rows.Add(new[] { kv.Key.ToString(CultureInfo.InvariantCulture), Out.N(kv.Value) });
                }

                o.Table(new[] { "prefab hash", "count" }, rows, new[] { true, true });
                o.Note("");
                o.Note("Hashes, not names: the save stores only the hash. Names come from the game's own log.");
            }

            foreach (string w in save.Warnings) Out.Warn(w);
            o.Line();
            return ExitCodes.Ok;
        }

        private static IEnumerable<KeyValuePair<int, int>> SortedByCount(Dictionary<int, int> d)
        {
            List<KeyValuePair<int, int>> l = new List<KeyValuePair<int, int>>(d);
            l.Sort((x, y) => y.Value != x.Value ? y.Value.CompareTo(x.Value) : x.Key.CompareTo(y.Key));
            return l;
        }

        internal static string? ResolveWorldDirectory(string want, string? root)
        {
            if (Directory.Exists(want) && Directory.GetFiles(want, "_main.*.fwl2").Length > 0)
            {
                return Path.GetFullPath(want);
            }

            SteamProbe.EnsureSteamRoot();
            foreach (ValheimSaveRoot r in SaveDiscovery.FindSaveRoots(root))
            {
                foreach (string dir in SaveDiscovery.WorldDirectories(r))
                {
                    // A world folder that holds only the minimap cache (the game always writes that
                    // under worlds_local, even for a Steam Cloud world) is not the save; keep looking.
                    if (string.Equals(new DirectoryInfo(dir).Name, want, StringComparison.OrdinalIgnoreCase)
                        && Directory.GetFiles(dir, "_main.*.fwl2").Length > 0)
                    {
                        return dir;
                    }
                }
            }

            // The bundled ground-truth worlds, so the command works straight out of the repository.
            string? gt = Verified.FindGroundTruth();
            if (gt != null)
            {
                string cand = Path.Combine(gt, "worlds", want);
                if (Directory.Exists(cand)) return cand;
            }

            return null;
        }
    }
}
