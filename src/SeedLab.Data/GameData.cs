using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json.Serialization.Metadata;
using SeedLab.Contracts.Dump;

namespace SeedLab.Data
{
    /// <summary>
    /// The game data SeedLab ships: the tables <c>tools\SeedLab.Dumper</c> read out of a running
    /// Valheim, loaded strictly and cached.
    ///
    /// <para>Nothing here is a code default. Every number is a measurement made inside the game
    /// (<c>Minimap.m_textureSize</c> is 256 in the IL and 2048 in the prefab;
    /// <c>ZoneSystem.m_locationVersion</c> is 1 in the IL and 32 in the prefab), which is why a
    /// missing field is an error rather than a default - see <see cref="StrictJson"/>.</para>
    ///
    /// <para>Loading is lazy per file and cached per directory, so a command that only needs the
    /// stamp never parses the 3.8 MB location-instance golden, and a search that asks for the
    /// location table 4 billion times parses it once.</para>
    /// </summary>
    public sealed class GameData
    {
        private readonly object _gate = new object();
        private readonly Dictionary<string, string> _sha = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private LocationTableFile? _locations;
        private VegetationTableFile? _vegetation;
        private AltBiomeTableFile? _altBiomes;
        private LocationPrefabsFile? _locationPrefabs;
        private PrefabConstantsFile? _prefabConstants;
        private VersionConstantsFile? _versionConstants;
        private SeedInputFile? _seedInput;
        private LocalizationFile? _localization;
        private LocationOccupantsFile? _occupants;
        private RoomOccupantsFile? _roomOccupants;
        private LocationDisplayNames? _displayNames;
        private Goldens? _goldens;
        private StampCheck? _check;
        private IReadOnlyList<LocationDef>? _enabled;
        private IReadOnlyList<LocationDef>? _placementOrder;
        private Dictionary<string, LocationDef>? _byPrefabName;

        private GameData(string directory, DumpManifest manifest, DataStamp stamp)
        {
            Directory = directory;
            Manifest = manifest;
            Stamp = stamp;

            if (manifest.files != null)
            {
                foreach (FileEntryDef f in manifest.files)
                {
                    if (f.path != null && f.sha256 != null) _sha[f.path] = f.sha256;
                }
            }
        }

        // ---- locating and opening -----------------------------------------------------------------

        /// <summary>Points the tool at a <c>data\</c> folder, or straight at one dump folder inside it.</summary>
        public const string DirectoryEnvironmentVariable = "SEEDLAB_DATA_DIR";

        private static GameData? s_default;
        private static readonly object s_defaultGate = new object();

        /// <summary>The shipped data, found and opened once per process.</summary>
        public static GameData Load()
        {
            if (s_default != null) return s_default;
            lock (s_defaultGate)
            {
                return s_default ??= Open(FindDumpDirectory());
            }
        }

        /// <summary>Opens a specific dump folder (the one that holds <c>manifest.json</c>).</summary>
        public static GameData Open(string dumpDirectory)
        {
            if (dumpDirectory == null) throw new ArgumentNullException(nameof(dumpDirectory));

            string manifestPath = Path.Combine(dumpDirectory, DumpFormat.ManifestFile);
            if (!File.Exists(manifestPath))
            {
                throw new GameDataException(
                    dumpDirectory + ": there is no " + DumpFormat.ManifestFile + " here, so this is not a "
                    + "SeedLab data folder. " + StrictJson.ReDumpHint)
                { File = manifestPath };
            }

            // The manifest cannot check its own SHA-256 - it is the thing that holds them.
            DumpManifest manifest = StrictJson.Load(
                manifestPath, DumpSchemas.DumpManifest, DumpJsonContext.Default.DumpManifest, null);

            DataStamp stamp = DataStamp.Parse(
                manifest.stamp ?? throw new GameDataException(manifestPath + ": the manifest has no stamp."),
                manifestPath);

            if (manifest.schema != DumpFormat.Schema)
            {
                throw new GameDataException(
                    manifestPath + ": the dump declares schema " + manifest.schema.ToString(CultureInfo.InvariantCulture)
                    + " and this build of SeedLab reads schema " + DumpFormat.Schema.ToString(CultureInfo.InvariantCulture)
                    + ". A schema bump means a field changed MEANING, so the data cannot be read as if it had not. "
                    + StrictJson.ReDumpHint)
                { File = manifestPath };
            }

            return new GameData(dumpDirectory, manifest, stamp);
        }

