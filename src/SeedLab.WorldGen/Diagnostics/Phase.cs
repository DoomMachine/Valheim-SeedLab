using System;
using System.Collections.Generic;

namespace SeedLab.WorldGen.Diagnostics
{
    /// <summary>
    /// The profiler's map of where one seed's time goes: a fixed id for every phase the world-building
    /// code and its callers mark with <see cref="PhaseSink.Begin"/> / <see cref="PhaseSink.End"/>.
    ///
    /// <para><b>The numbers are permanent.</b> They are the column ids of a stored per-seed profile, so a
    /// phase is never renumbered and an id is never reused for something else - a new phase is appended
    /// after the last one. 7 is a hole on purpose (it was reserved before the map was first published) and
    /// stays one.</para>
    ///
    /// <para><b>Times are inclusive.</b> A phase's time includes every phase entered inside it, and a
    /// report prints <c>other = parent - sum(children)</c>, so nothing is silently dropped. The structural
    /// parent of each phase is <see cref="PhaseMap.Parent"/>; <see cref="Construct"/> and
    /// <see cref="Pregen"/> have none of their own, because they run inside whatever built the generator
    /// (the seed itself, or <see cref="T5Construct"/> when the location oracle builds its own).</para>
    /// </summary>
    public enum Phase : ushort
    {
        /// <summary>Not a phase. Never entered; the root of a report's tree.</summary>
        None = 0,

        /// <summary><c>WorldGeneratorPort</c>'s constructor up to, not including, pre-generation.</summary>
        Construct = 1,

        /// <summary>The whole lake/river/stream pre-generation, eager or deferred - entered once per generator that pays it.</summary>
        Pregen = 2,

        /// <summary>FindLakes' 157 x 157 candidate scan.</summary>
        PregenLakesScan = 3,

        /// <summary>FindLakes' MergePoints(800).</summary>
        PregenLakesMerge = 4,

        /// <summary>PlaceRivers without its RenderRivers call: the lake-pair search.</summary>
        PregenRiversSearch = 5,

        /// <summary>RenderRivers for the rivers.</summary>
        PregenRiversRender = 6,

        // 7 is reserved and is never assigned.

        /// <summary>PlaceStreams pass 1 without its RenderRivers call: 3,000 start/end searches.</summary>
        PregenStreams1Search = 8,

        /// <summary>RenderRivers for the pass-1 streams (Deep North skipped).</summary>
        PregenStreams1Render = 9,

        /// <summary>PlaceStreams pass 2 without its RenderRivers call.</summary>
        PregenStreams2Search = 10,

        /// <summary>RenderRivers for the pass-2 streams (Deep North only).</summary>
        PregenStreams2Render = 11,

        /// <summary>The read-only views over the finished collections.</summary>
        PregenViews = 12,

        /// <summary>T2 at G12: the biome pass (sample and measure) on the game's own grid.</summary>
        T2G12 = 13,

        /// <summary>The largest-patch flood fill of the biome pass, at whatever grid it ran.</summary>
        Patch = 14,

        /// <summary>T3 at G12: the height pass (sample and measure).</summary>
        T3G12 = 15,

        /// <summary>T3 at G24.</summary>
        T3G24 = 16,

        /// <summary>T3 at G96.</summary>
        T3G96 = 17,

        /// <summary>T3 at G192.</summary>
        T3G192 = 18,

        /// <summary>T3 at G384.</summary>
        T3G384 = 19,

        /// <summary>The location oracle's own generator, constructed eagerly - its construct and pre-generation nest inside.</summary>
        T5Construct = 20,

        /// <summary>BiomeGrid.Build: the 2048^2 biome-and-height point grid.</summary>
        T5Grid = 21,

        /// <summary>AltBiomeAssignment.Generate.</summary>
        T5Alt = 22,

        /// <summary>LocationPlacementEngine.Run over the ordered prefix.</summary>
        T5Place = 23,

        /// <summary>The final harvest of the placement's instances and the copy handed back.</summary>
        T5Harvest = 24,

        /// <summary>BiomeField.Build: the sector decomposition.</summary>
        T5Field = 25,

        /// <summary>T4: the structure counts read off the pre-generated world.</summary>
        T4Measure = 26,

        /// <summary>A search's goal outcomes and score for one seed.</summary>
        Collect = 27,

        /// <summary>T2 at G24.</summary>
        T2G24 = 28,

        /// <summary>T2 at G96.</summary>
        T2G96 = 29,

        /// <summary>T2 at G192.</summary>
        T2G192 = 30,

        /// <summary>T2 at G384.</summary>
        T2G384 = 31,

        /// <summary>The shortest seed text for a seed that passed a search.</summary>
        SeedText = 32,

        /// <summary>A seed atlas row's ring binning.</summary>
        AtlasBin = 33,

        /// <summary>A seed atlas row's assembly.</summary>
        AtlasRow = 34,
    }

    /// <summary>Names, parents and grid slots of <see cref="Phase"/> - everything a report needs to print the tree.</summary>
    public static class PhaseMap
    {
        /// <summary>
        /// The size of every per-phase array. Larger than the last id so that appending a phase never
        /// changes a stored array's length.
        /// </summary>
        public const int Capacity = 64;

