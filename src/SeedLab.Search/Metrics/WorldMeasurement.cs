using System;
using System.Collections.Generic;
using SeedLab.Render;
using SeedLab.WorldGen;

namespace SeedLab.Search.Metrics
{
    /// <summary>
    /// Every number the search can compute for one world, measured on one stated grid.
    ///
    /// <para><b>Counting discipline.</b> Areas are accumulated as <c>long</c> cell counts and converted
    /// to m^2 exactly once, at the end. Summing floats in a parallel loop makes the answer depend on
    /// the order the loop happened to run in, which would break byte-for-byte reproducibility
    /// (07-features.md section 3.5).</para>
    ///
    /// <para>Definitions are 07-features.md section 2, and they are ours, so they travel with every
    /// number: land is <c>height &gt;= 30 m</c>, an island is a <b>4-connected</b> component of land
    /// cells, island area is <c>cells * spacing^2</c>, and the spawn island is the component nearest
    /// the origin (rule 2), reported as absent when the nearest land is over 500 m away (rule 3).</para>
    /// </summary>
    public sealed class WorldMeasurement
    {
        /// <summary>07-features.md section 2.3: beyond this, "the spawn area is at sea".</summary>
        public const double SpawnSearchRadiusM = 500.0;

        private readonly FieldGrid _grid;
        private int[] _label = Array.Empty<int>();
        private int[] _stack = Array.Empty<int>();
        private readonly List<long> _components = new List<long>();

        public WorldMeasurement(FieldGrid grid)
        {
            _grid = grid;
        }

        public FieldGrid Grid => _grid;

        public double CellArea => _grid.CellArea;

        // ---- biome pass ------------------------------------------------------------------------------
        public long InWorldCells;

        /// <summary>Per <c>Heightmap.BiomeIndex</c> 0..9.</summary>
        public readonly long[] BiomeCells = new long[10];

        /// <summary>Per biome: squared distance from the origin to the nearest sampled cell centre.</summary>
        public readonly double[] NearestD2 = new double[10];

        /// <summary>Per biome: the largest 4-connected patch, in cells. -1 when not requested.</summary>
        public readonly long[] LargestPatchCells = new long[10];

        public readonly List<long> AreaWithinCells = new List<long>();

        // ---- height pass -----------------------------------------------------------------------------
        public long LandCells;
        public long WaterCells;
        public readonly long[] BiomeLandCells = new long[10];
        public readonly List<long> AreaAboveCells = new List<long>();
        public readonly List<long> LandAreaWithinCells = new List<long>();
        public float PeakHeight = float.NegativeInfinity;
        public float PeakX, PeakZ;

        /// <summary>Per requested <c>shore_area_within</c> radius: land cells within 100 m of water.</summary>
        public readonly List<long> ShoreWithinCells = new List<long>();

        /// <summary>Per requested <c>min_area</c>: components of at least that area.</summary>
        public readonly List<int> IslandCounts = new List<int>();

        public long LargestIslandCells;
        public long SpawnIslandCells;
        public bool SpawnIslandExists;
        public double NearestLandD2 = double.PositiveInfinity;

        // ---- structures (free once the generator exists) ----------------------------------------------
        public int RiverCount = -1;
        public int LakeCount = -1;
        public int StreamCount = -1;

        public void Reset(MeasurementPlan plan)
        {
            InWorldCells = 0;
            Array.Clear(BiomeCells);
            Array.Clear(BiomeLandCells);
            for (int i = 0; i < 10; i++)
            {
                NearestD2[i] = double.PositiveInfinity;
                LargestPatchCells[i] = -1;
            }

            AreaWithinCells.Clear();
            for (int i = 0; i < plan.AreaWithin.Count; i++) AreaWithinCells.Add(0);
            AreaAboveCells.Clear();
            for (int i = 0; i < plan.AreaAbove.Count; i++) AreaAboveCells.Add(0);
            LandAreaWithinCells.Clear();
            for (int i = 0; i < plan.LandAreaWithin.Count; i++) LandAreaWithinCells.Add(0);
            IslandCounts.Clear();
            for (int i = 0; i < plan.IslandMinAreas.Count; i++) IslandCounts.Add(0);
            ShoreWithinCells.Clear();
            for (int i = 0; i < plan.ShoreWithin.Count; i++) ShoreWithinCells.Add(0);

            LandCells = 0;
            WaterCells = 0;
            PeakHeight = float.NegativeInfinity;
            PeakX = 0;
            PeakZ = 0;
            LargestIslandCells = 0;
            SpawnIslandCells = 0;
            SpawnIslandExists = false;
            NearestLandD2 = double.PositiveInfinity;
            RiverCount = LakeCount = StreamCount = -1;
        }