        /// <summary>
        /// Finds the dump folder: <c>SEEDLAB_DATA_DIR</c> (either a dump folder or a <c>data\</c>
        /// folder), then a <c>data\</c> folder found by walking up from the working directory and from
        /// the binary - so the tool behaves the same under <c>dotnet run</c> and as a published exe.
        ///
        /// <para>When a <c>data\</c> folder holds several dumps, the one whose stamp matches the
        /// installed game wins; with no install and no single candidate it refuses rather than
        /// choosing, because picking the wrong build silently is the whole failure this assembly
        /// exists to prevent.</para>
        /// </summary>
        public static string FindDumpDirectory()
        {
            string? env = Environment.GetEnvironmentVariable(DirectoryEnvironmentVariable);
            if (!string.IsNullOrEmpty(env))
            {
                if (File.Exists(Path.Combine(env, DumpFormat.ManifestFile))) return env;
                string? fromEnv = PickFrom(env);
                if (fromEnv != null) return fromEnv;

                throw new GameDataException(
                    DirectoryEnvironmentVariable + " is set to '" + env + "', which holds neither "
                    + DumpFormat.ManifestFile + " nor a dump folder that does.");
            }

            foreach (string start in new[] { SafeCurrentDirectory(), AppContext.BaseDirectory })
            {
                if (start.Length == 0) continue;
                DirectoryInfo? d = new DirectoryInfo(start);
                for (int i = 0; i < 12 && d != null; i++, d = d.Parent)
                {
                    string cand = Path.Combine(d.FullName, "data");
                    string? picked = PickFrom(cand);
                    if (picked != null) return picked;
                }
            }

            throw new GameDataException(
                "SeedLab's game data was not found. It ships in the repository's data\\<version>-<hash>\\ "
                + "folder; set " + DirectoryEnvironmentVariable + " to point at it, or run the tool from "
                + "inside the SeedLab tree. " + StrictJson.ReDumpHint);
        }

        private static string? PickFrom(string dataDirectory)
        {
            List<string> dumps = new List<string>();
            try
            {
                if (!System.IO.Directory.Exists(dataDirectory)) return null;
                foreach (string d in System.IO.Directory.GetDirectories(dataDirectory))
                {
                    if (File.Exists(Path.Combine(d, DumpFormat.ManifestFile))) dumps.Add(d);
                }
            }
            catch (Exception)
            {
                return null;
            }

            if (dumps.Count == 0) return null;
            if (dumps.Count == 1) return dumps[0];

            string? gameDir = GameInstall.FindDirectory();
            if (gameDir != null)
            {
                string installed;
                try { installed = GameInstall.Sha256File(GameInstall.AssemblyPath(gameDir)); }
                catch (Exception) { installed = ""; }

                if (installed.Length == 64)
                {
                    string wanted = installed.Substring(0, 8);
                    foreach (string d in dumps)
                    {
                        string name = Path.GetFileName(d);
                        if (name.EndsWith("-" + wanted, StringComparison.OrdinalIgnoreCase)) return d;
                    }
                }
            }

            dumps.Sort(StringComparer.OrdinalIgnoreCase);
            throw new GameDataException(
                dataDirectory + " holds " + dumps.Count.ToString(CultureInfo.InvariantCulture)
                + " dumps and none of them matches the installed game: "
                + string.Join(", ", dumps.ConvertAll(Path.GetFileName))
                + ". Set " + DirectoryEnvironmentVariable + " to the one you mean.");
        }

        // ---- identity ------------------------------------------------------------------------------

        /// <summary>The dump folder these tables were read from.</summary>
        public string Directory { get; }

        public DumpManifest Manifest { get; }

        /// <summary>Which Valheim build this data came out of.</summary>
        public DataStamp Stamp { get; }

