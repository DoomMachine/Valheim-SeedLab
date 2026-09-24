using System;
using System.Collections.Generic;
using SeedLab.Render;

namespace SeedLab.Cli.Analysis
{
    /// <summary>One 4-connected component of land cells.</summary>
    public sealed class Island
    {
        public int Label;
        public long Cells;
        public double AreaM2;
        public float MinX, MaxX, MinZ, MaxZ;

        /// <summary>The cell of this island closest to the world origin, and how far it is.</summary>
        public double NearestToOriginM = double.PositiveInfinity;
        public float NearestX, NearestZ;
    }

    /// <summary>
    /// Island analysis, to the definitions in 07-features.md section 2.2, which are SeedLab's own and
    /// are therefore stated with every number they produce:
    ///
    /// <list type="bullet">
    /// <item><b>land cell</b>: inside the 10 500 m water edge and
    /// <c>GetBiomeHeight(GetBiome(x,z), x, z) &gt;= 30.0f</c>. 30 is the water level the game hard-codes
    /// in <c>Minimap.GetMaskColor</c> and <c>AltBiomeWorldData.tryFill</c>.</item>
    /// <item><b>island</b>: a 4-connected component of land cells. Never 8-connected - a diagonal
    /// contact bridges two islands across a one-cell channel.</item>
    /// <item><b>island area</b> = cells * spacing^2; <b>island count</b> = components with area at
    /// least <c>MinAreaM2</c> (default 1 ha).</item>
    /// </list>
    ///
    /// <para><b>Resolution matters and is never hidden.</b> Over a 16x change in grid spacing the raw
    /// component count moves by 45x, the >= 1 ha count by about 5 % and the total land area by 0.5 %
    /// (the measured table in 07-features.md section 2.2). So the raw count is reported as
    /// resolution-dependent, and every figure carries its grid.</para>
    /// </summary>
    public sealed class IslandAnalysis
    {
        public const double DefaultMinAreaM2 = 10_000.0;      // 1 hectare

        /// <summary>The spawn-island search radius from 07-features.md section 2.3.</summary>
        public const double SpawnSearchRadiusM = 500.0;

        private IslandAnalysis() { }

        public double MinAreaM2 { get; private set; }

        /// <summary>Every component, including ones below <see cref="MinAreaM2"/>.</summary>
        public int ComponentsAll { get; private set; }

        public int ComponentsAtLeastMin { get; private set; }

        public long LandCells { get; private set; }

        public double LandAreaM2 { get; private set; }

        public Island? Largest { get; private set; }

        /// <summary>
        /// The island at the world centre, by rule 2 of 07-features.md section 2.3: the land component
        /// whose nearest cell centre is closest to (0,0). Rule 1 (the component holding the
        /// <c>StartTemple</c> instance) needs location placement, which this build does not do, so the
        /// result is labelled accordingly. Null when the nearest land is further than
        /// <see cref="SpawnSearchRadiusM"/> - rule 3, "the spawn area is at sea".
        /// </summary>
        public Island? CentreIsland { get; private set; }

        /// <summary>Distance from (0,0) to the nearest land cell centre on this grid, whatever the rule decided.</summary>
        public double NearestLandM { get; private set; } = double.PositiveInfinity;

        public float NearestLandX { get; private set; }
        public float NearestLandZ { get; private set; }

        /// <summary>The largest components, biggest first (at most <c>top</c> of them).</summary>
        public IReadOnlyList<Island> Top { get; private set; } = Array.Empty<Island>();

        public static IslandAnalysis Compute(WorldField field, double minAreaM2 = DefaultMinAreaM2, int top = 10)
        {
            FieldGrid g = field.Grid;
            int n = g.Size;
            int[] label = new int[g.Count];
            for (int i = 0; i < label.Length; i++) label[i] = -1;

            List<Island> islands = new List<Island>();
            int[] stack = new int[1024];
            int sp;

            IslandAnalysis r = new IslandAnalysis { MinAreaM2 = minAreaM2 };

            for (int start = 0; start < g.Count; start++)
            {
                if (label[start] >= 0 || !field.IsLand(start)) continue;

                int id = islands.Count;
                Island isl = new Island
                {
                    Label = id,
                    MinX = float.MaxValue,
                    MaxX = float.MinValue,
                    MinZ = float.MaxValue,
                    MaxZ = float.MinValue,
                };
                islands.Add(isl);

                sp = 0;
                stack[sp++] = start;
                label[start] = id;
                while (sp > 0)
                {
                    int k = stack[--sp];
                    int row = k / n, col = k - row * n;
                    isl.Cells++;

                    float wx = g.WorldX(col), wz = g.WorldZ(row);
                    if (wx < isl.MinX) isl.MinX = wx;
                    if (wx > isl.MaxX) isl.MaxX = wx;
                    if (wz < isl.MinZ) isl.MinZ = wz;
                    if (wz > isl.MaxZ) isl.MaxZ = wz;
                    double d = Math.Sqrt((double)wx * wx + (double)wz * wz);
                    if (d < isl.NearestToOriginM)
                    {
                        isl.NearestToOriginM = d;
                        isl.NearestX = wx;
                        isl.NearestZ = wz;
                    }

                    // 4-connected only.
                    if (col > 0) Push(ref stack, ref sp, k - 1, label, field, id);
                    if (col < n - 1) Push(ref stack, ref sp, k + 1, label, field, id);
                    if (row > 0) Push(ref stack, ref sp, k - n, label, field, id);
                    if (row < n - 1) Push(ref stack, ref sp, k + n, label, field, id);
                }

                isl.AreaM2 = isl.Cells * g.CellArea;
            }

            r.ComponentsAll = islands.Count;
            foreach (Island i in islands)
            {
                r.LandCells += i.Cells;
                if (i.AreaM2 >= minAreaM2) r.ComponentsAtLeastMin++;
                if (r.Largest == null || i.AreaM2 > r.Largest.AreaM2) r.Largest = i;
                if (i.NearestToOriginM < r.NearestLandM)
                {
                    r.NearestLandM = i.NearestToOriginM;
                    r.NearestLandX = i.NearestX;
                    r.NearestLandZ = i.NearestZ;
                    r.CentreIsland = i;
                }
            }

            r.LandAreaM2 = r.LandCells * g.CellArea;
            if (r.NearestLandM > SpawnSearchRadiusM) r.CentreIsland = null;

            islands.Sort((a, b) => b.AreaM2.CompareTo(a.AreaM2));
            r.Top = islands.GetRange(0, Math.Min(top, islands.Count));
            return r;
        }

        private static void Push(ref int[] stack, ref int sp, int k, int[] label, WorldField field, int id)
        {
            if (label[k] >= 0 || !field.IsLand(k)) return;
            label[k] = id;
            if (sp == stack.Length) Array.Resize(ref stack, stack.Length * 2);
            stack[sp++] = k;
        }
    }
}
