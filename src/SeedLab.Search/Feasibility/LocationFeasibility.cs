using System;
using System.Collections.Generic;
using System.Globalization;
using SeedLab.Search.Criteria;
using SeedLab.Search.Evaluation;
using SeedLab.Search.Locations;
using SeedLab.WorldGen;

namespace SeedLab.Search.Feasibility
{
    /// <summary>
    /// T0 for location goals: what the dumped <c>ZoneLocation</c> parameters make impossible <b>for
    /// every seed</b>, decided before a single world is built.
    ///
    /// <para><b>Classification: EXACT.</b> Every bound here comes from a test the game itself applies
    /// to every point it accepts, or from <see cref="BiomeGeometry"/>'s branch-condition bands, and is
    /// taken in the direction that can only widen the set of achievable values. A goal is called
    /// impossible only when the achievable set cannot meet it at all.</para>
    ///
    /// <para><b>The three facts everything is built from</b>, all in
    /// <c>ZoneSystem.GenerateLocationsTimeSliced</c> / <c>PlaceLocations</c> and reproduced in
    /// <c>LocationPlacementEngine.TryPoint</c>:</para>
    /// <list type="number">
    /// <item><b>Quantity is a hard cap.</b> The loop is
    /// <c>while (i &lt; attempts &amp;&amp; placed &lt; m_quantity)</c> and instances are only ever
    /// appended, so a world holds at most <c>m_quantity</c> of a type. (It can hold fewer; the cap is
    /// one-sided, which is exactly what a "at least N" rejection needs.)</item>
    /// <item><b>Filter 1 is a hard distance ring.</b> Any accepted point has
    /// <c>Vector3(x, 0, z).magnitude &gt;= m_minDistance</c> when <c>m_minDistance != 0</c>, and
    /// <c>&lt;= m_maxDistance</c> when <c>m_maxDistance != 0</c>. Filter 5 adds the same with
    /// <c>Utils.LengthXZ</c> and <c>m_min/maxDistanceFromCenter</c>. Both are intersections, so taking
    /// the tightest applicable pair is sound.</item>
    /// <item><b>Filter 2 is a hard biome test.</b> An accepted point satisfies
    /// <c>(m_biome &amp; GetBiome(x, z)) != 0</c>, so the point lies in one of the mask's biomes and
    /// <see cref="BiomeGeometry.Band"/>'s all-seed band for that biome applies.</item>
    /// </list>
    ///
    /// <para><b>Slack.</b> The game compares float32 magnitudes; this engine reports a double distance.
    /// The two differ by at most a float ULP - about 1.2 mm at 10 km - and <see cref="Slack"/> is a
    /// whole metre, so no float rounding can turn a true rejection into a false one.</para>
    /// </summary>
    public static class LocationFeasibility
    {
        /// <summary>The margin every bound is relaxed by before it is allowed to reject. Metres.</summary>
        public const double Slack = 1.0;

        /// <summary>
        /// The closest to the origin an instance of <paramref name="t"/> can ever be, over all seeds.
        /// Zero when nothing constrains it.
        /// </summary>
        public static double LowerBound(LocationTypeInfo t, int genVersion)
        {
            double lo = 0.0;
            if (t.MinDistance != 0f) lo = Math.Max(lo, t.MinDistance);
            if (t.MinDistanceFromCenter > 0f) lo = Math.Max(lo, t.MinDistanceFromCenter);

            // Filter 2: the point is in ONE of the mask's biomes, so the weakest band in the mask wins.
            double band = BiomeBandMin(t.Biome, genVersion);
            return Math.Max(lo, band);
        }

        /// <summary>
        /// The furthest from the origin an instance of <paramref name="t"/> can ever be, over all
        /// seeds. <see cref="double.PositiveInfinity"/> when nothing constrains it beyond the world.
        /// </summary>
        public static double UpperBound(LocationTypeInfo t, int genVersion)
        {
            double hi = BiomeGeometry.WaterEdge;   // GetBiome returns Ocean beyond it, and nothing places in it
            if (t.MaxDistance != 0f) hi = Math.Min(hi, t.MaxDistance);
            if (t.MaxDistanceFromCenter > 0f) hi = Math.Min(hi, t.MaxDistanceFromCenter);
            return Math.Min(hi, BiomeBandMax(t.Biome, genVersion));
        }

        /// <summary>The smallest <see cref="BiomeGeometry.Band"/> lower edge over the biomes in a mask.</summary>
        public static double BiomeBandMin(Biome mask, int genVersion)
        {
            double best = double.PositiveInfinity;
            foreach (Biome b in Each(mask)) best = Math.Min(best, BiomeGeometry.Band(b, genVersion).Min);
            return double.IsPositiveInfinity(best) ? 0.0 : best;
        }