        /// <summary>The stamp compared against the installed game. Computed once, then cached.</summary>
        public StampCheck InstalledGameCheck
        {
            get
            {
                if (_check != null) return _check;
                lock (_gate) { return _check ??= GameInstall.Check(Stamp); }
            }
        }

        /// <summary>
        /// <see cref="InstalledGameCheck"/> if something has already asked for it, else null - for a
        /// report that must not hash <c>assembly_valheim.dll</c> a second time just to say so.
        /// </summary>
        public StampCheck? InstalledGameCheckIfDone => _check;

        /// <summary>The data <see cref="Load"/> opened in this process, or null when nothing has asked for it.</summary>
        public static GameData? Loaded => s_default;

        // ---- what this process has verified ----------------------------------------------------------

        private static readonly object s_verifiedGate = new object();
        private static readonly List<string> s_verified = new List<string>();
        private static readonly HashSet<string> s_verifiedSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// How many data files this process has read AND found identical to the SHA-256 recorded for
        /// them in <c>manifest.json</c> (2026-09-24). Files are verified lazily, on first use, so this
        /// is what the command actually relied on - not the whole dump - and it is counted as the check
        /// happens, so a report of it hashes nothing again. A file that failed the check never counts:
        /// its load threw.
        /// </summary>
        public static int VerifiedFileCount
        {
            get
            {
                lock (s_verifiedGate) return s_verified.Count;
            }
        }

        /// <summary>The files behind <see cref="VerifiedFileCount"/>, full paths, in the order they were checked.</summary>
        public static IReadOnlyList<string> VerifiedFiles
        {
            get
            {
                lock (s_verifiedGate) return s_verified.ToArray();
            }
        }

        /// <summary>Called by the loaders when a file's bytes matched the manifest's SHA-256.</summary>
        internal static void NoteVerified(string path)
        {
            string full;
            try
            {
                full = Path.GetFullPath(path);
            }
            catch (Exception)
            {
                full = path;
            }

            lock (s_verifiedGate)
            {
                if (s_verifiedSet.Add(full)) s_verified.Add(full);
            }
        }

        /// <summary>Gate for anything that reads the asset tables. See <see cref="DataPolicy"/>.</summary>
        public void RequireUsableForAssetData(string whatWasAsked)
            => DataPolicy.RequireMatchForAssetData(InstalledGameCheck, whatWasAsked);

        // ---- the tables ----------------------------------------------------------------------------

        /// <summary>
        /// <c>ZoneSystem.m_locations</c> after <c>SetupLocations</c>, IN ORDER and complete - 232
        /// entries for this build, including the disabled ones and the 16 injected by an
        /// <c>AltBiome.m_addLocations</c>. The order is semantic: it decides everything downstream.
        /// </summary>
        public IReadOnlyList<LocationDef> Locations => LocationFile().locations!;

        /// <summary><c>ZoneSystem.m_vegetation</c>, in order.</summary>
        public IReadOnlyList<VegetationDef> Vegetation => VegetationFile().vegetation!;

        /// <summary><c>AltBiomeList.m_altBiomes</c>, in order.</summary>
        public IReadOnlyList<AltBiomeDef> AltBiomes => AltBiomeFile().altBiomes!;

        /// <summary>The <c>Location</c> component and <c>DungeonGenerator</c> data of each location prefab.</summary>
        public IReadOnlyList<LocationPrefabDef> LocationPrefabs => LocationPrefabFile().prefabs!;

        /// <summary>Serialized prefab scalars: minimap geometry and colours, zone system, heightmap.</summary>
        public PrefabConstantsFile PrefabConstants => Cached(
            ref _prefabConstants, DumpFormat.PrefabConstantsFile, DumpSchemas.PrefabConstantsFile,
            DumpJsonContext.Default.PrefabConstantsFile, f => f.stamp);

        /// <summary>Version numbers and the alt-biome grid geometry, as consts in the running game.</summary>
        public VersionConstantsFile VersionConstants => Cached(
            ref _versionConstants, DumpFormat.VersionConstantsFile, DumpSchemas.VersionConstantsFile,
            DumpJsonContext.Default.VersionConstantsFile, f => f.stamp);