        // In the order a seed runs them: construction, the biome pass, pre-generation (which the first
        // height or river read triggers), the structure counts, the height pass, then the location build.
        private static readonly Phase[] s_all =
        {
            Phase.Construct,
            Phase.T2G12, Phase.T2G24, Phase.T2G96, Phase.T2G192, Phase.T2G384,
            Phase.Patch,
            Phase.Pregen,
            Phase.PregenLakesScan, Phase.PregenLakesMerge,
            Phase.PregenRiversSearch, Phase.PregenRiversRender,
            Phase.PregenStreams1Search, Phase.PregenStreams1Render,
            Phase.PregenStreams2Search, Phase.PregenStreams2Render,
            Phase.PregenViews,
            Phase.T4Measure,
            Phase.T3G12, Phase.T3G24, Phase.T3G96, Phase.T3G192, Phase.T3G384,
            Phase.T5Construct, Phase.T5Grid, Phase.T5Field, Phase.T5Alt, Phase.T5Place, Phase.T5Harvest,
            Phase.Collect, Phase.SeedText, Phase.AtlasBin, Phase.AtlasRow,
        };

        /// <summary>Every defined phase, in the order a seed runs them and a report prints them.</summary>
        public static IReadOnlyList<Phase> All => s_all;

        /// <summary>True for an id the map defines (not <see cref="Phase.None"/>, not the reserved 7).</summary>
        public static bool IsDefined(Phase p) => Array.IndexOf(s_all, p) >= 0;

        /// <summary>The short dotted name a report and a stored profile use: "pregen.streams1.search", "t3.g12".</summary>
        public static string Name(Phase p) => p switch
        {
            Phase.None => "seed",
            Phase.Construct => "construct",
            Phase.Pregen => "pregen",
            Phase.PregenLakesScan => "pregen.lakes.scan",
            Phase.PregenLakesMerge => "pregen.lakes.merge",
            Phase.PregenRiversSearch => "pregen.rivers.search",
            Phase.PregenRiversRender => "pregen.rivers.render",
            Phase.PregenStreams1Search => "pregen.streams1.search",
            Phase.PregenStreams1Render => "pregen.streams1.render",
            Phase.PregenStreams2Search => "pregen.streams2.search",
            Phase.PregenStreams2Render => "pregen.streams2.render",
            Phase.PregenViews => "pregen.views",
            Phase.T2G12 => "t2.g12",
            Phase.T2G24 => "t2.g24",
            Phase.T2G96 => "t2.g96",
            Phase.T2G192 => "t2.g192",
            Phase.T2G384 => "t2.g384",
            Phase.Patch => "patch",
            Phase.T3G12 => "t3.g12",
            Phase.T3G24 => "t3.g24",
            Phase.T3G96 => "t3.g96",
            Phase.T3G192 => "t3.g192",
            Phase.T3G384 => "t3.g384",
            Phase.T4Measure => "t4.measure",
            Phase.T5Construct => "t5.construct",
            Phase.T5Grid => "t5.grid",
            Phase.T5Field => "t5.field",
            Phase.T5Alt => "t5.alt",
            Phase.T5Place => "t5.place",
            Phase.T5Harvest => "t5.harvest",
            Phase.Collect => "collect",
            Phase.SeedText => "seedtext",
            Phase.AtlasBin => "atlas.bin",
            Phase.AtlasRow => "atlas.row",
            _ => "phase" + ((int)p).ToString(System.Globalization.CultureInfo.InvariantCulture),
        };

        /// <summary>
        /// The phase this one always runs inside, or <see cref="Phase.None"/> when it runs directly in
        /// the seed's own work. <see cref="Phase.Construct"/> and <see cref="Phase.Pregen"/> answer None:
        /// they run inside whatever built the generator, which a report knows and this map does not.
        /// </summary>
        public static Phase Parent(Phase p) => p switch
        {
            Phase.PregenLakesScan or Phase.PregenLakesMerge
                or Phase.PregenRiversSearch or Phase.PregenRiversRender
                or Phase.PregenStreams1Search or Phase.PregenStreams1Render
                or Phase.PregenStreams2Search or Phase.PregenStreams2Render
                or Phase.PregenViews => Phase.Pregen,
            _ => Phase.None,
        };

        /// <summary>The children <see cref="Phase.Pregen"/> always enters, once each, every time it runs.</summary>
        public static IReadOnlyList<Phase> PregenChildren { get; } = new[]
        {
            Phase.PregenLakesScan, Phase.PregenLakesMerge,
            Phase.PregenRiversSearch, Phase.PregenRiversRender,
            Phase.PregenStreams1Search, Phase.PregenStreams1Render,
            Phase.PregenStreams2Search, Phase.PregenStreams2Render,
            Phase.PregenViews,
        };

        /// <summary>The children the location oracle enters, once each, for every seed it places.</summary>
        public static IReadOnlyList<Phase> OracleChildren { get; } = new[]
        {
            Phase.T5Construct, Phase.T5Grid, Phase.T5Field, Phase.T5Alt, Phase.T5Place, Phase.T5Harvest,
        };

        /// <summary>The five sampling grids that have a phase slot of their own, coarse to fine.</summary>
        public static IReadOnlyList<int> ProfiledGrids { get; } = new[] { 384, 192, 96, 24, 12 };

        /// <summary>The T2 slot for a grid spacing in metres, or <see cref="Phase.None"/> for a grid without one.</summary>
        public static Phase T2ForGrid(double spacing) => spacing switch
        {
            12.0 => Phase.T2G12,
            24.0 => Phase.T2G24,
            96.0 => Phase.T2G96,
            192.0 => Phase.T2G192,
            384.0 => Phase.T2G384,
            _ => Phase.None,
        };

        /// <summary>The T3 slot for a grid spacing in metres, or <see cref="Phase.None"/> for a grid without one.</summary>
        public static Phase T3ForGrid(double spacing) => spacing switch
        {
            12.0 => Phase.T3G12,
            24.0 => Phase.T3G24,
            96.0 => Phase.T3G96,
            192.0 => Phase.T3G192,
            384.0 => Phase.T3G384,
            _ => Phase.None,
        };
    }
}
