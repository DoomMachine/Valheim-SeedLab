using System;
using System.Collections.Generic;
using SeedLab.WorldGen;
using SeedLab.WorldGen.Unity;

namespace SeedLab.Locations
{
    /// <summary>
    /// One <c>BiomeSector</c> (decomp/BiomeSector.cs). Sectors are identified by their index in
    /// <see cref="BiomeField.Sectors"/>, which is append-only and stable - never by their position in a
    /// <see cref="BiomeTypeInfo.Sectors"/> list, which <c>GenerateAltBiomes</c> shuffles in place.
    /// </summary>
    public sealed class BiomeSectorData
    {
        internal BiomeSectorData(int index, Biome biome)
        {
            Index = index;
            Biome = biome;
        }

        public int Index { get; }
        public Biome Biome { get; }

        /// <summary>Number of points of this sector with at least one differing 4-neighbour.</summary>
        public int EdgeCount;

        /// <summary>
        /// During the edge pass this is the running FLOAT sum of edge-point map-space coordinates -
        /// <c>Center += new Vector2(j, i)</c>, one float add per point, precision loss included. The
        /// post pass turns it into world space.
        /// </summary>
        public float CenterX, CenterY;

        /// <summary>
        /// <c>Vector2</c> fields default to (0, 0), not to +/-infinity, so a sector that lies entirely
        /// in one quadrant gets a box that still contains the origin. The game never fixes this and
        /// nothing on the placement path reads it. Reproduced so a dump comparison lines up.
        /// </summary>
        public float MinX, MinY, MaxX, MaxY;

        /// <summary>
        /// The raw map-space edge-point sums, kept after step E has overwritten
        /// <see cref="CenterX"/>/<see cref="CenterY"/> with world space. Diagnostics only - nothing on
        /// the placement path reads them - but they are what a disagreement with the dumped
        /// <c>BiomeSector.Center</c> has to be bisected against.
        /// </summary>
        public float CenterSumX, CenterSumY;

        /// <summary>
        /// <c>GenerateSectors</c> computes BOTH from <c>Min</c> and passes <c>new Vector3(x, y)</c> to
        /// <c>GetZone</c>, which reads <c>z</c> - so MaxZone == MinZone and both have y == 0, always.
        /// </summary>
        public Vec2s MinZone, MaxZone;

        public float HeightMin = 99999f;
        public float HeightMax = -99999f;
        public float HeightAvg;
        public float DistanceFromCenter;

        /// <summary>
        /// Indices into <see cref="BiomeField.Sectors"/>. At most ONE neighbour is offered per edge
        /// point: the game's <c>||</c> chain short-circuits and leaves <c>item</c> holding only the
        /// first differing neighbour in probe order <c>-x, +x, -y, +y</c>.
        /// </summary>
        public readonly List<int> Neighbors = new List<int>(4);

        /// <summary>Filled by <see cref="AltBiomeAssignment"/>; empty when alt-biomes are not computed.</summary>
        public readonly List<AltBiomeRuntime> AltBiomes = new List<AltBiomeRuntime>();

        public override string ToString()
            => Biome + " #" + Index + " edges=" + EdgeCount + " center=(" + CenterX + ", " + CenterY
               + ") h=[" + HeightMin + ", " + HeightMax + "] avg=" + HeightAvg;
    }

    /// <summary>
    /// <c>BiomeTypeInfo</c> - one entry of <c>AltBiomeWorldData.Biomes</c>. Points are stored packed as
    /// <c>y * 2048 + x</c> (see <see cref="BiomeGrid.Index"/>); the game stores a
    /// <c>BiomePointCoordinate { short x; short y; }</c>, which is the same information in the same
    /// order, and order is all that matters because <c>GetRandomPointByBiome</c> indexes straight in.
    /// </summary>
    public sealed class BiomeTypeInfo
    {
        internal BiomeTypeInfo(Biome biome) { Biome = biome; }

        public Biome Biome { get; }

