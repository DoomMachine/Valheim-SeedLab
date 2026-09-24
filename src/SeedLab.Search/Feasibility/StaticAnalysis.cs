using System;
using System.Globalization;
using SeedLab.Search.Criteria;
using SeedLab.Search.Evaluation;
using SeedLab.Render;
using SeedLab.WorldGen;

namespace SeedLab.Search.Feasibility
{
    /// <summary>
    /// T0 - the free tier. It checks each goal against the fixed geometry of
    /// <c>WorldGenerator.GetBiome</c> <b>before any seed is touched</b>, so that "a Mistlands within
    /// 4 km of centre" is answered in a microsecond instead of after a week of scanning
    /// (07-features.md section 3.2.1).
    ///
    /// <para><b>Classification: EXACT.</b> It rejects a goal only when the rejection holds for every
    /// seed in the space, by a bound on the generator's own branch conditions, not by sampling. The
    /// bands are in <see cref="BiomeGeometry"/> with their source lines, and they are computed from the
    /// query's <c>worldGenVersion</c>, never hard-coded at v2.</para>
    ///
    /// <para>T0 never rejects a <i>seed</i>. It rejects a <i>query</i>, which is a different and much
    /// stronger claim, and the only reason it is allowed to is that its evidence covers all seeds at
    /// once. A goal it cannot decide is passed through untouched.</para>
    /// </summary>
    public static class StaticAnalysis
    {
        /// <summary>Null when the goal might be satisfiable; otherwise the geometric reason no seed can satisfy it.</summary>
        public static string? Check(CompiledGoal cg, Biome biome, int genVersion, FieldGrid grid)
        {
            Goal g = cg.Goal;
            if (cg.Def.Kind != TargetKind.Biome) return null;

            (double min, double max) = BiomeGeometry.Band(biome, genVersion);
            string evidence = BiomeGeometry.Evidence(biome, genVersion);

            switch (cg.Def.Name)
            {
                case "nearest_distance":
                    // "nearest <= D" is impossible when the biome cannot exist within D at all.
                    if ((g.Test == GoalTest.Near || g.Test == GoalTest.AtMost) && g.Value < min)
                    {
                        return biome + " can never be closer than " + M(min) + " to the centre, so '"
                               + M(g.Value) + " or nearer' is impossible for every seed. " + evidence;
                    }

                    // "nearest >= D" is impossible when the biome cannot exist beyond D at all... but
                    // the metric is the NEAREST one, so a far-away goal is satisfied by the absence of
                    // any nearer cell. Only the upper edge of the band can make it impossible, and only
                    // when D exceeds the furthest the biome can ever be AND the biome must exist.
                    if ((g.Test == GoalTest.Far || g.Test == GoalTest.AtLeast) && g.Value > max
                        && !double.IsPositiveInfinity(max))
                    {
                        return null;   // vacuously satisfiable: a world with no such biome scores infinity
                    }

                    if (g.Test == GoalTest.Between && g.Value > max)
                    {
                        return biome + " can never be further than " + M(max) + " from the centre, so a range "
                               + "starting at " + M(g.Value) + " is impossible for every seed. " + evidence;
                    }

                    return null;

                case "area_within":
                    if (g.Radius < min && (g.Test == GoalTest.AtLeast || g.Test == GoalTest.Far) && g.Value > 0)
                    {
                        return biome + " can never be closer than " + M(min) + " to the centre, so there is no "
                               + biome + " inside " + M(g.Radius) + " in any seed. " + evidence;
                    }

                    return TooMuchArea(g, biome, grid, min, max, g.Radius, evidence,
                                       "within " + M(g.Radius) + " of the centre");

                // Every one of these is bounded above by the area of the biome's own distance band:
                // largest_patch and land_area are subsets of the biome's cells, and area_above_height
                // is a subset again. The bound is computed on the query's grid, so it is exact.
                case "area":
                case "largest_patch_area":
                case "land_area":
                case "area_above_height":
                    return TooMuchArea(g, biome, grid, min, max, double.PositiveInfinity, evidence,
                                       "anywhere in the world");

                default:
                    return null;
            }
        }

        /// <summary>
        /// Rejects an "at least V m^2" goal when V is larger than the biome's distance band can hold on
        /// this grid. EXACT: <see cref="BiomeGeometry.MaxArea"/> is a sound upper bound on the measured
        /// value for every seed, so a goal above it is satisfied by none of them.
        /// </summary>
        private static string? TooMuchArea(Goal g, Biome biome, FieldGrid grid, double min, double max,
                                           double radiusLimit, string evidence, string where)
        {
            // The LOW end of the range, for every test. A goal is unsatisfiable only when no achievable
            // value can satisfy it, and the achievable values are [0, cap]. 'between a .. b' is
            // therefore satisfiable whenever a <= cap, however large b is: rejecting on b would refuse
            // "Meadows area between 1 km2 and 1,000,000 km2", which every seed in fact satisfies.
            double want = g.Value;
            if (g.Test != GoalTest.AtLeast && g.Test != GoalTest.Far && g.Test != GoalTest.Between) return null;
            if (!(want > 0)) return null;

            double cap = BiomeGeometry.MaxArea(grid, min, max, radiusLimit);
            if (want <= cap) return null;

            return biome + " occupies at most " + Km2(cap) + " " + where + " on this grid - it is confined to "
                   + M(min) + " .. " + M(Math.Min(max, BiomeGeometry.WaterEdge)) + " from the centre - so "
                   + Km2(want) + " is impossible for every seed. " + evidence;
        }

        private static string Km2(double m2)
            => (m2 / 1e6).ToString("N2", CultureInfo.InvariantCulture) + " km2";

        private static string M(double v) => v.ToString("N0", CultureInfo.InvariantCulture) + " m";
    }
}