        /// <summary>What the new-world UI accepts as a seed text (characterLimit 10, Alphanumeric).</summary>
        public SeedInputFile SeedInput => Cached(
            ref _seedInput, DumpFormat.SeedInputFile, DumpSchemas.SeedInputFile,
            DumpJsonContext.Default.SeedInputFile, f => f.stamp);

        // ---- the naming tables -----------------------------------------------------------------------
        //
        // These three are the expensive ones and they are deliberately NOT reachable from anything on
        // the per-seed path. localization.json is 0.4 MB, locationchildren.json 29.9 MB and
        // roomchildren.json 74.5 MB, and each is read, SHA-256'd against the manifest, structurally
        // validated and deserialized on first touch. They answer "what is this place CALLED", which is
        // a question asked once per command, never once per zone: `vseed locations <seed>` spends its
        // time in world generation and placement, and a name lookup that joined that loop would be the
        // single most expensive thing in it. Lazy and cached per process, like every other table here.

        /// <summary>
        /// <c>Localization.m_translations</c> as dumped, plus the language it was dumped in. Read
        /// <see cref="Localize"/> rather than this when all that is wanted is one token.
        /// </summary>
        public LocalizationFile Localization => LocalizationTable();

        /// <summary>
        /// The occupant slice of <c>locationchildren.json</c> - 186 entries, one per location prefab,
        /// carrying only the five arrays a name can come out of. See
        /// <see cref="LocationOccupantsFile"/> for why this is a trimmed DTO and not the full one.
        /// </summary>
        public IReadOnlyList<LocationOccupantsDef> LocationOccupants => OccupantFile().locations!;

        /// <summary>
        /// The occupant slice of <c>roomchildren.json</c> - 358 entries. It is in the naming path for
        /// exactly one row: the Mistlands queen's offering bowl is in a ROOM prefab, so the location
        /// walk alone finds 7 of the 8 bosses. See <see cref="RoomOccupantsFile"/>.
        /// </summary>
        public IReadOnlyList<RoomOccupantsDef> RoomOccupants => RoomOccupantFile().rooms!;

        /// <summary>
        /// Prefab to player-facing name, derived once per process. Touching it loads
        /// <see cref="Localization"/> and <see cref="LocationOccupants"/> - about 180 ms cold, nothing
        /// afterwards - and deliberately NOT <see cref="RoomOccupants"/>; see
        /// <see cref="LocationDisplayNames"/> for the measurement behind that.
        /// </summary>
        public LocationDisplayNames DisplayNames
        {
            get
            {
                if (_displayNames != null) return _displayNames;
                lock (_gate) { return _displayNames ??= LocationDisplayNames.Build(this); }
            }
        }

        /// <summary>
        /// One localization token through the dumped table.
        ///
        /// <para>A string that starts with <c>'$'</c> is a token and is looked up without it - the game
        /// stores <c>m_translations</c> keyed WITHOUT the leading <c>$</c> (verified against
        /// <c>localization.json</c>, 6,258 entries, no key begins with <c>$</c>). A token with no entry
        /// comes back <b>null</b>, never decorated and never echoed: the whole point of dumping the raw
        /// table instead of calling <c>Localization.Localize</c> is that "this has no name" and "this is
        /// named '$enemy_gdking'" must stay distinguishable.</para>
        ///
        /// <para>Anything that does not start with <c>'$'</c> is already literal text and is returned
        /// unchanged, so a caller holding a field that is sometimes a token and sometimes a plain string
        /// does not have to test which it has. Null in, null out.</para>
        ///
        /// <para>When the dump records <see cref="LocalizationFile.skipped"/> the table is empty and
        /// every token resolves to null. That is the honest answer for such a dump, and a consumer that
        /// wants to say so reads <c>Localization.skipped</c>.</para>
        /// </summary>
        public string? Localize(string? tokenOrLiteral)
        {
            if (tokenOrLiteral == null || tokenOrLiteral.Length == 0 || tokenOrLiteral[0] != '$')
            {
                return tokenOrLiteral;
            }

            return LocalizationTable().translations!.TryGetValue(tokenOrLiteral.Substring(1), out string? text)
                ? text
                : null;
        }