        /// <summary>The biome pass: counts, nearest distances and every requested "area within R".</summary>
        public void MeasureBiomes(SeedSampler s, MeasurementPlan plan)
        {
            int n = _grid.Size;
            int nw = plan.AreaWithin.Count;
            Span<double> within2 = nw <= 16 ? stackalloc double[16] : new double[nw];
            for (int i = 0; i < nw; i++) within2[i] = plan.AreaWithin[i].Radius * plan.AreaWithin[i].Radius;

            for (int row = 0; row < n; row++)
            {
                if (!s.RowSpan(row, s.SampledRadius, out int lo, out int hi)) continue;
                double wz = _grid.WorldZ(row);
                int b = row * n;
                for (int col = lo; col < hi; col++)
                {
                    byte bi = s.Biome[b + col];
                    if (bi == SeedSampler.Unsampled) continue;
                    double wx = _grid.WorldX(col);
                    double d2 = wx * wx + wz * wz;

                    InWorldCells++;
                    BiomeCells[bi]++;
                    if (d2 < NearestD2[bi]) NearestD2[bi] = d2;

                    for (int i = 0; i < nw; i++)
                    {
                        (int wb, _) = plan.AreaWithin[i];
                        if (d2 <= within2[i] && (wb < 0 || wb == bi)) AreaWithinCells[i]++;
                    }
                }
            }

            for (int bi = 0; bi < 10; bi++)
            {
                if (plan.NeedLargestPatch[bi]) LargestPatchCells[bi] = LargestComponent(s, bi);
            }
        }

        /// <summary>The height pass: land, peaks, "area above H", the shore band and the island analysis.</summary>
        public void MeasureHeights(SeedSampler s, MeasurementPlan plan)
        {
            int n = _grid.Size;
            int na = plan.AreaAbove.Count;
            int nl = plan.LandAreaWithin.Count;
            Span<double> land2 = nl <= 16 ? stackalloc double[16] : new double[nl];
            for (int i = 0; i < nl; i++) land2[i] = plan.LandAreaWithin[i] * plan.LandAreaWithin[i];

            for (int row = 0; row < n; row++)
            {
                if (!s.RowSpan(row, s.SampledRadius, out int lo, out int hi)) continue;
                double wz = _grid.WorldZ(row);
                int b = row * n;
                for (int col = lo; col < hi; col++)
                {
                    int k = b + col;
                    byte bi = s.Biome[k];
                    if (bi == SeedSampler.Unsampled) continue;
                    float h = s.Height[k];
                    double wx = _grid.WorldX(col);

                    if (h >= SeedSampler.WaterLevel)
                    {
                        LandCells++;
                        BiomeLandCells[bi]++;
                        double d2 = wx * wx + wz * wz;
                        if (d2 < NearestLandD2) NearestLandD2 = d2;
                        for (int i = 0; i < nl; i++)
                        {
                            if (d2 <= land2[i]) LandAreaWithinCells[i]++;
                        }
                    }
                    else
                    {
                        WaterCells++;
                    }

                    if (h > PeakHeight) { PeakHeight = h; PeakX = (float)wx; PeakZ = (float)wz; }

                    for (int i = 0; i < na; i++)
                    {
                        (int ab, double ah) = plan.AreaAbove[i];
                        if (h > ah && (ab < 0 || ab == bi)) AreaAboveCells[i]++;
                    }
                }
            }

            if (plan.ShoreWithin.Count > 0) MeasureShore(s, plan);
            if (plan.NeedIslands) MeasureIslands(s, plan);
        }

        // ---- the shore band -------------------------------------------------------------------------

        /// <summary>The definitional width of the shore band, metres. Never a query parameter.</summary>
        public const double ShoreBandM = 100.0;

        private long[] _shoreD2 = Array.Empty<long>();
        private long[] _shoreF = Array.Empty<long>();
        private long[] _shoreOut = Array.Empty<long>();
        private int[] _shoreV = Array.Empty<int>();
        private double[] _shoreZ = Array.Empty<double>();

