namespace SeedLab.Contracts.Dump
{
    /// <summary>
    /// The private state of one <c>WorldGenerator</c>, per seed (spec 04 section 3.6.1).
    /// Fields <c>m_offset0..4</c>, <c>m_riverSeed</c>, <c>m_streamSeed</c>, <c>m_lakes</c>,
    /// <c>m_rivers</c>, <c>m_streams</c>, <c>m_riverPoints</c> are all private instance fields
    /// (WorldGenerator.cs:60-80), read with <c>AccessTools.Field(...).GetValue(instance)</c>.
    /// </summary>
    public sealed class WorldGenDumpFile
    {
        public string? stamp;
        public int schema;

        public string? worldName;
        public string? seedText;
        public int seed;
        public int worldGenVersion;
        public bool menu;

        /// <summary>The generator's own <c>m_version</c> (copied from the world's worldGenVersion and
        /// fed to <c>VersionSetup</c>: v0 = 1500/0.5/8000, v1 = 1000/0.5/8000, v2 = 1000/0.4/6000).</summary>
        public int version;

        /// <summary>Draw 1 of 7. <c>Random.Range(-10000, 10000)</c>.</summary>
        public float offset0;

        /// <summary>Draw 2.</summary>
        public float offset1;

        /// <summary>Draw 3.</summary>
        public float offset2;

        /// <summary>Draw 4.</summary>
        public float offset3;

        /// <summary>Draw 7 - LAST, after the two river seeds. Getting this wrong misplaces the
        /// Mistlands and nothing else, which makes it exactly the error that survives a casual check.
        /// (WorldGenerator..ctor, WorldGenerator.cs:223-229.)</summary>
        public float offset4;

        /// <summary>Draw 5. <c>Random.Range(int.MinValue, int.MaxValue)</c>.</summary>
        public int riverSeed;

        /// <summary>Draw 6.</summary>
        public int streamSeed;

        /// <summary>The static <c>m_noiseGen.GetSeed()</c>. It is unconditionally <c>SetSeed(0)</c> at
        /// WorldGenerator.cs:222, so the world seed never reaches the cellular generator. Dumped as a
        /// tripwire: anything but 0 means the build changed.</summary>
        public int noiseGenSeed;

        public float minMountainDistance;
        public float minDarklandNoise;
        public float maxMarshDistance;

        /// <summary>Independent replay of the seven constructor draws, made inside a RandomGuard on a
        /// scratch stream: <c>InitState(seed)</c> then the same seven calls in the same order. If these
        /// do not equal the reflected fields above, something perturbed the constructor.</summary>
        public RandomTraceDef? constructorTrace;

        /// <summary><c>m_lakes</c> after <c>FindLakes</c> + <c>MergePoints(list, 800f)</c>.</summary>
        public Vec2Def[]? lakes;

        /// <summary><c>m_rivers</c>, the output of <c>PlaceRivers()</c>.</summary>
        public RiverDef[]? rivers;

        /// <summary><c>m_streams</c>. NOTE: <c>Pregenerate</c> discards the return value of
        /// <c>PlaceStreams(isDN: true)</c>, so this holds only the non-DeepNorth streams. The DeepNorth
        /// ones exist only inside <c>m_riverPoints</c>.</summary>
        public RiverDef[]? streams;

        public int riverPointCellCount;
        public int riverPointTotal;

        /// <summary>File name of the raw sidecar holding <c>m_riverPoints</c>
        /// (<c>DumpFormat.RiverPointsMagic</c>), or null when the dictionary was empty.</summary>
        public string? riverPointsFile;

        /// <summary>Grid files written for this seed, by grid id.</summary>
        public string[]? grids;
    }

    /// <summary>A <c>WorldGenerator.River</c>.</summary>
    public sealed class RiverDef
    {
        public Vec2Def? p0;
        public Vec2Def? p1;
        public Vec2Def? center;
        public float widthMin;
        public float widthMax;
        public float curveWidth;
        public float curveWavelength;
    }

    /// <summary>One <c>ZoneSystem.LocationInstance</c> as the game's own genloc produced it.</summary>
    public sealed class LocationInstanceDef
    {
        public short zoneX;
        public short zoneY;
        public string? prefabName;

        /// <summary><c>m_prefabName.GetStableHashCode()</c> - what <c>ZoneSystem.Save</c> writes.</summary>
        public int hash;

        public float x;
        public float y;
        public float z;

        /// <summary>Most instances are unplaced candidates. That is what makes the set useful: it is
        /// the raw output of the algorithm, before <c>RemoveUnplacedLocations</c> prunes anything.</summary>
        public bool placed;
    }

    public sealed class LocationInstancesFile
    {
        public string? stamp;
        public int schema;
        public string? worldName;
        public int seed;
        public int locationVersion;
        public int count;

        /// <summary>The order the placement run walked, as prefab names - i.e.
        /// <c>m_locations.Where(enable &amp;&amp; quantity != 0).OrderByDescending(m_prioritized)</c>.</summary>
        public string[]? orderedPrefabNames;

        public LocationInstanceDef[]? instances;
    }

    /// <summary>The alt-biome assignment for one world (goldens/altbiomes-assignment-&lt;seedHex&gt;.json).</summary>
    public sealed class AltBiomeAssignmentFile
    {
        public string? stamp;
        public int schema;
        public string? worldName;
        public int seed;

        /// <summary><c>WorldGenerator.instance.GetSeed() + 920</c> - the literal that opens
        /// <c>GenerateAltBiomes</c>.</summary>
        public int openingInitState;

        /// <summary>The observed enumeration order of <c>AltBiomeWorldData.Biomes</c>.</summary>
        public BiomeKeyDef[]? biomeKeys;

        public SectorDef[]? sectors;
        public AltBiomeAssignmentDef[]? assignments;

        /// <summary>True when any AltBiome already had sectors before the call - the contamination bug
        /// (spec 04 section 3.7 Invariant 2). The dump is then not a clean first run.</summary>
        public bool contaminated;
    }
}