        /// <summary>The recorded outputs of the running game, for checking the port against.</summary>
        public Goldens Goldens
        {
            get
            {
                if (_goldens != null) return _goldens;
                lock (_gate) { return _goldens ??= new Goldens(this); }
            }
        }

        // ---- derived views -------------------------------------------------------------------------

        /// <summary>The entries with <c>m_enable</c> - 186 for this build. NOT the placement list.</summary>
        public IReadOnlyList<LocationDef> EnabledLocations
        {
            get
            {
                if (_enabled != null) return _enabled;
                lock (_gate)
                {
                    if (_enabled != null) return _enabled;
                    List<LocationDef> list = new List<LocationDef>();
                    foreach (LocationDef d in Locations)
                    {
                        if (d.enable) list.Add(d);
                    }

                    return _enabled = list;
                }
            }
        }

        /// <summary>
        /// The list the placement run actually walks, in its order:
        /// <c>m_locations.Where(m_enable &amp;&amp; m_quantity != 0).OrderByDescending(m_prioritized)</c>
        /// (ZoneSystem.cs:1862-1876) - 183 entries for this build. The LINQ ordering is STABLE, and the
        /// dumper recorded the resulting position as <c>orderedIndex</c>, so this sorts by that
        /// recorded index rather than re-deriving a tie-break.
        /// </summary>
        public IReadOnlyList<LocationDef> PlacementOrder
        {
            get
            {
                if (_placementOrder != null) return _placementOrder;
                lock (_gate)
                {
                    if (_placementOrder != null) return _placementOrder;

                    List<LocationDef> list = new List<LocationDef>();
                    foreach (LocationDef d in Locations)
                    {
                        if (!d.enable || d.quantity == 0) continue;
                        if (d.orderedIndex < 0)
                        {
                            throw new GameDataException(
                                LocationPath + ": '" + (d.prefabName ?? "?") + "' is enabled with quantity "
                                + d.quantity.ToString(CultureInfo.InvariantCulture)
                                + " but its orderedIndex is -1, which says the game excluded it. The dump "
                                + "is internally inconsistent. " + StrictJson.ReDumpHint)
                            { File = LocationPath, Field = "orderedIndex" };
                        }

                        list.Add(d);
                    }

                    list.Sort((a, b) => a.orderedIndex.CompareTo(b.orderedIndex));

                    for (int i = 0; i < list.Count; i++)
                    {
                        if (list[i].orderedIndex == i) continue;
                        throw new GameDataException(
                            LocationPath + ": the recorded orderedIndex values are not 0.."
                            + (list.Count - 1).ToString(CultureInfo.InvariantCulture)
                            + " without gaps - entry " + i.ToString(CultureInfo.InvariantCulture) + " is "
                            + list[i].orderedIndex.ToString(CultureInfo.InvariantCulture) + ". "
                            + StrictJson.ReDumpHint)
                        { File = LocationPath, Field = "orderedIndex" };
                    }

                    return _placementOrder = list;
                }
            }
        }

        /// <summary>Entries with a non-zero quantity, enabled or not - 200 for this build.</summary>
        public int QuantityPositiveCount
        {
            get
            {
                int n = 0;
                foreach (LocationDef d in Locations)
                {
                    if (d.quantity != 0) n++;
                }

                return n;
            }
        }

        /// <summary>
        /// The entry whose <c>m_prefab.Name</c> is <paramref name="prefabName"/> - the string whose
        /// hash seeds that location's RNG stream and which the .db2 stores.
        /// </summary>
        public LocationDef? LocationByPrefabName(string prefabName)
        {
            if (prefabName == null) throw new ArgumentNullException(nameof(prefabName));

            if (_byPrefabName == null)
            {
                lock (_gate)
                {
                    if (_byPrefabName == null)
                    {
                        Dictionary<string, LocationDef> map =
                            new Dictionary<string, LocationDef>(StringComparer.Ordinal);
                        foreach (LocationDef d in Locations)
                        {
                            if (d.prefabName == null) continue;

                            // SetupLocations logs an error and ignores a later duplicate, so keep the first.
                            if (!map.ContainsKey(d.prefabName)) map.Add(d.prefabName, d);
                        }

                        _byPrefabName = map;
                    }
                }
            }

            return _byPrefabName.TryGetValue(prefabName, out LocationDef? found) ? found : null;
        }