        /// <summary>The largest <see cref="BiomeGeometry.Band"/> upper edge over the biomes in a mask.</summary>
        public static double BiomeBandMax(Biome mask, int genVersion)
        {
            double best = 0.0;
            bool any = false;
            foreach (Biome b in Each(mask)) { any = true; best = Math.Max(best, BiomeGeometry.Band(b, genVersion).Max); }
            return any ? best : BiomeGeometry.WaterEdge;
        }

        private static IEnumerable<Biome> Each(Biome mask)
        {
            foreach (Biome b in new[]
            {
                Biome.Meadows, Biome.Swamp, Biome.Mountain, Biome.BlackForest, Biome.Plains,
                Biome.AshLands, Biome.DeepNorth, Biome.Ocean, Biome.Mistlands,
            })
            {
                if (((int)mask & (int)b) != 0) yield return b;
            }
        }

        /// <summary>
        /// Null when some seed might satisfy the goal; otherwise the reason none can.
        /// <paramref name="types"/> is the goal's prefab set after group expansion.
        /// </summary>
        public static string? Check(CompiledGoal cg, IReadOnlyList<LocationTypeInfo> types, int genVersion)
        {
            Goal g = cg.Goal;
            if (types.Count == 0) return null;

            // A goal measured from the spawn point is a distance between two placed things, and the
            // origin moves with the seed, so none of the centre-relative rings below applies.
            bool fromCentre = g.From == DistanceOrigin.Center;

            // ---- 0. types that never place at all ---------------------------------------------------
            int placeable = 0, quantity = 0;
            List<string> dead = new List<string>();
            foreach (LocationTypeInfo t in types)
            {
                if (t.Placeable) { placeable++; quantity += t.Quantity; }
                else dead.Add(t.Prefab);
            }

            if (placeable == 0)
            {
                string why = dead.Count == 1
                    ? "'" + dead[0] + "' is in the game's location table but not in the list the placement "
                      + "run walks (m_enable is false, or m_quantity is 0), so no world contains one"
                    : "not one of " + string.Join(", ", dead) + " is in the list the placement run walks "
                      + "(m_enable false or m_quantity 0), so no world contains any of them";
                // Only a goal that NEEDS one is impossible; "at most 0 of them" is true everywhere.
                return WantsPresence(cg) ? why : null;
            }

            if (dead.Count > 0 && cg.Def.Name == "all_types_distance" && WantsPresence(cg))
            {
                return string.Join(", ", dead) + " never place (m_enable false, or m_quantity 0), and "
                       + "all_types_distance is the distance to the type that is furthest out - so it is "
                       + "infinity in every world and this goal can hold in none of them.";
            }

            double lo = double.PositiveInfinity, hi = 0.0;
            double loAll = 0.0;                       // the largest per-type lower bound
            string loAllPrefab = "";
            bool hiUnbounded = false;
            foreach (LocationTypeInfo t in types)
            {
                if (!t.Placeable) continue;
                double tl = LowerBound(t, genVersion), th = UpperBound(t, genVersion);
                lo = Math.Min(lo, tl);
                if (tl > loAll || loAllPrefab.Length == 0) { loAll = tl; loAllPrefab = t.Prefab; }
                if (double.IsPositiveInfinity(th)) hiUnbounded = true;
                else hi = Math.Max(hi, th);
            }

            if (hiUnbounded) hi = double.PositiveInfinity;
            string ring = Ring(types, genVersion);

            switch (cg.Def.Name)
            {
                // ---- counts ------------------------------------------------------------------------
                case "count":
                case "count_within":
                {
                    double want = Wanted(g);
                    if (double.IsNaN(want)) return null;

                    if (want > quantity)
                    {
                        return "the whole target can hold at most " + quantity.ToString(CultureInfo.InvariantCulture)
                               + " instances in any world (the sum of m_quantity over "
                               + placeable.ToString(CultureInfo.InvariantCulture) + " placed "
                               + (placeable == 1 ? "type" : "types")
                               + "; ZoneSystem's loop is 'while (i < attempts && placed < m_quantity)'), so "
                               + N(want) + " is impossible for every seed.";
                    }

                    if (cg.Def.Name == "count_within" && want > 0 && fromCentre && g.Radius + Slack < lo)
                    {
                        return "nothing in this target can be placed closer than " + M(lo)
                               + " to the centre, so a disc of " + M(g.Radius) + " holds none of it in any seed. " + ring;
                    }

                    return null;
                }

                case "types_within":
                {
                    double want = Wanted(g);
                    if (double.IsNaN(want)) return null;
                    if (want > placeable)
                    {
                        return "the target names " + placeable.ToString(CultureInfo.InvariantCulture)
                               + " prefabs that the placement run actually walks"
                               + (dead.Count > 0 ? " (" + string.Join(", ", dead) + " never place)" : "")
                               + ", so " + N(want) + " distinct types is impossible for every seed.";
                    }

                    if (want > 0 && fromCentre && g.Radius + Slack < lo)
                    {
                        return "nothing in this target can be placed closer than " + M(lo)
                               + " to the centre, so a disc of " + M(g.Radius) + " holds none of it in any seed. " + ring;
                    }

                    return null;
                }

                // ---- distances ---------------------------------------------------------------------
                // The achievable set of every distance metric here is a subset of
                // [bound, hi] together with +infinity (the empty world). 'bound' is the weakest lower
                // bound for a metric that reports ONE instance, and the strongest one for a metric that
                // has to cover every type.
                case "nearest_distance":
                case "all_candidates_distance":
                case "all_types_distance":
                {
                    if (!fromCentre) return null;
                    bool everyType = cg.Def.Name == "all_types_distance" && placeable > 1;
                    double bound = everyType ? loAll : lo;
                    string who = everyType ? loAllPrefab : "";

                    if ((g.Test == GoalTest.Near || g.Test == GoalTest.AtMost) && g.Value + Slack < bound)
                    {
                        return Impossible(bound, who, ring, g.Value);
                    }

                    if (g.Test == GoalTest.Between)
                    {
                        if (g.Max + Slack < bound) return Impossible(bound, who, ring, g.Max);

                        // Above the upper bound only +infinity is achievable, and 'between' never
                        // accepts it, so a range that starts beyond the ring is impossible.
                        if (!double.IsPositiveInfinity(hi) && g.Value > hi + Slack)
                        {
                            return "nothing in this target can be placed further than " + M(hi)
                                   + " from the centre, and a world with none of it measures infinity, which "
                                   + "a 'between' test rejects - so a range starting at " + M(g.Value)
                                   + " is impossible for every seed. " + ring;
                        }
                    }

                    // 'far'/'at_least' is always satisfiable: a world with none of the target measures
                    // +infinity. That is the documented behaviour of the metric, not an oversight.
                    return null;
                }

                default:
                    return null;
            }
        }