        /// <summary>
        /// <c>shore_area_within</c>: land cells whose centre lies within <see cref="ShoreBandM"/> of the
        /// centre of a water cell, counted inside each requested radius.
        ///
        /// <para><b>Exact, not a chamfer.</b> The distance to the nearest water cell is a
        /// Felzenszwalb-Huttenlocher squared-Euclidean distance transform - one vertical pass and one
        /// lower-envelope pass, both linear - so the value is the true minimum over every water cell on
        /// the grid. Distances are carried as <c>long</c> squared CELL counts and converted to metres
        /// once, for the same reason every area here is a cell count: a float sum would make the answer
        /// depend on the order the loop happened to run in.</para>
        ///
        /// <para><b>Why an unsampled cell may be treated as "not water".</b>
        /// <c>CompiledQuery.Compile</c> sizes this goal's disc at <c>radius + 100 m</c>, so every cell
        /// inside the band of a land cell this metric counts was sampled. A cell outside the disc can
        /// therefore never be the nearest water cell of a counted cell, and ignoring it cannot lose a
        /// source that mattered.</para>
        ///
        /// <para>Measured cost: 116.4 ms/seed at G12 over the whole world against 2,818.6 ms for the T3
        /// sampling pass it rides on - +3.5 % (docs\studies\coastline-verdict.md section 2, 2026-09-23).</para>
        /// </summary>
        private void MeasureShore(SeedSampler s, MeasurementPlan plan)
        {
            int n = _grid.Size;
            double spacing = _grid.Spacing;

            // The box that holds every sampled cell. With a region restriction that box is far smaller
            // than the grid, and the transform then costs the box rather than the world.
            double r = s.SampledRadius;
            int c0 = Math.Max(0, ColumnAtOrAfter(-r));
            int c1 = Math.Min(n, ColumnAtOrBefore(r) + 1);
            int r0 = c0, r1 = c1;                    // the grid is square and centred on the origin
            int bw = c1 - c0, bh = r1 - r0;
            if (bw <= 0 || bh <= 0) return;

            int longest = Math.Max(bw, bh);
            if (_shoreD2.Length < bw * bh) _shoreD2 = new long[bw * bh];
            if (_shoreF.Length < longest) _shoreF = new long[longest];
            if (_shoreOut.Length < longest) _shoreOut = new long[longest];
            if (_shoreV.Length < longest) _shoreV = new int[longest];
            if (_shoreZ.Length < longest + 1) _shoreZ = new double[longest + 1];

            // A finite sentinel larger than any achievable squared distance in the box, so the
            // parabola intersections below stay exact arithmetic instead of producing a NaN on a
            // column that holds no water at all.
            long inf = (long)bw * bw + (long)bh * bh + 1;

            // ---- pass 1: squared distance to the nearest water cell in the same column ---------------
            for (int col = 0; col < bw; col++)
            {
                int gcol = c0 + col;
                float wx = _grid.WorldX(gcol);
                long d = inf;
                for (int row = 0; row < bh; row++)
                {
                    if (IsWater(s, (r0 + row) * n + gcol, wx, _grid.WorldZ(r0 + row))) d = 0;
                    else if (d < inf) d++;
                    _shoreD2[row * bw + col] = d;
                }

                d = inf;
                for (int row = bh - 1; row >= 0; row--)
                {
                    if (IsWater(s, (r0 + row) * n + gcol, wx, _grid.WorldZ(r0 + row))) d = 0;
                    else if (d < inf) d++;
                    long best = _shoreD2[row * bw + col];
                    if (d < best) best = d;
                    _shoreD2[row * bw + col] = best >= inf ? inf : best * best;
                }
            }

            // ---- pass 2: the lower envelope across each row -------------------------------------------
            for (int row = 0; row < bh; row++)
            {
                int b = row * bw;
                for (int col = 0; col < bw; col++) _shoreF[col] = _shoreD2[b + col];
                Envelope(_shoreF, bw, inf);
                for (int col = 0; col < bw; col++) _shoreD2[b + col] = _shoreOut[col];
            }

            // ---- count, per requested radius ------------------------------------------------------------
            double band2 = ShoreBandM * ShoreBandM;
            double cellArea = spacing * spacing;
            for (int i = 0; i < plan.ShoreWithin.Count; i++)
            {
                double radius = plan.ShoreWithin[i];
                double rad2 = radius * radius;
                long count = 0;
                for (int row = 0; row < bh; row++)
                {
                    double wz = _grid.WorldZ(r0 + row);
                    int b = row * bw;
                    int gb = (r0 + row) * n;
                    for (int col = 0; col < bw; col++)
                    {
                        if (!s.IsLand(gb + c0 + col)) continue;
                        double wx = _grid.WorldX(c0 + col);
                        if (wx * wx + wz * wz > rad2) continue;
                        long d2 = _shoreD2[b + col];
                        if (d2 < inf && d2 * cellArea <= band2) count++;
                    }
                }

                ShoreWithinCells[i] = count;
            }
        }