        // ---- file plumbing ---------------------------------------------------------------------------

        internal string LocationPath => Path.Combine(Directory, DumpFormat.LocationsFile);

        internal string PathOf(string relative) => Path.Combine(Directory, relative.Replace('/', Path.DirectorySeparatorChar));

        internal string? ShaOf(string relative) => _sha.TryGetValue(relative, out string? s) ? s : null;

        internal T LoadFile<T>(string relative, DumpSchema schema, JsonTypeInfo<T> info) where T : class
            => StrictJson.Load(PathOf(relative), schema, info, ShaOf(relative));

        private T Cached<T>(ref T? slot, string relative, DumpSchema schema, JsonTypeInfo<T> info,
                            Func<T, string?> stampOf) where T : class
        {
            if (slot != null) return slot;
            lock (_gate)
            {
                if (slot != null) return slot;
                T loaded = LoadFile(relative, schema, info);
                CheckStamp(stampOf(loaded), relative);
                return slot = loaded;
            }
        }

        /// <summary>Every file repeats the stamp; a file from another build in this folder is an error.</summary>
        internal void CheckStamp(string? fileStamp, string relative)
        {
            string path = PathOf(relative);
            DataStamp s = DataStamp.Parse(
                fileStamp ?? throw new GameDataException(path + ": the file has no DATA-STAMP.") { File = path },
                path);

            if (s.SameBuild(Stamp)) return;

            throw new GameDataException(
                path + ": this file was dumped from a different game build than " + DumpFormat.ManifestFile
                + " (" + s.AssemblyValheimSha256.Substring(0, 16) + "... vs "
                + Stamp.AssemblyValheimSha256.Substring(0, 16) + "...). The folder mixes two dumps. "
                + StrictJson.ReDumpHint)
            { File = path };
        }

        private LocationTableFile LocationFile()
        {
            if (_locations != null) return _locations;
            lock (_gate)
            {
                if (_locations != null) return _locations;

                LocationTableFile f = LoadFile(
                    DumpFormat.LocationsFile, DumpSchemas.LocationTableFile, DumpJsonContext.Default.LocationTableFile);
                CheckStamp(f.stamp, DumpFormat.LocationsFile);

                LocationDef[] rows = Require(f.locations, DumpFormat.LocationsFile, "locations");
                RequireCount(rows.Length, f.count, DumpFormat.LocationsFile, "count");

                int placement = 0;
                for (int i = 0; i < rows.Length; i++)
                {
                    Require(rows[i].prefabName, DumpFormat.LocationsFile, "locations[" + i + "].prefabName");
                    Require(rows[i].name, DumpFormat.LocationsFile, "locations[" + i + "].name");
                    if (rows[i].index != i)
                    {
                        throw new GameDataException(
                            PathOf(DumpFormat.LocationsFile) + ": locations[" + i + "].index is "
                            + rows[i].index.ToString(CultureInfo.InvariantCulture)
                            + ", so the table is not in ZoneSystem.m_locations order. The order is "
                            + "semantic and cannot be repaired here. " + StrictJson.ReDumpHint)
                        { File = PathOf(DumpFormat.LocationsFile), Field = "locations[" + i + "].index" };
                    }

                    if (rows[i].enable && rows[i].quantity != 0) placement++;
                }

                RequireCount(placement, f.enabledCount, DumpFormat.LocationsFile, "enabledCount");
                return _locations = f;
            }
        }