        /// <summary>Indices into <see cref="BiomeField.Sectors"/>, in creation order until shuffled.</summary>
        public readonly List<int> Sectors = new List<int>();

        public readonly List<int> AllPoints = new List<int>();
        public readonly List<int> AllPointsAboveSeaLevel = new List<int>();
    }

    /// <summary>
    /// <c>AltBiomeWorldData</c> after <c>GenerateSectors</c>: the flood-filled sector decomposition, the
    /// per-biome point lists, and the random point draws that location placement uses.
    ///
    /// <para><b>The quirk that decides every draw.</b> Only <c>tryFill</c> appends to
    /// <c>AllPoints</c>/<c>AllPointsAboveSeaLevel</c>, and the seed point of each flood-filled component
    /// is pushed by the outer scan, not by <c>tryFill</c>. So <b>one point per connected component of
    /// every non-global biome is missing from the lists</b>. Get that wrong and every index drawn from
    /// them shifts. AshLands / DeepNorth / Ocean are not flood filled at all - they get one global
    /// sector each and their points are appended row-major by the step-B scan, with no missing point.
    /// </para>
    ///
    /// <para><b>Measured against the game's own dump, 2026-09-23</b>
    /// (goldens/altbiomes-assignment-0480A34C.json, seed 75539276): 938 of 938 sectors agree on biome,
    /// EdgeCount, Center, Min, Max, MinZone, MaxZone, HeightMin, HeightMax, HeightAvg,
    /// DistanceFromCenter and the neighbour list - every float compared as its IEEE-754 bit pattern -
    /// and all twelve biome keys agree on AllPoints.Count and AllPointsAboveSeaLevel.Count. Appending
    /// the flood fill's seed point to those lists (the "obvious fix") drops the location reproduction
    /// from 12 228 of 12 228 instances to 2 788, which is what makes the quirk load-bearing rather than
    /// cosmetic.</para>
    /// </summary>
    public sealed class BiomeField
    {
        /// <summary>
        /// The <c>AltBiomeWorldData.Biomes</c> dictionary keys, in <c>Enum.GetValues</c> (ascending)
        /// order - which is the order <c>GenerateAltBiomes</c> walks. TWELVE keys: the composite
        /// <c>Land</c> and <c>All</c> and the zero key <c>None</c> are real entries with empty lists,
        /// and <c>None</c> matters because <c>HasFlag(None)</c> is true for every mask.
        /// Hard-coded rather than derived from <c>Enum.GetValues</c> so the order is a fact of this
        /// port rather than of a runtime.
        /// </summary>
        public static readonly Biome[] BiomeKeyOrder =
        {
            Biome.None,        // 0
            Biome.Meadows,     // 1
            Biome.Swamp,       // 2
            Biome.Mountain,    // 4
            Biome.BlackForest, // 8
            Biome.Plains,      // 16
            Biome.AshLands,    // 32
            Biome.DeepNorth,   // 64
            Biome.Ocean,       // 256
            Biome.Mistlands,   // 512
            Biome.Land,        // 639
            Biome.All,         // 895
        };

        private readonly BiomeTypeInfo[] m_byKey;                 // parallel to BiomeKeyOrder
        private readonly Dictionary<Biome, BiomeTypeInfo> m_lookup;

        private BiomeField(BiomeGrid grid, int[] pointSectors, List<BiomeSectorData> sectors,
                           BiomeTypeInfo[] byKey, double buildMilliseconds)
        {
            Grid = grid;
            PointSectors = pointSectors;
            Sectors = sectors;
            m_byKey = byKey;
            BuildMilliseconds = buildMilliseconds;
            m_lookup = new Dictionary<Biome, BiomeTypeInfo>(byKey.Length);
            for (int i = 0; i < byKey.Length; i++) m_lookup[byKey[i].Biome] = byKey[i];
        }

        public BiomeGrid Grid { get; }