        private static string Impossible(double bound, string who, string ring, double asked)
        {
            string subject = who.Length > 0
                ? "'" + who + "', the tightest-ringed type in this target, cannot be placed"
                : "nothing in this target can be placed";
            return subject + " closer than " + M(bound) + " to the centre, so '"
                   + M(asked) + " or nearer' is impossible for every seed. " + ring;
        }

        /// <summary>True when the goal cannot be met by a world that holds none of the target.</summary>
        private static bool WantsPresence(CompiledGoal cg)
        {
            Goal g = cg.Goal;
            switch (cg.Def.Name)
            {
                case "count":
                case "count_within":
                case "types_within":
                    return (g.Test == GoalTest.AtLeast || g.Test == GoalTest.Far) ? g.Value > 0
                         : g.Test == GoalTest.Between && g.Value > 0;

                case "nearest_distance":
                case "all_candidates_distance":
                case "all_types_distance":
                    // An empty world measures +infinity, which only a 'near'/'at_most'/'between' rejects.
                    return g.Test == GoalTest.Near || g.Test == GoalTest.AtMost || g.Test == GoalTest.Between;

                default:
                    return false;
            }
        }

        /// <summary>The count a goal demands, or NaN when it demands no minimum.</summary>
        private static double Wanted(Goal g) => g.Test switch
        {
            GoalTest.AtLeast or GoalTest.Far => g.Value,
            GoalTest.Between => g.Value,
            _ => double.NaN,
        };

        private static string Ring(IReadOnlyList<LocationTypeInfo> types, int genVersion)
        {
            List<string> parts = new List<string>();
            foreach (LocationTypeInfo t in types)
            {
                if (!t.Placeable) continue;
                double l = LowerBound(t, genVersion), h = UpperBound(t, genVersion);
                parts.Add(t.Prefab + " " + M(l) + ".." + (double.IsPositiveInfinity(h) ? "world edge" : M(h)));
                if (parts.Count >= 8) { parts.Add("..."); break; }
            }

            return "Rings, from m_minDistance / m_maxDistance, m_min/maxDistanceFromCenter and the "
                   + "biome's own GetBiome band: " + string.Join("; ", parts) + ".";
        }

        private static string M(double v)
            => double.IsPositiveInfinity(v) ? "infinity"
             : v.ToString("N0", CultureInfo.InvariantCulture) + " m";

        private static string N(double v) => v.ToString("N0", CultureInfo.InvariantCulture);
    }
}