        private VegetationTableFile VegetationFile()
        {
            if (_vegetation != null) return _vegetation;
            lock (_gate)
            {
                if (_vegetation != null) return _vegetation;

                VegetationTableFile f = LoadFile(
                    DumpFormat.VegetationFile, DumpSchemas.VegetationTableFile, DumpJsonContext.Default.VegetationTableFile);
                CheckStamp(f.stamp, DumpFormat.VegetationFile);

                VegetationDef[] rows = Require(f.vegetation, DumpFormat.VegetationFile, "vegetation");
                RequireCount(rows.Length, f.count, DumpFormat.VegetationFile, "count");

                for (int i = 0; i < rows.Length; i++)
                {
                    Require(rows[i].prefabName, DumpFormat.VegetationFile, "vegetation[" + i + "].prefabName");
                }

                return _vegetation = f;
            }
        }

        private AltBiomeTableFile AltBiomeFile()
        {
            if (_altBiomes != null) return _altBiomes;
            lock (_gate)
            {
                if (_altBiomes != null) return _altBiomes;

                AltBiomeTableFile f = LoadFile(
                    DumpFormat.AltBiomesFile, DumpSchemas.AltBiomeTableFile, DumpJsonContext.Default.AltBiomeTableFile);
                CheckStamp(f.stamp, DumpFormat.AltBiomesFile);

                AltBiomeDef[] rows = Require(f.altBiomes, DumpFormat.AltBiomesFile, "altBiomes");
                RequireCount(rows.Length, f.count, DumpFormat.AltBiomesFile, "count");

                for (int i = 0; i < rows.Length; i++)
                {
                    Require(rows[i].name, DumpFormat.AltBiomesFile, "altBiomes[" + i + "].name");
                }

                return _altBiomes = f;
            }
        }

        private LocationPrefabsFile LocationPrefabFile()
        {
            if (_locationPrefabs != null) return _locationPrefabs;
            lock (_gate)
            {
                if (_locationPrefabs != null) return _locationPrefabs;

                LocationPrefabsFile f = LoadFile(
                    DumpFormat.LocationPrefabsFile, DumpSchemas.LocationPrefabsFile,
                    DumpJsonContext.Default.LocationPrefabsFile);
                CheckStamp(f.stamp, DumpFormat.LocationPrefabsFile);

                LocationPrefabDef[] rows = Require(f.prefabs, DumpFormat.LocationPrefabsFile, "prefabs");
                RequireCount(rows.Length, f.count, DumpFormat.LocationPrefabsFile, "count");

                for (int i = 0; i < rows.Length; i++)
                {
                    Require(rows[i].prefabName, DumpFormat.LocationPrefabsFile, "prefabs[" + i + "].prefabName");
                }

                return _locationPrefabs = f;
            }
        }

        private LocalizationFile LocalizationTable()
        {
            if (_localization != null) return _localization;
            lock (_gate)
            {
                if (_localization != null) return _localization;

                LocalizationFile f = LoadFile(
                    DumpFormat.LocalizationFile, DumpSchemas.LocalizationFile,
                    DumpJsonContext.Default.LocalizationFile);
                CheckStamp(f.stamp, DumpFormat.LocalizationFile);

                // Not an error when 'skipped' is set - the dumper says so in as many words and the
                // table is then empty, which Localize reports as "no name" for every token. What IS an
                // error is a null table, because that is a file that never said either way.
                Dictionary<string, string> table = Require(
                    f.translations, DumpFormat.LocalizationFile, "translations");
                RequireCount(table.Count, f.count, DumpFormat.LocalizationFile, "count");

                // 'language' is deliberately NOT required. It is a label a consumer displays ("names:
                // English, from the 1.0.15 dump"), not a precondition for resolving a token, and
                // refusing the whole naming layer because a future dump forgot to record it would
                // trade a working answer for a missing caption.
                return _localization = f;
            }
        }

        private LocationOccupantsFile OccupantFile()
        {
            if (_occupants != null) return _occupants;
            lock (_gate)
            {
                if (_occupants != null) return _occupants;

                LocationOccupantsFile f = LoadFile(
                    DumpFormat.LocationChildrenFile, DumpSchemas.LocationOccupantsFile,
                    DumpJsonContext.Default.LocationOccupantsFile);
                CheckStamp(f.stamp, DumpFormat.LocationChildrenFile);

                LocationOccupantsDef[] rows = Require(
                    f.locations, DumpFormat.LocationChildrenFile, "locations");
                RequireCount(rows.Length, f.count, DumpFormat.LocationChildrenFile, "count");

                for (int i = 0; i < rows.Length; i++)
                {
                    string where = "locations[" + i.ToString(CultureInfo.InvariantCulture) + "]";
                    Require(rows[i].prefabName, DumpFormat.LocationChildrenFile, where + ".prefabName");
                    RequireOccupantsCaptured(
                        rows[i].occupantsCaptured, DumpFormat.LocationChildrenFile, where, rows[i].prefabName!);
                }

                return _occupants = f;
            }
        }

