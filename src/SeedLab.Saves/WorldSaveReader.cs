using System;
using System.Collections.Generic;
using System.IO;

namespace SeedLab.Saves
{
    /// <summary>Everything read from one world's save group, plus its minimap cache if one exists.</summary>
    public sealed class WorldSave
    {
        public WorldSave(WorldSaveSet saveSet, WorldMeta meta, WorldDb? db, ChunkMapping? chunks,
                         MinimapCache? minimapCache, IReadOnlyList<string> warnings)
        {
            SaveSet = saveSet;
            Meta = meta;
            Db = db;
            Chunks = chunks;
            MinimapCache = minimapCache;
            Warnings = warnings;
        }

        public WorldSaveSet SaveSet { get; }
        public WorldMeta Meta { get; }

        /// <summary>Null when the group has no <c>.db2</c> or the caller asked not to read it.</summary>
        public WorldDb? Db { get; }

        public ChunkMapping? Chunks { get; }

        /// <summary>
        /// Null when no cache has been generated for this world - the game writes it the first time
        /// the map is opened, so a world that has never been played has none.
        /// </summary>
        public MinimapCache? MinimapCache { get; }

        /// <summary>Non-fatal problems: a missing <c>.ok</c>, an unparsable save number, a stale cache.</summary>
        public IReadOnlyList<string> Warnings { get; }
    }

    /// <summary>Options for <see cref="WorldSaveReader"/>.</summary>
    public sealed class WorldSaveOptions
    {
        /// <summary>Read the <c>.db2</c> (the location instances). Default true.</summary>
        public bool ReadDb { get; set; } = true;

        /// <summary>Read the <c>.chunks</c> index. Default false - nothing in generation needs it.</summary>
        public bool ReadChunks { get; set; }

        /// <summary>
        /// Read the minimap cache when one can be found. Default false: it is about 25 MB inflated
        /// per world, so it is opt-in.
        /// </summary>
        public bool ReadMinimapCache { get; set; }

        /// <summary>
        /// Where to look for the cache. The cache is always under the game data directory's
        /// <c>worlds_local</c>, even for a Steam Cloud world, so this normally needs the roots from
        /// <see cref="SaveDiscovery.FindSaveRoots"/> rather than the world's own folder.
        /// </summary>
        public IReadOnlyList<ValheimSaveRoot>? CacheSearchRoots { get; set; }

        public MinimapCacheOptions? MinimapOptions { get; set; }
    }

    /// <summary>
    /// Opens a world save group as one object. <b>Read-only</b>; see <see cref="IValheimReader"/>.
    /// <para>
    /// The <c>.fwl2</c> is required - it holds the seed, and without it nothing else is meaningful.
    /// Everything else is optional and its absence is a warning, not an error.
    /// </para>
    /// </summary>
    public sealed class WorldSaveReader : IValheimReader
    {
        public static WorldSave Open(WorldSaveSet saveSet, WorldSaveOptions? options = null)
        {
            options ??= new WorldSaveOptions();
            List<string> warnings = new List<string>();

            string fwlPath = saveSet.FwlPath ?? throw new FileNotFoundException(
                "World save group _main." + saveSet.SaveNumber + " in " + saveSet.Directory +
                " has no .fwl2, so it has no seed.");

            if (!saveSet.IsComplete)
                warnings.Add("Save group _main." + saveSet.SaveNumber + " is incomplete (" +
                             (saveSet.FwlPath == null ? "" : "fwl2 ") +
                             (saveSet.DbPath == null ? "" : "db2 ") +
                             (saveSet.ChunksPath == null ? "" : "chunks ") +
                             (saveSet.OkPath == null ? "" : "ok ") + "present).");

            WorldMeta meta = WorldMetaReader.Read(fwlPath);
            if (!meta.SeedMatchesSeedName)
                warnings.Add("Stored seed " + meta.Seed + " is not GetStableHashCode(\"" + meta.SeedName +
                             "\") = " + meta.SeedFromSeedName + "; generation follows the stored seed.");

            WorldDb? db = null;
            if (options.ReadDb && saveSet.DbPath != null) db = WorldDbReader.Read(saveSet.DbPath);

            ChunkMapping? chunks = null;
            if (options.ReadChunks && saveSet.ChunksPath != null) chunks = ChunkMappingReader.Read(saveSet.ChunksPath);

            MinimapCache? cache = null;
            if (options.ReadMinimapCache)
            {
                string? dir = null;
                if (options.CacheSearchRoots != null)
                    dir = SaveDiscovery.FindMinimapCacheDirectory(saveSet.WorldName, options.CacheSearchRoots);

                // A fixture folder often holds the cache beside the save; try that too.
                if (dir == null && MinimapCacheReader.Exists(saveSet.Directory)) dir = saveSet.Directory;

                if (dir == null)
                {
                    warnings.Add("No minimap cache found for world \"" + saveSet.WorldName +
                                 "\"; the game writes it the first time the map is opened.");
                }
                else
                {
                    MinimapCacheOptions cacheOptions = options.MinimapOptions ?? new MinimapCacheOptions();
                    // The game's own staleness test: the cache is only valid for this world's seed.
                    cacheOptions.ExpectedSeed ??= meta.Seed;
                    cache = MinimapCacheReader.Read(dir, cacheOptions);
                }
            }

            return new WorldSave(saveSet, meta, db, chunks, cache, warnings);
        }

        /// <summary>
        /// Resolves the newest group in a world folder and opens it. Re-resolves every call, because
        /// the save number moves while the user plays.
        /// </summary>
        public static WorldSave OpenWorldDirectory(string worldDirectory, WorldSaveOptions? options = null)
        {
            List<string> warnings = new List<string>();
            WorldSaveSet? set = SaveDiscovery.ResolveNewestSaveSet(worldDirectory, warnings);
            if (set == null)
                throw new FileNotFoundException("No _main.N.fwl2 in " + worldDirectory + ".");

            WorldSave save = Open(set, options);
            if (warnings.Count == 0) return save;

            List<string> merged = new List<string>(warnings);
            merged.AddRange(save.Warnings);
            return new WorldSave(save.SaveSet, save.Meta, save.Db, save.Chunks, save.MinimapCache, merged);
        }
    }
}
