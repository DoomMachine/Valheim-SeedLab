using System;
using System.Collections.Generic;
using System.Globalization;

namespace SeedLab.Search.Criteria
{
    /// <summary>
    /// The one sentence each not-grid-comparable metric carries onto every record and into every
    /// report. They live here rather than inline in <see cref="MetricCatalog"/> so the CLI, the web
    /// API and the results file cannot word the same fact three different ways.
    ///
    /// <para>Every number in them is measured: <c>docs\studies\resolution-study.md</c>
    /// (2,560 seeds, 2026-09-23) and <c>docs\studies\coastline-verdict.md</c> (512 seeds, same day).</para>
    /// </summary>
    public static class GridNotes
    {
        public const string BiomeNearest =
            "a distance to the nearest sampled CELL CENTRE, so its floor is the grid, not the biome: "
            + "at G12 it takes one distinct value for AshLands, two for Plains and Mistlands and five "
            + "for Black Forest over 2,560 seeds, and for Meadows it is the grid's own floor of "
            + "8.485 m in 93.2 % of them. Genuine only for Mountain and Ocean";

        public const string Patch =
            "a 4-connected component, so a coarse grid bridges two patches across a one-cell channel: "
            + "median |relative error| against G12 is 0.29 % at G24 but 11.9 % at G96 and 24.2 % at "
            + "G384, and Spearman against the G12 ranking falls from 0.98 to 0.17-0.51";

        public const string IslandCount =
            "a component count, and NON-MONOTONE in the grid: the min_area 100,000 m2 count over "
            + "2,560 seeds runs 137/126/119/119/121/120/112/141/128 from G12 to G384 - it falls, "
            + "then rises. With min_area 0 it moves 77x. Spearman against G12 is 0.83 at G24. The "
            + "count also depends strongly on min_area at a fixed grid: at G12 the median is 225 at "
            + "1 ha and 138 at 10 ha (256 seeds, 2026-09-23), so a threshold is meaningless without "
            + "the min_area it was measured with";

        public const string LargestIsland =
            "a 4-connected component: coarse grids merge islands across straits. Median |relative "
            + "error| against G12 is 0.26 % at G24 but 4.92 % at G96 and 79.0 % at G384 (max 552 %), "
            + "and Spearman falls from 0.851 at G24 to 0.051 at G384";

        public const string SpawnIsland =
            "the worst-behaved metric in the tool at a coarse grid: median |relative error| against "
            + "G12 is 14.0 % at G24, 43.4 % at G96 and 70.8 % at G384, and no margin at any grid "
            + "removes the false negatives (50 % at G24 still lost 8 of 802 true matches)";

        public const string NearestLand =
            "a quantised grid distance, not a metre distance: the value equals the grid's own floor "
            + "(spacing x sqrt(2) / 2) in 70.0 % of seeds at G12 and 96.6 % at G384, where it takes "
            + "only two distinct values at all. At the floor it means 'the origin cell is land'";

        public const string Peak =
            "an extremum, which a coarse grid can only ever under-read: the median |error| against "
            + "G12 is 2.31 m at G24, 27.7 m at G96 and 116 m at G384, and 'highest_peak >= 450 m' "
            + "loses 31.7 % of its true matches at G48 and 82.3 % at G96";

        public const string Shore =
            "the 100 m band is a fixed physical width, so a grid whose cells approach it stops "
            + "resolving it: measured over 512 seeds the value is 0.939x its own G12 value at G24, "
            + "0.793x at G48, 0.724x at G96 and exactly 0 at G164 and coarser, where one cell is "
            + "wider than the band. The RANKING survives to G96 (Spearman 0.953); the NUMBER does not";
    }

    /// <summary>
    /// The measured median |relative error| of a metric against its own G12 value, per grid - so a
    /// number decided at a coarse grid can be printed with the error that grid really has instead of
    /// with an adjective.
    ///
    /// <para><b>Provenance is part of the data.</b> <see cref="Source"/> is emitted next to every
    /// figure; a grid or a metric the study did not sample returns null and the report says
    /// "unmeasured" rather than interpolating one.</para>
    /// </summary>
    public static class MetricError
    {
        /// <summary>The sentence that travels with every figure from this table.</summary>
        public const string Source = "resolution-study 2026-09-23, 2,560 seeds";