        /// <summary>
        /// A source cell for the shore transform: sampled water, or anything past the 10,500 m edge.
        ///
        /// <para>The second clause is not a convenience - it is the generator's own fact.
        /// <c>GetBiomeHeight</c>'s first statement returns <c>-2 * GetHeightMultiplier()</c> (-400 m)
        /// beyond 10,500 m in every seed, so a cell out there IS water and a land cell on the rim is
        /// genuinely shore. Cells that are unsampled only because of the query's region restriction
        /// are not sources - and cannot matter, because <c>CompiledQuery</c> gives this metric a disc
        /// of <c>radius + 100 m</c>, so every cell inside the band of a counted land cell is either
        /// sampled or past the edge.</para>
        /// </summary>
        private static bool IsWater(SeedSampler s, int k, float wx, float wz)
        {
            if (s.Biome[k] != SeedSampler.Unsampled) return s.Height[k] < SeedSampler.WaterLevel;
            return DUtils.Length(wx, wz) > 10500f;
        }

        /// <summary>
        /// The lower envelope of the parabolas <c>f(q) + (x - q)^2</c>: Felzenszwalb-Huttenlocher 2012,
        /// figure 3. Reads <paramref name="f"/> and writes <c>_shoreOut</c>.
        /// </summary>
        private void Envelope(long[] f, int len, long inf)
        {
            int[] v = _shoreV;
            double[] z = _shoreZ;
            int k = 0;
            v[0] = 0;
            z[0] = double.NegativeInfinity;
            z[1] = double.PositiveInfinity;

            for (int q = 1; q < len; q++)
            {
                // z[0] is -infinity and every intersection below is finite (inf is a finite
                // sentinel, so an all-sentinel column gives (q + p) / 2, never a NaN), so this loop
                // always terminates at or above k = 0.
                double intersect = Intersect(f, q, v[k]);
                while (intersect <= z[k])
                {
                    k--;
                    intersect = Intersect(f, q, v[k]);
                }

                k++;
                v[k] = q;
                z[k] = intersect;
                z[k + 1] = double.PositiveInfinity;
            }

            int top = k;
            k = 0;
            for (int q = 0; q < len; q++)
            {
                while (k < top && z[k + 1] < q) k++;
                int p = v[k];
                long d = f[p] + (long)(q - p) * (q - p);
                _shoreOut[q] = d >= inf ? inf : d;
            }
        }

        private static double Intersect(long[] f, int q, int p)
            => ((double)(f[q] + (long)q * q) - (double)(f[p] + (long)p * p)) / (2.0 * (q - p));

        /// <summary>The first column whose centre is at or after <paramref name="x"/> metres.</summary>
        private int ColumnAtOrAfter(double x)
            => (int)Math.Ceiling((x - _grid.Spacing / 2.0) / _grid.Spacing + _grid.Size / 2.0);

        /// <summary>The last column whose centre is at or before <paramref name="x"/> metres.</summary>
        private int ColumnAtOrBefore(double x)
            => (int)Math.Floor((x - _grid.Spacing / 2.0) / _grid.Spacing + _grid.Size / 2.0);

        // ---- connected components ---------------------------------------------------------------------

        private void EnsureScratch()
        {
            if (_label.Length != _grid.Count) _label = new int[_grid.Count];
            if (_stack.Length < 4096) _stack = new int[4096];
        }

