using System;
using System.Collections.Generic;
using SeedLab.WorldGen;

namespace SeedLab.Search.Criteria
{
    /// <summary>
    /// The evaluation tiers of 07-features.md section 3.2, in cost order. A goal declares the cheapest
    /// tier that can answer it exactly; the evaluator runs a seed only as far up this ladder as the
    /// query actually needs.
    ///
    /// <para><b>What this build really costs, measured, not copied from the spec.</b>
    /// <c>WorldGeneratorPort</c>'s constructor always runs the full lake/river/stream pre-generation
    /// (<c>WorldGeneratorPort.cs</c> lines 227-232), so <i>every</i> tier pays it. On this machine that
    /// is 320 ms of the per-seed cost, which is more than the whole G12 biome field. The spec's tier
    /// ladder assumed a generator that could be built without pre-generation; until the port offers one,
    /// T1 and T2 are nowhere near as cheap as section 4.5 predicts, and <c>vseed search</c> prints the
    /// measured rate rather than the predicted one.</para>
    /// </summary>
    public enum Tier
    {
        /// <summary>Static analysis of the query against fixed generator geometry. No seed is touched.</summary>
        T0 = 0,

        /// <summary>A sparse subset of the definitional grid. Accept-only: it may never reject.</summary>
        T1 = 1,

        /// <summary><c>GetBiome</c> over the grid, restricted to the query's region of interest.</summary>
        T2 = 2,

        /// <summary>The T2b location-zone bound. Needs the dumped location table.</summary>
        T2b = 3,

        /// <summary><c>GetBiomeHeight</c> over the grid: land, islands, peaks, lava.</summary>
        T3 = 4,

        /// <summary>The pre-generated river/lake/stream structures themselves.</summary>
        T4 = 5,

        /// <summary>Full location placement. Needs the dumped location table.</summary>
        T5 = 6,
    }

    /// <summary>
    /// How much a filter is allowed to claim. 07-features.md section 3.1: a prefilter may reject a seed
    /// only when it evaluates the criterion's own definition exactly, or when it computes a proven bound
    /// in the safe direction.
    /// </summary>
    public enum Evidence
    {
        /// <summary>Evaluates the goal's own definition on its own grid. May accept and may reject.</summary>
        Exact,

        /// <summary>A proven one-sided bound. May decide only in the safe direction; the proof is in the code comment.</summary>
        SoundBound,

        /// <summary>No proof. Off unless <c>--approx</c>, and it taints every record it touches.</summary>
        Heuristic,
    }

    /// <summary>What a metric is measured in - printed with every number so a figure is never bare.</summary>
    public enum Unit
    {
        Metres,
        SquareMetres,
        Fraction,
        Count,
    }

    /// <summary>One metric the criteria language understands.</summary>
    public sealed class MetricDef
    {
        public MetricDef(string name, TargetKind kind, Tier tier, Unit unit, string help,
                         bool needsRadius = false, bool needsHeight = false, bool needsLocations = false,
                         string uniqueSemantics = "", bool gridComparable = true, string gridNote = "")
        {
            Name = name;
            Kind = kind;
            Tier = tier;
            Unit = unit;
            Help = help;
            NeedsRadius = needsRadius;
            NeedsHeight = needsHeight;
            NeedsLocations = needsLocations;
            UniqueSemantics = uniqueSemantics;
            GridComparable = gridComparable;
            GridNote = gridNote;
        }

        public string Name { get; }
        public TargetKind Kind { get; }

        /// <summary>The cheapest tier that answers this metric exactly.</summary>
        public Tier Tier { get; }

        public Unit Unit { get; }
        public string Help { get; }
        public bool NeedsRadius { get; }
        public bool NeedsHeight { get; }

        /// <summary>True when the metric cannot be measured at all until the dumper has run.</summary>
        public bool NeedsLocations { get; }

        /// <summary>
        /// For a location metric: exactly what the number means when the target contains an
        /// <c>m_unique</c> type, whose instances are candidates and not placements. Empty for every
        /// other metric. <c>vseed explain</c> prints this next to the goal, so a report never implies
        /// that a trader's position is known when it is not.
        /// </summary>
        public string UniqueSemantics { get; }

