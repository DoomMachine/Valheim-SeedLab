using System;
using System.Collections.Generic;

namespace SeedLab.Search.Metrics
{
    /// <summary>
    /// Everything the compiled query needs measured, gathered once so a seed is walked as few times as
    /// possible. Two goals asking for "Swamp area within 3 km" share one accumulator; a query with no
    /// height-dependent goal never evaluates a height at all.
    /// </summary>
    public sealed class MeasurementPlan
    {
        /// <summary>The disc, in metres, that every goal in this query is contained by. 10,500 = the whole world.</summary>
        public double Radius = SeedSampler.WaterEdge;

        /// <summary>
        /// True when any goal is measured off the sampling grid at all. A query whose only goals are
        /// location goals is false here: its answer comes from the placement engine's own 2048^2 point
        /// grid, and sampling a second grid for it would be a third of a second per seed spent on
        /// numbers nothing reads.
        /// </summary>
        public bool NeedBiomes;

        /// <summary>True when any goal needs <c>GetBiomeHeight</c> (land, islands, peaks, area above a height).</summary>
        public bool NeedHeights;

        /// <summary>True when any goal needs the pre-generated river/lake/stream lists.</summary>
        public bool NeedStructures;

        /// <summary>Per biome index (game numbering 0..9): the largest 4-connected patch is wanted.</summary>
        public readonly bool[] NeedLargestPatch = new bool[10];

        public bool NeedIslands;

        /// <summary>
        /// "land cells within 100 m of water, inside radius r" - one entry per distinct radius in the
        /// query. The 100 m band is definitional (<see cref="WorldMeasurement.ShoreBandM"/>) and is
        /// never a parameter, so the radius is the whole key.
        /// </summary>
        public readonly List<double> ShoreWithin = new List<double>();

        public int AddShoreWithin(double radius)
        {
            for (int i = 0; i < ShoreWithin.Count; i++)
            {
                if (ShoreWithin[i] == radius) return i;
            }

            ShoreWithin.Add(radius);
            return ShoreWithin.Count - 1;
        }

        /// <summary>Island component-area thresholds, m^2, one per distinct <c>min_area</c> in the query.</summary>
        public readonly List<double> IslandMinAreas = new List<double>();

        /// <summary>"cells of biome b inside radius r": (biome index, radius m). Biome -1 means any land.</summary>
        public readonly List<(int Biome, double Radius)> AreaWithin = new List<(int, double)>();

        /// <summary>"cells of biome b above height h": (biome index or -1 for any, height m).</summary>
        public readonly List<(int Biome, double Height)> AreaAbove = new List<(int, double)>();

        /// <summary>"land cells inside radius r", measured in the height pass.</summary>
        public readonly List<double> LandAreaWithin = new List<double>();

        public int AddLandAreaWithin(double radius)
        {
            for (int i = 0; i < LandAreaWithin.Count; i++)
            {
                if (LandAreaWithin[i] == radius) return i;
            }

            LandAreaWithin.Add(radius);
            return LandAreaWithin.Count - 1;
        }

        public int AddAreaWithin(int biome, double radius)
        {
            for (int i = 0; i < AreaWithin.Count; i++)
            {
                if (AreaWithin[i].Biome == biome && AreaWithin[i].Radius == radius) return i;
            }

            AreaWithin.Add((biome, radius));
            return AreaWithin.Count - 1;
        }

        public int AddAreaAbove(int biome, double height)
        {
            for (int i = 0; i < AreaAbove.Count; i++)
            {
                if (AreaAbove[i].Biome == biome && AreaAbove[i].Height == height) return i;
            }

            AreaAbove.Add((biome, height));
            return AreaAbove.Count - 1;
        }

        public int AddIslandMinArea(double minArea)
        {
            NeedIslands = true;
            for (int i = 0; i < IslandMinAreas.Count; i++)
            {
                if (IslandMinAreas[i] == minArea) return i;
            }

            IslandMinAreas.Add(minArea);
            return IslandMinAreas.Count - 1;
        }
    }
}
