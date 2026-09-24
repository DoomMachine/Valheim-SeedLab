using System;
using System.Collections.Generic;
using System.Threading;

namespace SeedLab.Web.Api
{
    /// <summary>
    /// The seed panel's figures - the same measurements <c>vseed seed</c> prints, shaped for a page
    /// rather than a terminal.
    ///
    /// <para><b>Why this is a DTO and not <c>WorldSummary</c>.</b> The definitions of land, islands,
    /// the spawn island and the nearest-biome distances live once, in <c>SeedLab.Cli.Analysis</c>,
    /// and the CLI is the project that owns them. This library must not depend on the executable
    /// that references it, so the server declares the shape it needs and <c>vseed serve</c> supplies
    /// a <see cref="SeedReportProvider"/> that fills it from the real analysis. No definition is
    /// duplicated: if <c>WorldSummary</c> changes, this record is filled differently, and there is
    /// still exactly one place where "island" means something.</para>
    ///
    /// <para>Every figure carries the grid it was counted on, because a figure from a coarser grid is
    /// a different measurement and not an approximation of the fine one.</para>
    /// </summary>
    public sealed class SeedReport
    {
        public int Seed { get; init; }

        /// <summary>The text the user typed, when they typed one.</summary>
        public string? AsTyped { get; init; }

        public string ShortestText { get; init; } = "";

        public string GameStyleText { get; init; } = "";

        public int WorldGenVersion { get; init; }

        public GridInfo Grid { get; init; } = new GridInfo();

        public LandInfo Land { get; init; } = new LandInfo();

        public List<BiomeRow> Biomes { get; init; } = new List<BiomeRow>();

        public IslandInfo Islands { get; init; } = new IslandInfo();

        public OriginInfo Origin { get; init; } = new OriginInfo();

        public PointInfo Highest { get; init; } = new PointInfo();

        public PointInfo Lowest { get; init; } = new PointInfo();

        public TimingInfo Timing { get; init; } = new TimingInfo();

        public sealed class GridInfo
        {
            public double SpacingM { get; init; }

            public int Size { get; init; }

            public bool IsGameGrid { get; init; }

            public double CellAreaM2 { get; init; }

            public long CellsTotal { get; init; }

            public long CellsInWorld { get; init; }

            public long CellsOutside { get; init; }

            public double AreaSampledM2 { get; init; }

            /// <summary>Half a cell diagonal: the worst case error in any "nearest" distance here.</summary>
            public double DistanceUncertaintyM { get; init; }
        }

        public sealed class LandInfo
        {
            public double WaterLevelM { get; init; }

            public long LandCells { get; init; }

            public double LandM2 { get; init; }

            public long WaterCells { get; init; }

            public double WaterM2 { get; init; }
        }

        public sealed class BiomeRow
        {
            public string Name { get; init; } = "";

            public int Index { get; init; }

            public string Color { get; init; } = "";

            public long Cells { get; init; }

            public double AreaM2 { get; init; }

            public double Share { get; init; }

            public long LandCells { get; init; }

            public double LandM2 { get; init; }

            /// <summary>Null when the biome does not occur on this grid.</summary>
            public double? NearestM { get; init; }

            public double? NearestLandM { get; init; }
        }

        public sealed class IslandInfo
        {
            public double MinAreaM2 { get; init; }

            public int CountAtLeastMin { get; init; }

            public int ComponentsAll { get; init; }

            public double? LargestAreaM2 { get; init; }

            public double? LargestNearestM { get; init; }

            public double? NearestLandM { get; init; }

            public double NearestLandX { get; init; }

            public double NearestLandZ { get; init; }

            public IslandRow? CentreIsland { get; init; }

            public string Rule { get; init; } = "";

            public List<IslandRow> Top { get; init; } = new List<IslandRow>();
        }

        public sealed class IslandRow
        {
            public double AreaM2 { get; init; }

            public long Cells { get; init; }

            public double NearestToOriginM { get; init; }

            public double NearestX { get; init; }

            public double NearestZ { get; init; }

            public double MinX { get; init; }

            public double MaxX { get; init; }

            public double MinZ { get; init; }

            public double MaxZ { get; init; }
        }

        public sealed class OriginInfo
        {
            public string Biome { get; init; } = "";

            public double HeightM { get; init; }

            public double AboveWaterM { get; init; }

            public double ForestFactor { get; init; }

            public bool InForest { get; init; }
        }

        public sealed class PointInfo
        {
            public double HeightM { get; init; }

            public double X { get; init; }

            public double Z { get; init; }

            public string Biome { get; init; } = "";
        }

        public sealed class TimingInfo
        {
            public double FieldS { get; init; }

            public double AnalysisS { get; init; }

            public int Threads { get; init; }
        }
    }

    /// <summary>What the server asks for; the provider decides how to measure it.</summary>
    public sealed class SeedReportRequest
    {
        public int Seed { get; init; }

        public string? AsTyped { get; init; }

        public double GridSpacingM { get; init; } = 12.0;

        public double MinIslandAreaM2 { get; init; } = 10_000.0;

        public int TopIslands { get; init; } = 5;

        public int Threads { get; init; }

        public int WorldGenVersion { get; init; } = 2;
    }

    /// <summary>
    /// The one seam between the server and the analysis the CLI owns. <c>vseed serve</c> passes an
    /// implementation; a test or another host can pass its own.
    /// </summary>
    public delegate SeedReport SeedReportProvider(SeedReportRequest request, CancellationToken ct);
}