        /// <summary>Sector index per grid point, <c>k = y * 2048 + x</c>. Never -1 after the build.</summary>
        public int[] PointSectors { get; }

        /// <summary><c>AltBiomeWorldData.Sectors</c>, append-only, the stable sector identity.</summary>
        public List<BiomeSectorData> Sectors { get; }

        public double BuildMilliseconds { get; }

        /// <summary>The twelve <see cref="BiomeTypeInfo"/> in <see cref="BiomeKeyOrder"/> order.</summary>
        public IReadOnlyList<BiomeTypeInfo> BiomesInKeyOrder => m_byKey;

        public BiomeTypeInfo this[Biome key] => m_lookup[key];

        /// <summary>
        /// <c>AltBiomeWorldData.GenerateSectors</c> (decomp/AltBiomeWorldData.cs 143-260), steps A-F.
        /// The alt-biome assignment that the game runs at the end of the same method is deliberately
        /// NOT run here - see <see cref="AltBiomeAssignment.Generate"/>, which needs the asset table.
        /// </summary>
        public static BiomeField Build(BiomeGrid grid)
        {
            if (grid == null) throw new ArgumentNullException(nameof(grid));
            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();

            const int size = BiomeGrid.Size;
            byte[] pb = grid.PointBiomes;
            float[] ph = grid.PointHeights;

            BiomeTypeInfo[] byKey = new BiomeTypeInfo[BiomeKeyOrder.Length];
            BiomeTypeInfo?[] byIndex = new BiomeTypeInfo?[(int)BiomeIndex.Count];
            for (int i = 0; i < BiomeKeyOrder.Length; i++)
            {
                byKey[i] = new BiomeTypeInfo(BiomeKeyOrder[i]);
                Biome b = BiomeKeyOrder[i];
                // Only the ten single-value keys have a BiomeIndex; Land/All do not.
                if (b == Biome.None) byIndex[(int)BiomeIndex.None] = byKey[i];
                else if (b != Biome.Land && b != Biome.All) byIndex[(int)b.ToBiomeIndex()] = byKey[i];
            }

            List<BiomeSectorData> sectors = new List<BiomeSectorData>(4096);
            int[] pointSectors = new int[BiomeGrid.PointCount];
            bool[] visited = new bool[BiomeGrid.PointCount];

            BiomeSectorData NewSector(Biome b)
            {
                BiomeSectorData s = new BiomeSectorData(sectors.Count, b);
                sectors.Add(s);
                BiomeTypeInfo? info = byIndex[(int)b.ToBiomeIndex()];
                if (info == null) throw new InvalidOperationException("No BiomeTypeInfo for " + b);
                info.Sectors.Add(s.Index);
                return s;
            }

            // ---- Step A: the three global sectors, in this exact order (indices 0, 1, 2).
            BiomeSectorData ash = NewSector(Biome.AshLands);
            BiomeSectorData deep = NewSector(Biome.DeepNorth);
            BiomeSectorData ocean = NewSector(Biome.Ocean);
            int[] globalFor = new int[(int)BiomeIndex.Count];
            for (int i = 0; i < globalFor.Length; i++) globalFor[i] = -1;
            globalFor[(int)BiomeIndex.AshLands] = ash.Index;
            globalFor[(int)BiomeIndex.DeepNorth] = deep.Index;
            globalFor[(int)BiomeIndex.Ocean] = ocean.Index;

            // ---- Step B: row-major scan claims every AshLands / DeepNorth / Ocean point.
            for (int y = 0; y < size; y++)
            {
                int rowBase = y * size;
                for (int x = 0; x < size; x++)
                {
                    int k = rowBase + x;
                    int g = globalFor[pb[k]];
                    if (g < 0) continue;
                    pointSectors[k] = g;
                    visited[k] = true;
                    BiomeTypeInfo info = byIndex[(int)sectors[g].Biome.ToBiomeIndex()]!;
                    info.AllPoints.Add(k);
                    if (ph[k] >= 30f) info.AllPointsAboveSeaLevel.Add(k);
                }
            }

            // ---- Step C: 4-connected DFS flood fill for everything else.
            // Neighbour push order +x, -x, +y, -y; LIFO pop. The seed point is NEVER appended to the
            // point lists - see the class remarks. Do not "fix" it.
            int[] stack = new int[1024];
            int sp = 0;
            for (int y = 0; y < size; y++)
            {
                int rowBase = y * size;
                for (int x = 0; x < size; x++)
                {
                    int k0 = rowBase + x;
                    if (visited[k0]) continue;
                    visited[k0] = true;
                    BiomeSectorData s = NewSector(((BiomeIndex)pb[k0]).ToBiome());
                    pointSectors[k0] = s.Index;
                    BiomeTypeInfo info = byIndex[(int)s.Biome.ToBiomeIndex()]!;
                    if (sp == stack.Length) Array.Resize(ref stack, stack.Length * 2);
                    stack[sp++] = k0;
                    while (sp > 0)
                    {
                        int p = stack[--sp];
                        byte pBiome = pb[p];
                        int px = p & (size - 1);
                        int py = p >> 11;

                        // tryFill(+x), tryFill(-x), tryFill(+y), tryFill(-y)
                        if (px + 1 < size) TryFill(p + 1);
                        if (px - 1 >= 0) TryFill(p - 1);
                        if (py + 1 < size) TryFill(p + size);
                        if (py - 1 >= 0) TryFill(p - size);

                        void TryFill(int n)
                        {
                            if (visited[n] || pb[n] != pBiome) return;
                            visited[n] = true;
                            pointSectors[n] = s.Index;
                            info.AllPoints.Add(n);
                            if (ph[n] >= 30f) info.AllPointsAboveSeaLevel.Add(n);
                            if (sp == stack.Length) Array.Resize(ref stack, stack.Length * 2);
                            stack[sp++] = n;
                        }
                    }
                }
            }

            // ---- Step D: edge pass over the interior only, probe order -x, +x, -y, +y, and `item`
            // holds only the FIRST differing neighbour because || short-circuits.
            for (int y = 1; y < size - 1; y++)
            {
                int rowBase = y * size;
                for (int x = 1; x < size - 1; x++)
                {
                    int k = rowBase + x;
                    int self = pointSectors[k];
                    int item;
                    if ((item = pointSectors[k - 1]) != self
                        || (item = pointSectors[k + 1]) != self
                        || (item = pointSectors[k - size]) != self
                        || (item = pointSectors[k + size]) != self)
                    {
                        BiomeSectorData s = sectors[self];
                        float h = ph[k];
                        s.EdgeCount++;
                        s.CenterX += x;               // float add, one per edge point, as in Vector2 +=
                        s.CenterY += y;
                        s.CenterSumX = s.CenterX;
                        s.CenterSumY = s.CenterY;
                        if (x < s.MinX) s.MinX = x;
                        if (x > s.MaxX) s.MaxX = x;
                        if (y < s.MinY) s.MinY = y;
                        if (y > s.MaxY) s.MaxY = y;
                        if (!s.Neighbors.Contains(item)) s.Neighbors.Add(item);
                        if (h < s.HeightMin) s.HeightMin = h;
                        if (h > s.HeightMax) s.HeightMax = h;
                    }
                }
            }

            // ---- Step E: map space -> world space, for EdgeCount > 0 only.
            foreach (BiomeTypeInfo info in byKey)
            {
                foreach (int si in info.Sectors)
                {
                    BiomeSectorData s = sectors[si];
                    if (s.EdgeCount <= 0) continue;
                    s.CenterX = BiomeGrid.MapSpaceToWorldSpace(s.CenterX / (float)s.EdgeCount);
                    s.CenterY = BiomeGrid.MapSpaceToWorldSpace(s.CenterY / (float)s.EdgeCount);
                    s.MinX = BiomeGrid.MapSpaceToWorldSpace(s.MinX);
                    s.MinY = BiomeGrid.MapSpaceToWorldSpace(s.MinY);
                    s.MaxX = BiomeGrid.MapSpaceToWorldSpace(s.MaxX);
                    s.MaxY = BiomeGrid.MapSpaceToWorldSpace(s.MaxY);
                    // GetZone(new Vector3(Min.x, Min.y)) - the second argument lands in Y and GetZone
                    // reads Z, so both zones are (zoneOf(Min.x), zoneOf(0)). MaxZone uses Min too.
                    s.MinZone = ZoneMath.GetZone(s.MinX, 0f);
                    s.MaxZone = ZoneMath.GetZone(s.MinX, 0f);
                    s.HeightAvg = (s.HeightMin + s.HeightMax) / 2f;
                }
            }

            // ---- Step F: SectorsCalculated; the IsDiscovered pass is dead (MinZone == MaxZone), and
            // DistanceFromCenter is set for EVERY sector, including the EdgeCount == 0 ones whose
            // Center is still (0, 0) in map space.
            foreach (BiomeSectorData s in sectors)
                s.DistanceFromCenter = Vec2.Distance(new Vec2(s.CenterX, s.CenterY), Vec2.Zero);

            sw.Stop();
            return new BiomeField(grid, pointSectors, sectors, byKey, sw.Elapsed.TotalMilliseconds);
        }

