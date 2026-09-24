namespace SeedLab.Contracts.Dump
{
    /// <summary>
    /// <c>manifest.json</c>. Identifies the exact build the data came from, so the offline tool can
    /// refuse to use stale data after a game update instead of producing plausible nonsense.
    /// The <see cref="stamp"/> string is repeated as the first property of every other file in the
    /// dump, so a file lifted out of the folder is still self-identifying.
    /// </summary>
    public sealed class DumpManifest
    {
        public string? stamp;
        public int schema;

        public GameInfoDef? game;
        public DumperInfoDef? dumper;
        public WorldInfoDef? world;
        public CountsDef? counts;

        /// <summary>True when two <c>LocationList</c>s share an <c>m_sortOrder</c>; see
        /// <see cref="ZoneSystemConstantsDef.sortOrderTies"/>.</summary>
        public bool sortOrderTies;

        /// <summary>Every file written by this dump, with its size and SHA-256. A hash mismatch means a
        /// tampered or partial dump and the tool must hard-error rather than continue.</summary>
        public FileEntryDef[]? files;

        /// <summary>Anything the operator needs to know: refusals, partial results, warnings.</summary>
        public string[]? notes;
    }

    public sealed class GameInfoDef
    {
        /// <summary><c>Version.GetVersionString()</c>, e.g. "1.0.15".</summary>
        public string? version;

        /// <summary><c>Version.c_networkVersion</c>.</summary>
        public int networkVersion;

        /// <summary><c>Application.unityVersion</c>.</summary>
        public string? unityVersion;

        /// <summary>SHA-256 of <c>valheim_Data\Managed\assembly_valheim.dll</c> - the same artifact
        /// <c>check-game-version.ps1</c> hashes, so the two stamps can be compared by eye.</summary>
        public string? assemblyValheimSha256;

        /// <summary>SHA-256 of <c>UnityPlayer.dll</c>. The natives (PerlinNoise, Random, FloatToHalf)
        /// live there, so a change here invalidates the goldens even if the game code is unchanged.</summary>
        public string? unityPlayerSha256;

        public long assemblyValheimBytes;
        public long unityPlayerBytes;

        /// <summary>The Steam build id, if the plugin could read <c>steam_appid</c>/manifest data;
        /// null otherwise. The two SHA-256s are the authority, not this.</summary>
        public string? steamBuild;
    }

    public sealed class DumperInfoDef
    {
        public string? version;

        /// <summary>"assets", "natives" or "worldgen".</summary>
        public string? mode;

        public string? utc;

        /// <summary>The BepInEx plugin GUID, so a third-party rebuild is distinguishable.</summary>
        public string? guid;
    }

    /// <summary>The world that was loaded when the dump ran, or null for a menu-mode dump.</summary>
    public sealed class WorldInfoDef
    {
        public string? name;
        public string? seedText;
        public int seed;
        public int worldGenVersion;

        /// <summary><c>Version.World</c> of the loaded world (41 = DeepNorth for this build).</summary>
        public int worldVersion;

        /// <summary><c>ZoneSystem.m_locationVersion</c> at dump time.</summary>
        public int locationVersion;

        public bool menu;
    }

    public sealed class CountsDef
    {
        /// <summary><c>ZoneSystem.m_locations.Count</c> after SetupLocations. MEASURED, never inferred -
        /// it is not the 213 location prefabs in the SoftRef manifest, and it is not 86 either.</summary>
        public int locations;

        /// <summary>Entries with <c>m_enable &amp;&amp; m_quantity != 0</c> - what the placement loop walks.
        /// The game's own log line says 183 for this build.</summary>
        public int locationsEnabled;

        public int vegetation;
        public int vegetationEnabled;
        public int altBiomes;
        public int altBiomesEnabled;
        public int locationLists;
        public int altBiomeLists;

        /// <summary>Distinct <c>ZoneLocation.Hash</c> values. Fewer than <see cref="locations"/> means
        /// a duplicate, which <c>SetupLocations</c> logs and then ignores.</summary>
        public int distinctLocationHashes;

        public int locationInstances;
        public int locationInstancesPlaced;
    }

    public sealed class FileEntryDef
    {
        /// <summary>Path relative to the dump folder, with forward slashes.</summary>
        public string? path;

        public long bytes;
        public string? sha256;
    }
}