        private RoomOccupantsFile RoomOccupantFile()
        {
            if (_roomOccupants != null) return _roomOccupants;
            lock (_gate)
            {
                if (_roomOccupants != null) return _roomOccupants;

                RoomOccupantsFile f = LoadFile(
                    DumpFormat.RoomChildrenFile, DumpSchemas.RoomOccupantsFile,
                    DumpJsonContext.Default.RoomOccupantsFile);
                CheckStamp(f.stamp, DumpFormat.RoomChildrenFile);

                if (f.skipped != null)
                {
                    string path = PathOf(DumpFormat.RoomChildrenFile);
                    throw new GameDataException(
                        path + ": the room walk did not run (" + f.skipped + "), so this file lists no "
                        + "rooms at all. That is not 'no room contains a boss' - it is 'this dump cannot "
                        + "say', and the difference matters: the Mistlands queen's offering bowl lives in "
                        + "a room prefab and in no location prefab. " + StrictJson.ReDumpHint)
                    { File = path, Field = "skipped" };
                }

                RoomOccupantsDef[] rows = Require(f.rooms, DumpFormat.RoomChildrenFile, "rooms");
                RequireCount(rows.Length, f.count, DumpFormat.RoomChildrenFile, "count");

                for (int i = 0; i < rows.Length; i++)
                {
                    string where = "rooms[" + i.ToString(CultureInfo.InvariantCulture) + "]";
                    Require(rows[i].prefabName, DumpFormat.RoomChildrenFile, where + ".prefabName");
                    RequireOccupantsCaptured(
                        rows[i].occupantsCaptured, DumpFormat.RoomChildrenFile, where, rows[i].prefabName!);
                }

                return _roomOccupants = f;
            }
        }

        /// <summary>
        /// An occupant array that is ABSENT is not an occupant array that is EMPTY. A dump written
        /// before 2026-09-23 has no occupant arrays and they deserialize to null, which read as "empty"
        /// would answer "this world has no bosses and no traders" - the one wrong answer this loader
        /// exists to prevent. The dumper records which of the two it is; this refuses on the first.
        /// </summary>
        private void RequireOccupantsCaptured(bool captured, string relative, string where, string prefabName)
        {
            if (captured) return;
            string path = PathOf(relative);
            throw new GameDataException(
                path + ": " + where + " ('" + prefabName + "') has occupantsCaptured false, so its "
                + "occupant arrays were never written and are absent rather than empty. Reading them "
                + "would report a place with no boss, no trader and no runestone when the truth is that "
                + "nobody looked. " + StrictJson.ReDumpHint)
            { File = path, Field = where + ".occupantsCaptured" };
        }

        private T Require<T>(T? value, string relative, string field) where T : class
        {
            if (value != null) return value;
            string path = PathOf(relative);
            throw new GameDataException(
                path + ": '" + field + "' is null, and the tool has no honest substitute for it. "
                + StrictJson.ReDumpHint)
            { File = path, Field = field };
        }

        private void RequireCount(int actual, int declared, string relative, string field)
        {
            if (actual == declared) return;
            string path = PathOf(relative);
            throw new GameDataException(
                path + ": the file declares " + field + " = " + declared.ToString(CultureInfo.InvariantCulture)
                + " but holds " + actual.ToString(CultureInfo.InvariantCulture)
                + ". The dump is truncated or was edited. " + StrictJson.ReDumpHint)
            { File = path, Field = field };
        }

        private static string SafeCurrentDirectory()
        {
            try { return System.IO.Directory.GetCurrentDirectory(); }
            catch { return ""; }
        }
    }
}