        /// <summary>
        /// False when the number this metric produces at one grid is <b>not</b> an approximation of the
        /// number it produces at another - a connectivity count, an extremum, or a distance whose floor
        /// is the grid itself. A results file carries <c>grid_comparable: false</c> and
        /// <see cref="GridNote"/> for these, because the failure mode is somebody comparing a G96
        /// island count with a G12 one in a month and believing the difference.
        ///
        /// <para>The seven named in the metric audit (<c>docs\studies\coastline-verdict.md</c> section
        /// 4, rule 2) plus <c>shore_area_within</c>, whose measured ratio to its own G12 value is 0.939 at
        /// G24, 0.793 at G48, 0.724 at G96 and exactly 0 at G164 and coarser.</para>
        /// </summary>
        public bool GridComparable { get; }

        /// <summary>One sentence saying why <see cref="GridComparable"/> is false. Empty when it is true.</summary>
        public string GridNote { get; }

        /// <summary>True when a smaller measured value is "better" for the usual phrasing of this metric.</summary>
        public bool IsDistance => Unit == Unit.Metres && Name.Contains("distance");
    }

    /// <summary>
    /// Every metric, with the tier that answers it and whether it is available in this build.
    /// The list is the documentation: <c>vseed search --metrics</c> prints it.
    /// </summary>
    public static class MetricCatalog
    {
        private static readonly Dictionary<string, MetricDef> ByName =
            new Dictionary<string, MetricDef>(StringComparer.Ordinal);

        public static readonly IReadOnlyList<MetricDef> All;