        // -------------------------------------------------------------------------------------------
        // WorldGenerator.GetBiomeSector
        // -------------------------------------------------------------------------------------------

        /// <summary>
        /// <c>WorldGenerator.GetBiomeSector(Vector3)</c> -> <c>GetBiomeSector(int, int, bool)</c>
        /// (decomp/WorldGenerator.cs 840-870). The <c>clamp</c> parameter is never read: the body clamps
        /// to [0, 2047] unconditionally. Used by placement filters 10a and 10b.
        /// </summary>
        public BiomeSectorData GetBiomeSector(float worldX, float worldZ)
        {
            int gx = BiomeGrid.WorldSpaceToMapSpace(worldX);
            int gy = BiomeGrid.WorldSpaceToMapSpace(worldZ);
            if (gx < 0) gx = 0;
            if (gy < 0) gy = 0;
            if (gx >= BiomeGrid.Size) gx = BiomeGrid.Size - 1;
            if (gy >= BiomeGrid.Size) gy = BiomeGrid.Size - 1;
            return Sectors[PointSectors[BiomeGrid.Index(gx, gy)]];
        }

        // -------------------------------------------------------------------------------------------
        // The random point draws (AltBiomeWorldData)
        // -------------------------------------------------------------------------------------------

        /// <summary>
        /// <c>AltBiomeWorldData.RandomBiomeFromBiomes</c> (decomp 383-448), reproduced literally
        /// including both bugs.
        ///
        /// <para>Draws 0 ints when the mask has 0 or 1 bits, exactly 1 otherwise. The draw is
        /// <c>Range(0, popcount - 1)</c>, whose upper bound is EXCLUSIVE, so the last reachable slot is
        /// never chosen. The Plains slot returns <b>BlackForest</b>, and the slot that returns
        /// <b>Ocean</b> tests the <b>Meadows</b> bit - so Ocean can come back only when Meadows, Ocean
        /// and Mistlands are all set, and the Ocean bit itself is never tested. Anything past the
        /// reachable slots falls through to Mistlands.</para>
        ///
        /// <para>The returned biome need not be in the requested mask. Filter 2 re-tests the point's
        /// real biome, so for multi-biome entries this wastes a large share of attempts. That waste is
        /// part of the result.</para>
        ///
        /// <para><b>Both bugs are load-bearing, measured 2026-09-23.</b> Making the Plains slot return
        /// Plains drops the fresh-world reproduction from 12 228 of 12 228 instances to 11 675 (95.5 %);
        /// making the draw <c>Range(0, num)</c> drops it to 8 456 (69.2 %).</para>
        /// </summary>
        public static Biome RandomBiomeFromBiomes(UnityRandom rnd, Biome biome)
        {
            int m = (int)biome;
            if ((m & (m - 1)) == 0) return biome;

            int num = 0;
            if ((m & (int)Biome.Meadows) != 0) num++;
            if ((m & (int)Biome.Swamp) != 0) num++;
            if ((m & (int)Biome.Mountain) != 0) num++;
            if ((m & (int)Biome.BlackForest) != 0) num++;
            if ((m & (int)Biome.Plains) != 0) num++;
            if ((m & (int)Biome.AshLands) != 0) num++;
            if ((m & (int)Biome.DeepNorth) != 0) num++;
            if ((m & (int)Biome.Ocean) != 0) num++;
            if ((m & (int)Biome.Mistlands) != 0) num++;

            int num2 = rnd.Range(0, num - 1);
            if ((m & (int)Biome.Meadows) != 0 && num2-- == 0) return Biome.Meadows;
            if ((m & (int)Biome.Swamp) != 0 && num2-- == 0) return Biome.Swamp;
            if ((m & (int)Biome.Mountain) != 0 && num2-- == 0) return Biome.Mountain;
            if ((m & (int)Biome.BlackForest) != 0 && num2-- == 0) return Biome.BlackForest;
            if ((m & (int)Biome.Plains) != 0 && num2-- == 0) return Biome.BlackForest;   // sic
            if ((m & (int)Biome.AshLands) != 0 && num2-- == 0) return Biome.AshLands;
            if ((m & (int)Biome.DeepNorth) != 0 && num2-- == 0) return Biome.DeepNorth;
            if ((m & (int)Biome.Meadows) != 0 && num2-- == 0) return Biome.Ocean;        // sic: tests Meadows
            return Biome.Mistlands;
        }