        /// <summary>The metric-audit sample, for the rows that came from it instead.</summary>
        public const string ShoreSource = "metrictruth 2026-09-23, 512 seeds";

        // resolution-study.md section 3.1 and 3.2. Keyed by grid spacing in metres; the biome-area
        // row is the WORST of the nine biomes at each grid, because a bound quoted for a metric must
        // not be the best case of the family it stands for.
        private static readonly Dictionary<double, double> BulkArea = new Dictionary<double, double>
        {
            { 24.0, 0.00103 }, { 48.0, 0.00236 }, { 96.0, 0.00716 },
            { 164.0, 0.0143 }, { 192.0, 0.0195 }, { 384.0, 0.0553 },
        };

        private static readonly Dictionary<double, double> LargestIsland = new Dictionary<double, double>
        {
            { 24.0, 0.00260 }, { 48.0, 0.0171 }, { 96.0, 0.0492 },
            { 164.0, 0.142 }, { 192.0, 0.234 }, { 384.0, 0.790 },
        };

        private static readonly Dictionary<double, double> SpawnIsland = new Dictionary<double, double>
        {
            { 24.0, 0.140 }, { 48.0, 0.336 }, { 96.0, 0.434 },
            { 164.0, 0.470 }, { 192.0, 0.522 }, { 384.0, 0.708 },
        };

        private static readonly Dictionary<double, double> Patch = new Dictionary<double, double>
        {
            { 24.0, 0.00285 }, { 48.0, 0.0312 }, { 96.0, 0.119 },
            { 164.0, 0.178 }, { 192.0, 0.196 }, { 384.0, 0.242 },
        };

        // metrictruth\shore_an.py: 1 - (value at grid / value at G12), median over 512 seeds.
        private static readonly Dictionary<double, double> Shore = new Dictionary<double, double>
        {
            { 24.0, 0.061 }, { 48.0, 0.207 }, { 96.0, 0.276 }, { 164.0, 1.0 }, { 384.0, 1.0 },
        };

        /// <summary>
        /// The median |relative error| of <paramref name="metric"/> at <paramref name="grid"/> against
        /// the game's own 12 m grid, or null when the study did not sample that pair. Zero at G12,
        /// which is the definitional grid and therefore exact by construction.
        /// </summary>
        public static double? MedianRelErrorAt(string metric, double grid, out string source)
        {
            source = Source;
            if (grid <= 12.0) return 0.0;

            Dictionary<double, double>? table = metric switch
            {
                "area" or "share" or "area_within" or "land_area" or "water_area" or "land_share"
                    or "water_share" or "ocean_share" or "land_area_within" => BulkArea,
                "largest_island_area" => LargestIsland,
                "spawn_island_area" => SpawnIsland,
                "largest_patch_area" => Patch,
                "shore_area_within" => Shore,
                _ => null,
            };

            if (metric == "shore_area_within") source = ShoreSource;
            if (table == null) return null;
            return table.TryGetValue(grid, out double v) ? v : (double?)null;
        }

        /// <summary>
        /// The smallest distance this grid can report: half a cell diagonal. A measured distance at or
        /// below it is not a measurement of the world, it is the grid's floor, and
        /// <c>RecordFormatter</c> prints it as a bound.
        /// </summary>
        public static double ResolutionFloor(double spacing) => spacing * Math.Sqrt(2.0) / 2.0;
    }

    /// <summary>
    /// A metric this build used to have and deliberately does not any more.
    ///
    /// <para>A removed metric must never come back as "'coastline_length' is not a metric for a world
    /// target": that reads as a typo, sends the user looking for the right spelling, and hides a
    /// decision. Every retired name gets its own refusal naming the measurement that killed it and
    /// what to use instead.</para>
    /// </summary>
    public sealed class RetiredMetric
    {
        public RetiredMetric(string name, TargetKind kind, string retired, string why, string instead)
        {
            Name = name;
            Kind = kind;
            Retired = retired;
            Why = why;
            Instead = instead;
        }