        static MetricCatalog()
        {
            List<MetricDef> all = new List<MetricDef>
            {
                // ---- biome metrics: the biome field alone (T2) --------------------------------------
                new MetricDef("area", TargetKind.Biome, Tier.T2, Unit.SquareMetres,
                    "area of the biome on the query grid, inside the 10,500 m water edge"),
                new MetricDef("share", TargetKind.Biome, Tier.T2, Unit.Fraction,
                    "the biome's share of all in-world cells"),
                new MetricDef("nearest_distance", TargetKind.Biome, Tier.T2, Unit.Metres,
                    "distance from the centre to the nearest cell centre of the biome",
                    gridComparable: false, gridNote: GridNotes.BiomeNearest),
                new MetricDef("largest_patch_area", TargetKind.Biome, Tier.T2, Unit.SquareMetres,
                    "the largest 4-connected patch of the biome",
                    gridComparable: false, gridNote: GridNotes.Patch),
                new MetricDef("area_within", TargetKind.Biome, Tier.T2, Unit.SquareMetres,
                    "area of the biome inside a disc of 'radius' around the centre", needsRadius: true),
                new MetricDef("present", TargetKind.Biome, Tier.T2, Unit.Count,
                    "1 when the biome has at least one cell on the grid, else 0"),

                // ---- biome metrics that also need heights (T3) ---------------------------------------
                new MetricDef("land_area", TargetKind.Biome, Tier.T3, Unit.SquareMetres,
                    "area of the biome that is land (height >= 30 m)"),
                new MetricDef("area_above_height", TargetKind.Biome, Tier.T3, Unit.SquareMetres,
                    "area of the biome above 'height' metres", needsHeight: true),

                // ---- world shape: biome field only ----------------------------------------------------
                new MetricDef("ocean_share", TargetKind.World, Tier.T2, Unit.Fraction,
                    "share of in-world cells whose biome is Ocean"),

                // ---- world shape: heights --------------------------------------------------------------
                new MetricDef("land_area", TargetKind.World, Tier.T3, Unit.SquareMetres,
                    "total land area (height >= 30 m) on the query grid"),
                new MetricDef("water_area", TargetKind.World, Tier.T3, Unit.SquareMetres,
                    "total in-world water area"),
                new MetricDef("land_share", TargetKind.World, Tier.T3, Unit.Fraction,
                    "land cells / in-world cells"),
                new MetricDef("water_share", TargetKind.World, Tier.T3, Unit.Fraction,
                    "water cells / in-world cells"),
                new MetricDef("island_count", TargetKind.World, Tier.T3, Unit.Count,
                    "4-connected land components of at least 'min_area' m^2 (default 1 ha)",
                    gridComparable: false, gridNote: GridNotes.IslandCount),
                new MetricDef("largest_island_area", TargetKind.World, Tier.T3, Unit.SquareMetres,
                    "the largest 4-connected land component",
                    gridComparable: false, gridNote: GridNotes.LargestIsland),
                new MetricDef("spawn_island_area", TargetKind.World, Tier.T3, Unit.SquareMetres,
                    "the land component nearest the centre, or 0 when the nearest land is over 500 m away",
                    gridComparable: false, gridNote: GridNotes.SpawnIsland),
                new MetricDef("land_area_within", TargetKind.World, Tier.T3, Unit.SquareMetres,
                    "land area (height >= 30 m) inside a disc of 'radius' around the centre", needsRadius: true),
                new MetricDef("nearest_land_distance", TargetKind.World, Tier.T3, Unit.Metres,
                    "distance from the centre to the nearest land cell centre",
                    gridComparable: false, gridNote: GridNotes.NearestLand),

                // The replacement for the retired coastline_length (see RetiredMetrics). It is an AREA
                // with an intrinsic 100 m length scale, so unlike a coastline it converges as the grid
                // refines instead of diverging, and inside a 1 km disc it is statistically independent
                // of land_area_within at the same radius (Spearman -0.034 over 512 seeds at G12,
                // docs\studies\coastline-verdict.md section 2) - it asks a question nothing else in the
                // catalogue asks.
                new MetricDef("shore_area_within", TargetKind.World, Tier.T3, Unit.SquareMetres,
                    "area of land (height >= 30 m) within 100 m of water, inside a disc of 'radius' "
                    + "around the centre. The 100 m band is part of the DEFINITION and is never a "
                    + "parameter. 'radius' is required and must be below 10,500 m: measured over 512 "
                    + "seeds the whole-world value has a CV of 0.022 (p90/p10 = 1.06), which is the "
                    + "same non-discrimination that retired coastline_length; inside 1 km its CV is "
                    + "0.166 and p90/p10 is 1.53",
                    needsRadius: true, gridComparable: false, gridNote: GridNotes.Shore),
                // coastline_length and deepest_point used to stand here. Both were RETIRED on
                // 2026-09-23 by the metric audit (docs\studies\coastline-verdict.md sections 1 and 3);
                // RetiredMetrics holds the NAMED refusal each one now gets, with the measurement that
                // killed it and what to use instead. A query that names one is never told "unknown
                // metric" - that would read as a typo and hide a deliberate removal.
                new MetricDef("highest_peak", TargetKind.World, Tier.T3, Unit.Metres,
                    "the highest sampled height",
                    gridComparable: false, gridNote: GridNotes.Peak),
                new MetricDef("area_above_height", TargetKind.World, Tier.T3, Unit.SquareMetres,
                    "area above 'height' metres, any biome", needsHeight: true),

                // ---- world shape: the pre-generated structures (T4, free once the seed is built) -------
                new MetricDef("river_count", TargetKind.World, Tier.T4, Unit.Count,
                    "rivers from WorldGenerator.PlaceRivers"),
                new MetricDef("lake_count", TargetKind.World, Tier.T4, Unit.Count,
                    "merged lake points from FindLakes/MergePoints"),
                new MetricDef("stream_count", TargetKind.World, Tier.T4, Unit.Count,
                    "streams from PlaceStreams pass 1 (the Deep North pass is discarded by the game)"),

                // ---- locations: everything below needs the dumped ZoneLocation table -------------------
                //
                // Three distance metrics, not one, because an m_unique type has no single position and
                // the three honest questions about its candidate set are genuinely different goals:
                //   nearest_distance         at least ONE candidate is this close
                //   all_types_distance       EVERY named type has a candidate this close
                //   all_candidates_distance  EVERY candidate is this close - so whichever one the game
                //                            keeps is too. This is the only one that GUARANTEES a
                //                            distance for a trader.
                // A goal never sees a made-up single position, and the report always names the one it
                // used (MetricDef.UniqueSemantics).
            };

            string uniqAny = "the target's m_unique types contribute their CANDIDATES; the goal means "
                             + "'at least one candidate', and the candidate the game finally keeps "
                             + "(the first zone a player generates) may be a different, further one";
            string uniqAll = "every candidate of every m_unique type in the target is included, so the "
                             + "goal holds for whichever candidate the game finally keeps";
            string uniqCount = "candidates of an m_unique type are counted individually: a trader with "
                               + "m_quantity 10 contributes up to 10, of which the game keeps exactly one";
            string uniqTypes = "an m_unique type counts as present when ANY of its candidates is inside "
                               + "the radius; the surviving candidate may be outside it";

            foreach (TargetKind kind in new[] { TargetKind.Location, TargetKind.Group })
            {
                string what = kind == TargetKind.Location ? "the prefab" : "any prefab in the group";
                all.Add(new MetricDef("nearest_distance", kind, Tier.T5, Unit.Metres,
                    "distance from the origin to the nearest instance of " + what
                    + "; +infinity when the world has none, so a 'far' goal passes on absence",
                    needsLocations: true, uniqueSemantics: uniqAny));
                all.Add(new MetricDef("all_candidates_distance", kind, Tier.T5, Unit.Metres,
                    "distance to the FURTHEST instance of " + what
                    + "; 'near D' therefore means every one of them is within D",
                    needsLocations: true, uniqueSemantics: uniqAll));
                all.Add(new MetricDef("all_types_distance", kind, Tier.T5, Unit.Metres,
                    kind == TargetKind.Location
                        ? "same as nearest_distance for a single prefab; it exists so a query can be "
                          + "rewritten between location: and group: without changing meaning"
                        : "the largest of the per-prefab nearest distances, i.e. how far out you must go "
                          + "to have reached EVERY type in the group; +infinity when one is missing",
                    needsLocations: true, uniqueSemantics: uniqAny));
                all.Add(new MetricDef("count_within", kind, Tier.T5, Unit.Count,
                    "instances of " + what + " inside 'radius'",
                    needsRadius: true, needsLocations: true, uniqueSemantics: uniqCount));
                all.Add(new MetricDef("count", kind, Tier.T5, Unit.Count,
                    "instances of " + what + " in the whole world",
                    needsLocations: true, uniqueSemantics: uniqCount));
                all.Add(new MetricDef("types_within", kind, Tier.T5, Unit.Count,
                    "how many DISTINCT prefabs of the target have at least one instance inside 'radius'",
                    needsRadius: true, needsLocations: true, uniqueSemantics: uniqTypes));
            }

            foreach (MetricDef m in all) ByName[Key(m.Kind, m.Name)] = m;
            All = all;
        }

        private static string Key(TargetKind k, string name) => k + "." + name;

        public static MetricDef? Find(TargetKind kind, string metric)
            => ByName.TryGetValue(Key(kind, metric), out MetricDef? d) ? d : null;

        /// <summary>The metric names valid for a target kind, for an error message that actually helps.</summary>
        public static IEnumerable<string> NamesFor(TargetKind kind)
        {
            foreach (MetricDef m in All)
            {
                if (m.Kind == kind) yield return m.Name;
            }
        }

        private static readonly Dictionary<string, Biome> Biomes = new Dictionary<string, Biome>(StringComparer.OrdinalIgnoreCase)
        {
            { "Meadows", Biome.Meadows },
            { "Swamp", Biome.Swamp },
            { "Mountain", Biome.Mountain },
            { "BlackForest", Biome.BlackForest },
            { "Plains", Biome.Plains },
            { "AshLands", Biome.AshLands },
            { "DeepNorth", Biome.DeepNorth },
            { "Ocean", Biome.Ocean },
            { "Mistlands", Biome.Mistlands },
        };

        public static bool TryParseBiome(string name, out Biome biome) => Biomes.TryGetValue(name, out biome);

        public static IEnumerable<string> BiomeNames => Biomes.Keys;
    }
}