        /// <summary>
        /// <c>GetRandomPointByBiomes</c>: one <see cref="RandomBiomeFromBiomes"/> draw (when the mask is
        /// multi-bit) plus exactly one list-index draw. Returns a packed grid point.
        /// </summary>
        public int GetRandomPointByBiomes(UnityRandom rnd, Biome biome)
            => GetRandomPointByBiome(rnd, RandomBiomeFromBiomes(rnd, biome));

        /// <summary>
        /// <c>GetRandomPointByBiomesAboveSeaLevel</c>. The empty-list fallback tests the count BEFORE
        /// drawing, so the number of draws is the same either way: exactly one.
        /// </summary>
        public int GetRandomPointByBiomesAboveSeaLevel(UnityRandom rnd, Biome biome)
        {
            Biome b = RandomBiomeFromBiomes(rnd, biome);
            BiomeTypeInfo info = m_lookup[b];
            if (info.AllPointsAboveSeaLevel.Count == 0) return GetRandomPointByBiome(rnd, b);
            return info.AllPointsAboveSeaLevel[rnd.Range(0, info.AllPointsAboveSeaLevel.Count)];
        }

        private int GetRandomPointByBiome(UnityRandom rnd, Biome b)
        {
            BiomeTypeInfo info = m_lookup[b];
            if (info.AllPoints.Count == 0)
                throw new InvalidOperationException(
                    "AllPoints is empty for " + b + ": the game would draw Range(0, 0) and then throw "
                    + "IndexOutOfRange inside List<T>.get_Item. Not reachable for a real world.");
            return info.AllPoints[rnd.Range(0, info.AllPoints.Count)];
        }

        /// <summary>Counts of every biome's point lists - the cheap dump cross-check of spec 02 section 10.4.</summary>
        public string PointListSummary()
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            foreach (BiomeTypeInfo info in m_byKey)
                sb.Append(info.Biome).Append(": sectors=").Append(info.Sectors.Count)
                  .Append(" points=").Append(info.AllPoints.Count)
                  .Append(" aboveSea=").Append(info.AllPointsAboveSeaLevel.Count).Append('\n');
            return sb.ToString();
        }
    }
}
