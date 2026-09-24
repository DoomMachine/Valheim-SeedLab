namespace SeedLab.Contracts.Dump
{
    /// <summary>
    /// Names, magics and schema numbers of the dump produced by <c>tools\SeedLab.Dumper</c>.
    /// Both sides of the wire (the BepInEx plugin that writes, the offline tool that reads) use these
    /// constants so a rename cannot silently desynchronise them.
    ///
    /// Layout (spec 04 section 3.5 "Output schema", spec 08 section 2.2):
    /// <code>
    /// &lt;root&gt;/1.0.15-59f53fb5/          &lt;gameVersion&gt;-&lt;first 8 hex of assembly_valheim sha256&gt;
    ///   manifest.json                     latest run; its files[] covers the WHOLE folder
    ///   manifest-assets.json              per-run copies, kept so one mode cannot erase another's
    ///   manifest-natives.json             metadata (world, counts, notes)
    ///   manifest-worldgen.json
    ///   locations.json  vegetation.json  altbiomes.json
    ///   prefab-constants.json  version-constants.json  locationprefabs.json
    ///   locationchildren.json  roomchildren.json  search.json
    ///   goldens/
    ///     natives-random.json   natives-perlin.bin   natives-perlin.json
    ///     natives-libm.json     natives-half.json    natives-hash.json
    ///     worldgen-&lt;seedHex&gt;-&lt;source&gt;.json          worldgen-&lt;seedHex&gt;-&lt;source&gt;-riverpoints.bin
    ///     worldgrid-&lt;seedHex&gt;-&lt;gridId&gt;-&lt;source&gt;.bin
    ///     locationinstances-&lt;seedHex&gt;.json altbiomes-assignment-&lt;seedHex&gt;.json
    /// </code>
    ///
    /// <c>&lt;source&gt;</c> is <see cref="SourceWorld"/> or <see cref="SourceMenu"/> and is NOT
    /// decoration: the asset dump and the world-generator dump can both produce a generator dump for
    /// the same seed, and without it the second run would silently overwrite the first (both write
    /// with <c>FileMode.Create</c>). They are not interchangeable - <c>world</c> is the generator the
    /// loaded world is actually running, <c>menu</c> is one the dumper built at the main menu from the
    /// seed text alone.
    ///
    /// JSON conventions, all three non-negotiable per spec 04 section 3.5:
    /// <list type="bullet">
    /// <item>Every object that has <c>float</c>/<c>double</c> members carries a sibling <c>"bits"</c>
    /// object mapping the same member names to the raw IEEE-754 pattern
    /// (<c>BitConverter.SingleToInt32Bits</c> as <c>"0xXXXXXXXX"</c>, doubles as 16 hex digits).
    /// The decimal form is a human convenience; a reader must use the bits.</item>
    /// <item>Enums are written as integers, never names.</item>
    /// <item>Nothing is omitted because it equals a code default.</item>
    /// </list>
    /// Member (JSON key) order inside an object is advisory - only array order is semantic.
    /// </summary>
    public static class DumpFormat
    {
        /// <summary>
        /// Bumped whenever the meaning of an existing field changes. Additive changes do not bump it.
        ///
        /// <b>Deliberately NOT bumped on 2026-09-23</b>, although four fields did change meaning:
        /// <see cref="RandomSpawnDef.activatedNetViewNames"/>,
        /// <see cref="RandomSpawnDef.subtreeChildNames"/>,
        /// <see cref="RandomSpawnDef.offObjectChildNames"/> and
        /// <see cref="RandomObjectDef.subtreeChildNames"/> now hold NORMALISED names
        /// (<c>Utils.GetPrefabName</c>) where they used to hold raw <c>GameObject.name</c>s. The reason
        /// the number stays at 1 is narrow and worth writing down rather than re-deriving: the prefab
        /// child walk has <b>never produced a file</b> - it was written, reviewed and preflighted on
        /// 2026-09-23 and revised before its first run - so no data with the old meaning exists
        /// anywhere for a bump to protect. Everything else about the change is additive.
        ///
        /// If any of those three ever changes meaning again, bump it: the next time there will be old
        /// files.
        /// </summary>
        public const int Schema = 1;

        /// <summary>The entry point a reader opens. Always written by the most recent run, and its
        /// <c>files[]</c> is a scan of the whole folder - not just that run's output - so a natives
        /// run after an asset run cannot leave a manifest that omits half the dump.</summary>
        public const string ManifestFile = "manifest.json";

        /// <summary>Per-run copy, e.g. <c>manifest-assets.json</c>. Keeps that run's own world, counts
        /// and notes, which the shared <see cref="ManifestFile"/> would otherwise overwrite.</summary>
        public static string ManifestFileForMode(string mode) { return "manifest-" + mode + ".json"; }

        public const string LocationsFile = "locations.json";
        public const string VegetationFile = "vegetation.json";
        public const string AltBiomesFile = "altbiomes.json";
        public const string PrefabConstantsFile = "prefab-constants.json";
        public const string VersionConstantsFile = "version-constants.json";

        /// <summary>The new-world UI's seed/name input constraints (main-menu capture only).</summary>
        public const string SeedInputFile = "seed-input.json";
        public const string LocationPrefabsFile = "locationprefabs.json";

        /// <summary>
        /// <c>locationchildren.json</c> - what is INSIDE each location prefab: the ordered
        /// <c>RandomSpawn</c> and <c>RandomObject</c> arrays that spend the location's seeded RNG
        /// stream, every <c>Container</c> with its <c>m_defaultItems</c> drop table, and a flat index
        /// of the distinct child names. Written by the same prefab walk that writes
        /// <see cref="LocationPrefabsFile"/>. See <see cref="LocationChildrenFile"/> for the contract.
        /// </summary>
        public const string LocationChildrenFile = "locationchildren.json";

        /// <summary>
        /// <c>roomchildren.json</c> - the same walk applied to every dungeon/camp ROOM prefab in
        /// <c>DungeonDB</c>, which is where everything inside a crypt, a cave or a camp actually lives.
        /// A location prefab never contains a room. See <see cref="RoomChildrenFile"/>.
        /// </summary>
        public const string RoomChildrenFile = "roomchildren.json";

        /// <summary>
        /// <c>search.json</c> - for each configured prefab name, FOUND (with every host, path and gate)
        /// or NOT FOUND, in as many words, plus what was searched and what was not. It is the one file
        /// in the dump whose job is to make an absence explicit instead of implicit. See
        /// <see cref="SearchFile"/>.
        /// </summary>
        public const string SearchFile = "search.json";

        /// <summary>
        /// The game's token-to-text table and the language it is in. Without it every name this tool
        /// shows is a developer's identifier - <c>GDKing</c> where the player knows "The Elder".
        /// See <see cref="LocalizationFile"/>.
        /// </summary>
        public const string LocalizationFile = "localization.json";

        public const string GoldensDir = "goldens";
        public const string NativesRandomFile = "natives-random.json";
        public const string NativesPerlinIndexFile = "natives-perlin.json";
        public const string NativesPerlinBinFile = "natives-perlin.bin";
        public const string NativesLibmFile = "natives-libm.json";
        public const string NativesHalfFile = "natives-half.json";
        public const string NativesHashFile = "natives-hash.json";

        /// <summary>A generator dump taken from the world that was loaded at the time (the asset dump).</summary>
        public const string SourceWorld = "world";

        /// <summary>A generator dump the dumper built at the main menu from a seed text (the
        /// world-generator dump).</summary>
        public const string SourceMenu = "menu";

        /// <summary><c>goldens/worldgen-&lt;seedHex&gt;-&lt;source&gt;.json</c>. The source token keeps the
        /// asset dump and the world-generator dump from overwriting each other for the same seed.</summary>
        public static string WorldGenFile(string seedHex, string source)
        {
            return GoldensDir + "/worldgen-" + seedHex + "-" + source + ".json";
        }

        /// <summary><c>goldens/worldgen-&lt;seedHex&gt;-&lt;source&gt;-riverpoints.bin</c>.</summary>
        public static string RiverPointsFile(string seedHex, string source)
        {
            return GoldensDir + "/worldgen-" + seedHex + "-" + source + "-riverpoints.bin";
        }

        /// <summary><c>goldens/worldgrid-&lt;seedHex&gt;-&lt;gridId&gt;-&lt;source&gt;.bin</c>. Same
        /// collision, same fix: both modes accept <c>grid=</c>.</summary>
        public static string WorldGridFile(string seedHex, string gridId, string source)
        {
            return GoldensDir + "/worldgrid-" + seedHex + "-" + gridId + "-" + source + ".bin";
        }

        /// <summary>
        /// Magic of <c>natives-perlin.bin</c>, written as its 4 raw ASCII bytes - NOT as a
        /// length-prefixed <c>BinaryWriter</c> string.
        ///
        /// The file is a 12-byte header followed by nothing but samples:
        /// <code>
        /// magic (4 bytes "VPL1") | int32 schema | int32 blockCount
        /// sample, sample, sample, ...        (blockCount blocks, concatenated, in index order)
        /// </code>
        /// One sample is <c>float32 x, float32 y, float32 result</c> = 12 bytes, and <c>y</c> is 0 for
        /// a <c>kind 1</c> (<c>PerlinNoise1D</c>) block. <b>There is no per-block header in the .bin.</b>
        /// A block's identity, note, kind, sampleCount and its absolute <c>byteOffset</c> from the start
        /// of the file live in the sibling index <c>natives-perlin.json</c>
        /// (<c>NativesPerlinIndexFile.blocks</c>), and that index is the only way to tell one block
        /// from the next: a reader seeks to <c>byteOffset</c> and reads <c>sampleCount * 12</c> bytes.
        /// Little-endian throughout.
        ///
        /// (This block is the contract a reader is written from. It said "int32 idByteLen, utf8 id,
        /// int32 kind, int32 sampleCount" per block until 2026-09-23; the writer
        /// <c>ModeNatives.PerlinBody</c> never wrote that, and a reader built on it would have decoded
        /// sample floats as lengths and looked like a generator defect.)
        /// </summary>
        public const string PerlinMagic = "VPL1";

        /// <summary>Magic of <c>worldgrid-&lt;seedHex&gt;-&lt;gridId&gt;-&lt;source&gt;.bin</c> (spec 04
        /// section 3.6.2), written as 4 raw ASCII bytes.
        /// Header: magic, int32 schema, int32 seed, int32 worldGenVersion, float32 x0, float32 z0,
        /// float32 step, int32 nx, int32 nz, int32 flags.
        ///
        /// Body: <c>nz*nx</c> records, x fastest (row z0 first). One record is
        /// <c>{ uint16 biome, float32 height, float32 maskR, maskG, maskB, maskA }</c>, then
        /// <c>float32 baseHeight</c> when <see cref="GridFlagBaseHeight"/> is set, and the whole record
        /// is PREFIXED by <c>float32 x, float32 z</c> when <see cref="GridFlagExplicitPoints"/> is set
        /// (in which case <c>nz</c> is 1, <c>nx</c> is the point count and x0/z0/step are meaningless).
        /// Little-endian throughout; <c>biome</c> is the <c>Heightmap.Biome</c> bitmask value.</summary>
        public const string GridMagic = "VGT1";

        /// <summary>Grid record carries the private <c>WorldGenerator.GetBaseHeight(x, y, menuTerrain:false)</c>.</summary>
        public const int GridFlagBaseHeight = 1;

        /// <summary>Grid sample positions are an explicit list, not a lattice (the <c>edges</c> grid).</summary>
        public const int GridFlagExplicitPoints = 2;

        /// <summary>Magic of <c>worldgen-&lt;seedHex&gt;-&lt;source&gt;-riverpoints.bin</c>, written as
        /// 4 raw ASCII bytes. Header: magic, int32 schema,
        /// int32 seed, float32 gridSize, int32 cellCount. Per cell: int32 gx, int32 gy, int32 n,
        /// then n * (float32 p.x, float32 p.y, float32 w, float32 w2). Little-endian.</summary>
        public const string RiverPointsMagic = "VRP1";
    }
}