        public string Name { get; }
        public TargetKind Kind { get; }

        /// <summary>The date it was removed, so a query file written before it can be dated.</summary>
        public string Retired { get; }

        /// <summary>The measurement that removed it.</summary>
        public string Why { get; }

        /// <summary>What to write instead, as an edit.</summary>
        public string Instead { get; }

        public string Message =>
            "'" + Name + "' was removed from the metric catalogue on " + Retired + ". " + Why;
    }

    /// <summary>The retired-metric table. Consulted BEFORE "unknown metric" is ever considered.</summary>
    public static class RetiredMetrics
    {
        public static readonly IReadOnlyList<RetiredMetric> All = new RetiredMetric[]
        {
            new RetiredMetric(
                "coastline_length", TargetKind.World, "2026-09-23",
                "It does not distinguish Valheim worlds. Measured at G12 over 2,560 seeds its "
                + "p10/p50/p90 are 2,625,271 / 2,712,816 / 2,796,511 m, so the most coastal world in "
                + "2,560 has 8 % more coastline than the least (p90/p10 = 1.065, CV = 0.0245). A "
                + "threshold on an 8 % span is not a search criterion. It was also a fractal measured "
                + "in units of the grid - at G24 no margin below 26 % passed a single true "
                + "'coastline >= 2,750 km' match, and at G96 and coarser none under 50 % passed any - "
                + "but the fractal problem is only a ranking offset; the 8 % span is what killed it.",
                "Use 'world:shore_area_within' with a radius: the area of land within 100 m of water "
                + "inside that disc. At radius 1,000 m it has 7x the spread (CV 0.166, p90/p10 1.53), "
                + "it ranks seeds better than coastline did at every grid (Spearman 0.988/0.949/0.862 "
                + "at G24/G48/G96), and it is statistically independent of land_area_within at the "
                + "same radius, so it measures shore rather than restating how much land is nearby. "
                + "Example: { \"target\": \"world:shore_area_within\", \"metric\": "
                + "\"shore_area_within\", \"radius\": 1000, \"test\": \"at_least\", \"value\": 1000000 }"),

            new RetiredMetric(
                "deepest_point", TargetKind.World, "2026-09-23",
                "It is not a property of the seed at all - it is a property of the grid. Measured at "
                + "G12 it is -399.859 m for ALL 2,560 sampled seeds (one distinct value); at G24 it is "
                + "-396.308 m for all of them; at G384 it is -100.87 m. It reports the world-edge "
                + "height of the outermost sampled cell, which is fixed by where the grid happens to "
                + "put a cell centre near 10,500 m, and it moves by 4x across grids while never "
                + "varying by seed.",
                "There is no replacement, because there was nothing being measured. For ocean extent "
                + "use 'world:water_share' or 'biome:Ocean area'; for relief use 'world:highest_peak' "
                + "or 'world:area_above_height'."),
        };

        /// <summary>The retired entry for a name, or null when the metric was never in the catalogue.</summary>
        public static RetiredMetric? Find(TargetKind kind, string metric)
        {
            foreach (RetiredMetric r in All)
            {
                if (r.Kind == kind && string.Equals(r.Name, metric, StringComparison.Ordinal)) return r;
            }

            return null;
        }

        /// <summary>The same, ignoring the target kind - for a "did you mean" that spans targets.</summary>
        public static RetiredMetric? FindAnyKind(string metric)
        {
            foreach (RetiredMetric r in All)
            {
                if (string.Equals(r.Name, metric, StringComparison.Ordinal)) return r;
            }

            return null;
        }

        /// <summary>One line per retired metric, for <c>vseed search --metrics</c>.</summary>
        public static IEnumerable<string> Lines()
        {
            foreach (RetiredMetric r in All)
            {
                yield return r.Kind.ToString().ToLowerInvariant() + ":" + r.Name
                             + "  RETIRED " + r.Retired + " - " + r.Instead;
            }
        }

        internal static string Format(double v)
            => v.ToString("N0", CultureInfo.InvariantCulture);
    }
}
