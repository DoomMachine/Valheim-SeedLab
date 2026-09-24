using System;
using SeedLab.Render;
using SeedLab.WorldGen;

namespace SeedLab.Search.Feasibility
{
    /// <summary>
    /// The distance band each biome can possibly occupy, <b>for every seed</b>, read straight off the
    /// branch conditions of <c>WorldGenerator.GetBiome</c> and <c>GetBaseHeight</c>
    /// (07-features.md section 3.2.1; the port is <c>WorldGeneratorPort.GetBiome</c>, lines 1093-1146).
    ///
    /// <para>The wobble is <c>A = WorldAngle(x,z) * 100</c>, and <c>WorldAngle</c> is
    /// <c>sin(atan2(x,z) * 20)</c>, so <c>A ∈ [-100, 100]</c> everywhere. A band written
    /// <c>dist &gt; 6000 + A</c> is therefore satisfiable no closer than 5,900 m.</para>
    ///
    /// <para><b>Every row is worldGenVersion-dependent</b> and the query chooses the version, so these
    /// are computed from the version, never hard-coded at v2. <c>VersionSetup</c>:
    /// <c>version &lt;= 0</c> sets <c>m_minMountainDistance = 1500</c> (Mountain floor 1,100 m instead of
    /// 600 m); <c>version &lt;= 1</c> sets <c>maxMarshDistance = 8000</c> (Swamp band ends at 8 km, not
    /// 6 km). Hard-coding v2 here would reject satisfiable v0/v1 queries - a correctness bug, not an
    /// inaccuracy.</para>
    /// </summary>
    public static class BiomeGeometry
    {
        /// <summary>|WorldAngle * 100|, the wobble on every lower band bound.</summary>
        public const double Wobble = 100.0;

        /// <summary>The edge of the world: <c>GetBiomeHeight</c> returns -400 beyond it.</summary>
        public const double WaterEdge = 10500.0;

        /// <summary>The closest and furthest a cell of <paramref name="biome"/> can be from the centre.</summary>
        public static (double Min, double Max) Band(Biome biome, int genVersion)
        {
            double maxMarsh = genVersion <= 1 ? 8000.0 : 6000.0;
            double minMountain = genVersion <= 0 ? 1500.0 : 1000.0;

            switch (biome)
            {
                // if (PN(off4...) > minDarklandNoise && dist > 6000 + A && dist < 10000)
                case Biome.Mistlands:
                    return (6000.0 - Wobble, 10000.0);

                // if (PN(off1...) > 0.4 && dist > 3000 + A && dist < 8000)
                case Biome.Plains:
                    return (3000.0 - Wobble, 8000.0);

                // if (PN(off2...) > 0.4 && dist > 600 + A && dist < 6000)  ... or the dist > 5000 + A fallback
                case Biome.BlackForest:
                    return (600.0 - Wobble, WaterEdge);

                // if (PN(off0...) > 0.6 && dist > 2000 && dist < m_maxMarshDistance) - no wobble either side
                case Biome.Swamp:
                    return (2000.0, maxMarsh);

                // The final fallback, reached only when dist <= 5000 + A.
                case Biome.Meadows:
                    return (0.0, 5000.0 + Wobble);

                // GetBaseHeight caps the base at Lerp(0.28, 0.38, t) <= 0.38 while
                // LerpStep(minMountainDistance - 400, minMountainDistance, dist) == 0, i.e. below
                // minMountainDistance - 400; Mountain needs baseHeight > 0.4.
                case Biome.Mountain:
                    return (minMountain - 400.0, WaterEdge);

                // IsAshlands: |(x, z - 4000)| > 12000 + A. Closest to the origin along -z:
                // z = -(12000 - 100) + 4000 = -7900.
                case Biome.AshLands:
                    return (12000.0 - Wobble - 4000.0, WaterEdge);

                // IsDeepnorth: |(x, z + 4000)| > 12000 + A, mirrored to the north.
                case Biome.DeepNorth:
                    return (12000.0 - Wobble - 4000.0, WaterEdge);

                // baseHeight <= oceanLevel anywhere, including at the origin.
                case Biome.Ocean:
                    return (0.0, WaterEdge);

                default:
                    return (0.0, WaterEdge);
            }
        }

        /// <summary>
        /// The largest area, <b>on this exact sampling grid</b>, that a biome confined to the distance
        /// band <c>[min, max]</c> could possibly occupy: the number of cells whose centre is in the
        /// world and lies inside the band, times the cell area.
        ///
        /// <para><b>Classification: EXACT (a sound upper bound on the measured value).</b> Two things
        /// make it exact rather than approximate. First, it is computed on the grid the query is
        /// measured on and with the same in-world test the measurement uses
        /// (<c>DUtils.Length(wx, wz) &lt;= 10500f</c> at the cell centre), so there is no discretisation
        /// gap to argue about - a cell either counts in both or in neither. Second, the band bounds are
        /// taken inclusively, which can only make the bound larger, never smaller. A measured area can
        /// therefore never exceed it, for any seed.</para>
        ///
        /// <para>Cost: one pass over the grid per query, not per seed.</para>
        /// </summary>
        /// <param name="radiusLimit">
        /// For <c>area_within</c>: cells beyond this radius are not counted. Pass
        /// <see cref="double.PositiveInfinity"/> for a whole-world metric.
        /// </param>
        public static double MaxArea(FieldGrid grid, double min, double max, double radiusLimit)
        {
            double limit = Math.Min(Math.Min(max, WaterEdge), radiusLimit);
            if (limit < min) return 0.0;

            long cells = 0;
            for (int row = 0; row < grid.Size; row++)
            {
                float wz = grid.WorldZ(row);
                for (int col = 0; col < grid.Size; col++)
                {
                    float wx = grid.WorldX(col);
                    double d = DUtils.Length(wx, wz);
                    if (d > WaterEdge) continue;             // the measurement's own in-world test
                    if (d < min || d > limit) continue;
                    cells++;
                }
            }

            return cells * grid.CellArea;
        }

        /// <summary>The source line of the branch that sets the band, for an explanation the user can check.</summary>
        public static string Evidence(Biome biome, int genVersion) => biome switch
        {
            Biome.Mistlands => "GetBiome test 'dist > 6000 + A && dist < 10000', A = WorldAngle*100 in [-100,100]",
            Biome.Plains => "GetBiome test 'dist > 3000 + A && dist < 8000'",
            Biome.BlackForest => "GetBiome tests 'dist > 600 + A && dist < 6000' and the 'dist > 5000 + A' fallback",
            Biome.Swamp => "GetBiome test 'dist > 2000 && dist < m_maxMarshDistance', maxMarshDistance = "
                           + (genVersion <= 1 ? "8000 (worldGenVersion <= 1)" : "6000 (worldGenVersion 2)"),
            Biome.Meadows => "GetBiome's final fallback, reached only when dist <= 5000 + A",
            Biome.Mountain => "GetBaseHeight caps base at 0.38 below m_minMountainDistance - 400 = "
                              + ((genVersion <= 0 ? 1500.0 : 1000.0) - 400.0).ToString("0")
                              + " m, and Mountain needs base > 0.4",
            Biome.AshLands => "IsAshlands: |(x, z - 4000)| > 12000 + A, so no closer than 7,900 m from the centre",
            Biome.DeepNorth => "IsDeepnorth: |(x, z + 4000)| > 12000 + A, so no closer than 7,900 m from the centre",
            Biome.Ocean => "Ocean is reachable at any distance (baseHeight <= oceanLevel)",
            _ => "no constraint known",
        };
    }
}