        /// <summary>
        /// 4-connected components of land cells. Never 8-connected: a diagonal contact would bridge two
        /// islands across a one-cell channel (07-features.md section 2.2).
        /// </summary>
        private void MeasureIslands(SeedSampler s, MeasurementPlan plan)
        {
            EnsureScratch();
            Array.Fill(_label, -1);
            _components.Clear();

            int n = _grid.Size;
            double bestD2 = double.PositiveInfinity;
            int bestComponent = -1;

            for (int start = 0; start < _label.Length; start++)
            {
                if (_label[start] >= 0 || !s.IsLand(start)) continue;

                int id = _components.Count;
                long cells = 0;
                _sp = 0;
                _stack[_sp++] = start;
                _label[start] = id;
                while (_sp > 0)
                {
                    int k = _stack[--_sp];
                    cells++;
                    int row = k / n, col = k - row * n;
                    double wx = _grid.WorldX(col), wz = _grid.WorldZ(row);
                    double d2 = wx * wx + wz * wz;
                    if (d2 < bestD2) { bestD2 = d2; bestComponent = id; }

                    if (col > 0) Push(k - 1, id, s);
                    if (col < n - 1) Push(k + 1, id, s);
                    if (row > 0) Push(k - n, id, s);
                    if (row < n - 1) Push(k + n, id, s);
                }

                _components.Add(cells);
                if (cells > LargestIslandCells) LargestIslandCells = cells;
            }

            double cellArea = _grid.CellArea;
            for (int i = 0; i < plan.IslandMinAreas.Count; i++)
            {
                double min = plan.IslandMinAreas[i];
                int count = 0;
                foreach (long c in _components)
                {
                    if (c * cellArea >= min) count++;
                }

                IslandCounts[i] = count;
            }

            if (bestComponent >= 0 && Math.Sqrt(bestD2) <= SpawnSearchRadiusM)
            {
                SpawnIslandExists = true;
                SpawnIslandCells = _components[bestComponent];
            }
        }

        private void Push(int k, int id, SeedSampler s)
        {
            if (_label[k] >= 0 || !s.IsLand(k)) return;
            _label[k] = id;
            if (_sp == _stack.Length) Array.Resize(ref _stack, _stack.Length * 2);
            _stack[_sp++] = k;
        }

        // The flood fill's stack pointer lives on the instance so Push can grow the array; the loop
        // above keeps it in sync. (A ref-local cannot be captured by a method call.)
        private int _sp;

        private long LargestComponent(SeedSampler s, int biomeIndex)
        {
            EnsureScratch();
            Array.Fill(_label, -1);
            int n = _grid.Size;
            long best = 0;
            int id = 0;

            for (int start = 0; start < _label.Length; start++)
            {
                if (_label[start] >= 0 || s.Biome[start] != biomeIndex) continue;
                long cells = 0;
                _sp = 0;
                _stack[_sp++] = start;
                _label[start] = id;
                while (_sp > 0)
                {
                    int k = _stack[--_sp];
                    cells++;
                    int row = k / n, col = k - row * n;
                    if (col > 0) PushBiome(k - 1, id, s, biomeIndex);
                    if (col < n - 1) PushBiome(k + 1, id, s, biomeIndex);
                    if (row > 0) PushBiome(k - n, id, s, biomeIndex);
                    if (row < n - 1) PushBiome(k + n, id, s, biomeIndex);
                }

                if (cells > best) best = cells;
                id++;
            }

            return best;
        }

        private void PushBiome(int k, int id, SeedSampler s, int biomeIndex)
        {
            if (_label[k] >= 0 || s.Biome[k] != biomeIndex) return;
            _label[k] = id;
            if (_sp == _stack.Length) Array.Resize(ref _stack, _stack.Length * 2);
            _stack[_sp++] = k;
        }

        /// <summary>The three pre-generated structure counts. Free: the generator already built them.</summary>
        public void MeasureStructures(WorldGeneratorPort gen)
        {
            RiverCount = gen.GetRivers()?.Count ?? 0;
            StreamCount = gen.GetStreams()?.Count ?? 0;
            LakeCount = gen.GetLakes()?.Count ?? 0;
        }

        // ---- derived, in the units the query speaks ----------------------------------------------------
        public double BiomeArea(int bi) => BiomeCells[bi] * CellArea;

        public double BiomeShare(int bi) => InWorldCells == 0 ? 0 : (double)BiomeCells[bi] / InWorldCells;

        public double NearestDistance(int bi)
            => double.IsPositiveInfinity(NearestD2[bi]) ? double.PositiveInfinity : Math.Sqrt(NearestD2[bi]);

        public double LandArea => LandCells * CellArea;

        public double WaterArea => WaterCells * CellArea;

        public double LandShare => InWorldCells == 0 ? 0 : (double)LandCells / InWorldCells;

        public double NearestLandDistance
            => double.IsPositiveInfinity(NearestLandD2) ? double.PositiveInfinity : Math.Sqrt(NearestLandD2);

    }
}
