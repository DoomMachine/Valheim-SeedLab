using System;
using System.Collections.Generic;
using System.Diagnostics;
using SeedLab.WorldGen;
using SeedLab.WorldGen.Unity;

namespace SeedLab.Locations
{
    /// <summary>Optional per-attempt callback. Null in the hot path; see <see cref="PlacementTraceRecorder"/>.</summary>
    public interface IPlacementTrace
    {
        /// <summary>
        /// One outer attempt: the candidate zone and, when it was thrown away before any point try,
        /// why. <paramref name="reason"/> is <see cref="RejectReason.None"/> when the zone was accepted
        /// and point tries follow.
        /// </summary>
        void Zone(ZoneLocationEntry e, int attempt, Vec2s zone, RejectReason reason);

        /// <summary>
        /// One point try. <paramref name="value"/> and <paramref name="threshold"/> carry the numbers
        /// the failing comparison used, so "why not here" is answerable; they are NaN when the reason
        /// has no single number.
        /// </summary>
        void Point(ZoneLocationEntry e, int attempt, int tryIndex, float x, float y, float z,
                   RejectReason reason, float value, float threshold);
    }

    /// <summary>
    /// Records the trace for ONE prefab (or all of them) into a list. Cheap enough for a single type,
    /// far too expensive for a whole run - 183 types x up to 12 000 attempts.
    /// </summary>
    public sealed class PlacementTraceRecorder : IPlacementTrace
    {
        public readonly struct ZoneEvent
        {
            public ZoneEvent(string prefab, int attempt, Vec2s zone, RejectReason reason)
            { Prefab = prefab; Attempt = attempt; Zone = zone; Reason = reason; }
            public string Prefab { get; }
            public int Attempt { get; }
            public Vec2s Zone { get; }
            public RejectReason Reason { get; }
            public override string ToString()
                => "attempt " + Attempt + " zone " + Zone + " -> " + (Reason == RejectReason.None ? "accepted" : Reason.ToString());
        }

        public readonly struct PointEvent
        {
            public PointEvent(string prefab, int attempt, int tryIndex, float x, float y, float z,
                              RejectReason reason, float value, float threshold)
            { Prefab = prefab; Attempt = attempt; TryIndex = tryIndex; X = x; Y = y; Z = z; Reason = reason; Value = value; Threshold = threshold; }
            public string Prefab { get; }
            public int Attempt { get; }
            public int TryIndex { get; }
            public float X { get; }
            public float Y { get; }
            public float Z { get; }
            public RejectReason Reason { get; }
            public float Value { get; }
            public float Threshold { get; }
            public override string ToString()
                => "attempt " + Attempt + "." + TryIndex + " (" + X.ToString("F1") + ", " + Y.ToString("F1")
                   + ", " + Z.ToString("F1") + ") -> " + Reason
                   + (float.IsNaN(Value) ? "" : " [" + Value + " vs " + Threshold + "]");
        }

        private readonly string? m_only;
        private readonly int m_limit;

        public PlacementTraceRecorder(string? onlyPrefabName = null, int limit = 100000)
        {
            m_only = onlyPrefabName;
            m_limit = limit;
        }

        public readonly List<ZoneEvent> Zones = new List<ZoneEvent>();
        public readonly List<PointEvent> Points = new List<PointEvent>();

        private bool Wanted(ZoneLocationEntry e)
            => m_only == null || string.Equals(m_only, e.PrefabName, StringComparison.Ordinal);

        public void Zone(ZoneLocationEntry e, int attempt, Vec2s zone, RejectReason reason)
        {
            if (Wanted(e) && Zones.Count < m_limit) Zones.Add(new ZoneEvent(e.PrefabName, attempt, zone, reason));
        }

        public void Point(ZoneLocationEntry e, int attempt, int tryIndex, float x, float y, float z,
                          RejectReason reason, float value, float threshold)
        {
            if (Wanted(e) && Points.Count < m_limit)
                Points.Add(new PointEvent(e.PrefabName, attempt, tryIndex, x, y, z, reason, value, threshold));
        }
    }

    /// <summary>Knobs for one run. Everything defaults to "a fresh, never-played world".</summary>
    public sealed class PlacementOptions
    {
        /// <summary>
        /// Stop after this index of <see cref="LocationTable.Ordered"/> (inclusive). Use
        /// <see cref="LocationTable.PrefixLengthForTarget"/> to size it for a named target. Null runs
        /// the whole list.
        /// </summary>
        public int? StopAfterOrderedIndex;

        /// <summary>
        /// Zones already generated in the save. Empty for a fresh world, which is the only case the
        /// engine claims to reproduce - see <see cref="PlacementResult.NotPredictable"/>.
        /// </summary>
        public IReadOnlyCollection<Vec2s>? GeneratedZones;

        /// <summary>Optional trace sink. Leave null for full speed.</summary>
        public IPlacementTrace? Trace;

        /// <summary>
        /// The alt-biome sector assignment, if it has been computed. When null, filters 10a and 10b see
        /// empty <c>AltBiomes</c> lists on every sector: 10b then always passes, and 10a rejects every
        /// point of every entry that has an <c>AltBiomeParent</c> - which is exactly what the game does
        /// when no sector carries that alt-biome, but is NOT a safe assumption when the alt-biome table
        /// is unknown. The engine raises a warning if the table contains such entries and this is null.
        /// </summary>
        public bool AltBiomesComputed;

        /// <summary>
        /// Asked after each ordered entry's loop returns, with that entry and the instances the run
        /// holds so far. Returning false stops the run there; <see cref="PlacementResult.StoppedEarly"/>
        /// then says which entry it stopped after.
        ///
        /// <para><b>Why stopping here loses nothing.</b> An instance is appended only by the loop of
        /// its own entry, and no later entry removes or moves one - so when an entry's loop returns,
        /// that type's instance set for this seed is final. A caller that has already decided its
        /// question from the types that have finished can stop without changing any answer it has. A
        /// caller that has not must return true.</para>
        ///
        /// <para>Null in the hot path of a full run. Used by <c>SeedLab.LocationOracle</c> so that a
        /// seed search rejects on the first boss altar rather than placing 180 more types it will
        /// throw away.</para>
        /// </summary>
        public Func<ZoneLocationEntry, IReadOnlyList<LocationInstanceResult>, bool>? ContinueAfterType;
    }

    /// <summary>
    /// <c>ZoneSystem.GenerateLocationsTimeSliced</c>, ported (decomp/ZoneSystem.cs 1700-1760 and
    /// 1877-2130).
    ///
    /// <para><b>What this reproduces.</b> Each location type opens its own stream seeded
    /// <c>worldSeed + m_prefab.Name.GetStableHashCode()</c> (unchecked), draws candidate zones until it
    /// has <c>m_quantity</c> instances or burns its 60 000 / 12 000 attempt budget, and runs up to six
    /// point tries per accepted zone through eleven filters in a fixed order. Time slicing is ignored
    /// on purpose: the coroutine saves and restores the ambient state around every yield, so the
    /// stream is bit-identical however many frames it spans.</para>
    ///
    /// <para><b>RNG accounting is the part a port gets wrong.</b> Per outer attempt: two ints per
    /// do-while iteration of <c>GetRandomZone</c> for a centre-first entry; otherwise one int for
    /// <c>RandomBiomeFromBiomes</c> when the biome mask is multi-bit, plus one int for the list index.
    /// A zone rejected for occupancy, prior generation or biome area consumes THAT and nothing else.
    /// Every point try then costs two floats, and every try that survives filters 1-5 costs a further
    /// ten <c>insideUnitCircle</c> draws inside <c>GetTerrainDelta</c> - whether or not the delta
    /// passes, and even when <c>m_exteriorRadius</c> is 0 and all ten samples collapse to the centre.
    /// </para>
    ///
    /// <para><b>Correction to spec 02 section 5.4.</b> The spec says <c>mask.a</c> "is exactly 0f"
    /// outside Mistlands and AshLands. It is not: <c>GetBiomeHeight</c> starts with
    /// <c>mask = Color.black</c>, and <c>Color.black</c> is <c>(0, 0, 0, 1)</c>. So the alpha is
    /// <b>1</b> everywhere except Deep North (which writes <c>(0, g, 0, 0)</c>, alpha 0) and
    /// Mistlands / AshLands (which write a computed alpha). That inverts the reading of filter 9: an
    /// entry with <c>m_maximumVegetation &lt; 1</c> is rejected everywhere outside Deep North /
    /// Mistlands / AshLands rather than accepted everywhere, and the surround check in filter 11 scores
    /// a non-zero constant rather than 0. This port follows the code
    /// (SeedLab.WorldGen.Unity.ColorRGBA.Black = (0,0,0,1), which the height acceptance suite
    /// validates), not the spec sentence.</para>
    ///
    /// <para><b>Proven, 2026-09-23.</b> Run against the three worlds the game itself produced:
    /// <list type="bullet">
    /// <item>the FRESH world the dumper captured (seed 75539276, nothing explored, so the instance set
    /// is the raw output of this method): <b>12 228 of 12 228</b> instances reproduced with the zone,
    /// the prefab and x, y and z bit-identical, 0 missing, 0 extra, 178 of 178 prefabs exact, and every
    /// type's <c>placed</c> counter equal.</item>
    /// <item>both played worlds, against their <c>.db2</c>: <b>12 314 of 12 314</b> and
    /// <b>12 287 of 12 287</b>, 0 instances the engine produced that the save no longer holds.</item>
    /// <item>the game's own worldgen log of testworldclaude's creation: all <b>29</b> types that logged
    /// a "placed N out of M" line reproduced exactly, including Crypt4 170/200, TarPit1 91/100 and
    /// NorthVillage 53/135.</item>
    /// </list>
    /// Run it with <c>dotnet run -c Release --project tools\SeedLab.LocationLab -- gate</c>.</para>
    ///
    /// <para><b>What vanilla 1.0.15 does NOT exercise</b>, measured from the table rather than assumed,
    /// so a future reader knows which of the claims above rest on IL reading alone:
    /// no ordered entry has <c>m_exteriorRadius == 0</c> (so the "ten insideUnitCircle draws are spent
    /// even at radius 0" claim is never observed); the largest <c>MaxRadius</c> is exactly 32, so
    /// <see cref="ZoneLocationEntry.CanEscapeZone"/> and
    /// <see cref="ZoneLocationEntry.PointDrawBoundsInverted"/> are false for every entry and
    /// <c>RegisterLocation</c>'s recomputed zone always equals the candidate zone; and filter 1 is
    /// active for only 18 of the 183 entries, none of which straddled the
    /// <c>Utils.LengthXZ</c>/<c>Vector3.magnitude</c> rounding in any of the three worlds. Those four
    /// behaviours were each planted as a deliberate defect and the fresh-world oracle did NOT catch
    /// them - unlike the nine that it did.</para>
    /// </summary>
    public sealed class LocationPlacementEngine
    {
        private readonly WorldGeneratorPort m_gen;
        private readonly BiomeField m_field;
        private readonly LocationTable m_table;
        private readonly PlacementOptions m_opt;

        // ZoneSystem state
        private readonly Dictionary<Vec2s, LocationInstanceResult> m_locationInstances = new Dictionary<Vec2s, LocationInstanceResult>();
        private readonly Dictionary<AssetId, List<LocationInstanceResult>> m_idCache = new Dictionary<AssetId, List<LocationInstanceResult>>();
        private readonly Dictionary<string, List<LocationInstanceResult>> m_groupCache = new Dictionary<string, List<LocationInstanceResult>>(StringComparer.Ordinal);
        private readonly Dictionary<string, List<LocationInstanceResult>> m_maxGroupCache = new Dictionary<string, List<LocationInstanceResult>>(StringComparer.Ordinal);

        /// <summary>CountNrOfLocation's answer, maintained incrementally. Keyed by m_prefab.Name, as the game compares it.</summary>
        private readonly Dictionary<string, int> m_countByPrefabName = new Dictionary<string, int>(StringComparer.Ordinal);

        private readonly List<LocationInstanceResult> m_instances = new List<LocationInstanceResult>();
        private readonly HashSet<Vec2s> m_generatedZones = new HashSet<Vec2s>();

        /// <summary>
        /// <c>ZoneSystem.s_tempVeg</c>. Despite the <c>s_</c> prefix it is an INSTANCE field, cleared
        /// once per location type and never trimmed - the surround check's growing baseline.
        /// </summary>
        private readonly List<float> m_tempVeg = new List<float>();

        private LocationPlacementEngine(WorldGeneratorPort gen, BiomeField field, LocationTable table, PlacementOptions opt)
        {
            m_gen = gen;
            m_field = field;
            m_table = table;
            m_opt = opt;
            if (opt.GeneratedZones != null) foreach (Vec2s z in opt.GeneratedZones) m_generatedZones.Add(z);
        }

        /// <summary>
        /// Runs the placement. <paramref name="gen"/> must be the world the <paramref name="field"/>
        /// was built from; they are cross-checked on the seed.
        /// </summary>
        public static PlacementResult Run(WorldGeneratorPort gen, BiomeField field, LocationTable table,
                                          PlacementOptions? options = null)
        {
            if (gen == null) throw new ArgumentNullException(nameof(gen));
            if (field == null) throw new ArgumentNullException(nameof(field));
            if (table == null) throw new ArgumentNullException(nameof(table));
            if (field.Grid.Seed != gen.GetSeed())
                throw new ArgumentException("The biome grid was built for seed " + field.Grid.Seed
                                            + " but the generator is seed " + gen.GetSeed() + ".");
            return new LocationPlacementEngine(gen, field, table, options ?? new PlacementOptions()).RunCore();
        }

        private PlacementResult RunCore()
        {
            Stopwatch total = Stopwatch.StartNew();
            List<string> warnings = new List<string>(m_table.Notes);
            List<LocationTypeResult> types = new List<LocationTypeResult>();

            int last = m_opt.StopAfterOrderedIndex ?? (m_table.Ordered.Count - 1);
            if (last >= m_table.Ordered.Count) last = m_table.Ordered.Count - 1;

            if (!m_opt.AltBiomesComputed)
            {
                int parented = 0;
                foreach (ZoneLocationEntry e in m_table.Ordered) if (e.AltBiomeParent != null) parented++;
                if (parented > 0)
                    warnings.Add(parented + " ordered entries carry an AltBiomeParent but alt-biomes were "
                                 + "not computed. Every one of them will fail filter 10a on every attempt. "
                                 + "That may be the true answer (a disabled alt-biome does exactly this) - "
                                 + "but it is an assumption until altbiomes.json is dumped.");
            }

            ZoneLocationEntry? stoppedAfter = null;
            int lastRun = -1;
            for (int oi = 0; oi <= last; oi++)
            {
                types.Add(RunOne(m_table.Ordered[oi], oi));
                lastRun = oi;
                if (m_opt.ContinueAfterType != null && !m_opt.ContinueAfterType(m_table.Ordered[oi], m_instances))
                {
                    stoppedAfter = m_table.Ordered[oi];
                    break;
                }
            }

            // Unique entries with more than one surviving candidate.
            List<UniqueCandidateSet> unique = new List<UniqueCandidateSet>();
            foreach (LocationTypeResult t in types)
            {
                if (!t.Location.Unique) continue;
                List<LocationInstanceResult> c = new List<LocationInstanceResult>();
                foreach (LocationInstanceResult i in m_instances)
                    if (ReferenceEquals(i.Location, t.Location)) c.Add(i);
                if (c.Count > 0) unique.Add(new UniqueCandidateSet(t.Location, c));
            }

            total.Stop();
            return new PlacementResult(m_gen.GetSeed(), m_gen.WorldGenVersion, m_instances, m_locationInstances,
                                       types, unique, lastRun + 1, m_table.Ordered.Count,
                                       total.Elapsed.TotalMilliseconds, warnings)
            {
                StoppedEarly = stoppedAfter,
                LastOrderedIndexRun = lastRun,
            };
        }

        private LocationTypeResult RunOne(ZoneLocationEntry location, int orderedIndex)
        {
            Stopwatch sw = Stopwatch.StartNew();
            LocationTypeResult res = new LocationTypeResult
            {
                Location = location,
                OrderedIndex = orderedIndex,
                Quantity = location.Quantity,
                StreamSeed = unchecked(m_gen.GetSeed() + location.NameHash),
            };

            UnityRandom rnd = new UnityRandom();
            rnd.InitState(res.StreamSeed);

            float maxRadius = location.MaxRadius;
            int attempts = location.Attempts;
            int placed = CountNrOfLocation(location);
            float maxRange = 10000f;
            m_tempVeg.Clear();

            IPlacementTrace? trace = m_opt.Trace;

            if (location.Unique && placed > 0)
            {
                res.SkippedAsUnique = true;
                res.Placed = placed;
                sw.Stop();
                res.Milliseconds = sw.Elapsed.TotalMilliseconds;
                return res;
            }

            if (location.CenterFirst) maxRange = location.MinDistance;

            int instancesBefore = m_instances.Count;
            int i = 0;
            while (i < attempts && placed < location.Quantity)
            {
                Vec2s zoneID;
                if (location.CenterFirst)
                {
                    zoneID = ZoneMath.GetRandomZone(rnd, maxRange);
                    maxRange++;                                  // AFTER the draw: attempt k uses minDistance + k
                }
                else if (!(location.MinAltitude < 0f))
                {
                    int p = m_field.GetRandomPointByBiomesAboveSeaLevel(rnd, location.Biome);
                    zoneID = ZoneOfGridPoint(p);
                }
                else
                {
                    int p = m_field.GetRandomPointByBiomes(rnd, location.Biome);
                    zoneID = ZoneOfGridPoint(p);
                }

                if (m_locationInstances.ContainsKey(zoneID))
                {
                    res.Rejections[(int)RejectReason.ZoneOccupied]++;
                    trace?.Zone(location, i, zoneID, RejectReason.ZoneOccupied);
                }
                else if (m_generatedZones.Contains(zoneID))
                {
                    // The game has no counter here and does not even log it.
                    res.Rejections[(int)RejectReason.ZoneAlreadyGenerated]++;
                    trace?.Zone(location, i, zoneID, RejectReason.ZoneAlreadyGenerated);
                }
                else
                {
                    Vec2s zoneCenter = ZoneMath.GetZoneCenter(zoneID);
                    BiomeArea biomeArea = m_gen.GetBiomeArea(zoneCenter);
                    if ((location.BiomeArea & biomeArea) == 0)
                    {
                        res.Rejections[(int)RejectReason.BiomeArea]++;
                        trace?.Zone(location, i, zoneID, RejectReason.BiomeArea);
                    }
                    else
                    {
                        trace?.Zone(location, i, zoneID, RejectReason.None);
                        for (int j = 0; j < 6; j++)
                        {
                            res.Iterations++;
                            if (TryPoint(location, rnd, zoneID, maxRadius, res, i, j, orderedIndex))
                            {
                                placed++;
                                break;
                            }
                        }
                    }
                }

                i++;
            }

            res.Attempts = i;
            res.Placed = placed;
            res.Registered = m_instances.Count - instancesBefore;
            sw.Stop();
            res.Milliseconds = sw.Elapsed.TotalMilliseconds;
            return res;
        }

        /// <summary>
        /// One point try. Returns true when the point was accepted (and <c>placed</c> must be
        /// incremented by the caller - the game does that unconditionally after
        /// <c>RegisterLocation</c>, even when the registration silently collided).
        /// </summary>
        private bool TryPoint(ZoneLocationEntry location, UnityRandom rnd, Vec2s zoneID, float maxRadius,
                              LocationTypeResult res, int attempt, int tryIndex, int orderedIndex)
        {
            IPlacementTrace? trace = m_opt.Trace;

            // Two float draws, always, before any filter.
            (float px, float py, float pz) = ZoneMath.GetRandomPointInZone(rnd, zoneID, maxRadius);

            // Filter 1 - min/max distance from origin. y is still 0 here.
            float magnitude = ZoneMath.Magnitude3(px, py, pz);
            if (location.MinDistance != 0f && magnitude < location.MinDistance)
            {
                res.Rejections[(int)RejectReason.CenterDistance]++;
                trace?.Point(location, attempt, tryIndex, px, py, pz, RejectReason.CenterDistance, magnitude, location.MinDistance);
                return false;
            }
            if (location.MaxDistance != 0f && magnitude > location.MaxDistance)
            {
                res.Rejections[(int)RejectReason.CenterDistance]++;
                trace?.Point(location, attempt, tryIndex, px, py, pz, RejectReason.CenterDistance, magnitude, location.MaxDistance);
                return false;
            }

            // Filter 2 - biome. The RAW procedural biome at the point, not the 12 m grid.
            Biome biome = m_gen.GetBiome(px, pz);
            if (((int)location.Biome & (int)biome) == 0)
            {
                res.Rejections[(int)RejectReason.Biome]++;
                trace?.Point(location, attempt, tryIndex, px, py, pz, RejectReason.Biome, (int)biome, (int)location.Biome);
                return false;
            }

            // Filter 3 - altitude. This is where p.y and the vegetation mask come from.
            py = m_gen.GetHeight(px, pz, out ColorRGBA mask);
            float alt = (float)((double)py - 30.0);
            if (alt < location.MinAltitude || alt > location.MaxAltitude)
            {
                res.Rejections[(int)RejectReason.Altitude]++;
                trace?.Point(location, attempt, tryIndex, px, py, pz, RejectReason.Altitude, alt,
                             alt < location.MinAltitude ? location.MinAltitude : location.MaxAltitude);
                return false;
            }

            // Filter 4 - forest factor (static and seedless).
            if (location.InForest)
            {
                float ff = WorldGeneratorPort.GetForestFactor(px, py, pz);
                if (ff < location.ForestTresholdMin || ff > location.ForestTresholdMax)
                {
                    res.Rejections[(int)RejectReason.Forest]++;
                    trace?.Point(location, attempt, tryIndex, px, py, pz, RejectReason.Forest, ff,
                                 ff < location.ForestTresholdMin ? location.ForestTresholdMin : location.ForestTresholdMax);
                    return false;
                }
            }

            // Filter 5 - the second distance pair. Utils.LengthXZ, not Vector3.magnitude, and the game
            // keeps NO error counter for it.
            if (location.MinDistanceFromCenter > 0f || location.MaxDistanceFromCenter > 0f)
            {
                float d = ZoneMath.LengthXZ(px, pz);
                if ((location.MinDistanceFromCenter > 0f && d < location.MinDistanceFromCenter)
                    || (location.MaxDistanceFromCenter > 0f && d > location.MaxDistanceFromCenter))
                {
                    res.Rejections[(int)RejectReason.DistanceFromCenter]++;
                    trace?.Point(location, attempt, tryIndex, px, py, pz, RejectReason.DistanceFromCenter, d,
                                 d < location.MinDistanceFromCenter ? location.MinDistanceFromCenter : location.MaxDistanceFromCenter);
                    return false;
                }
            }

            // Filter 6 - terrain delta. TEN insideUnitCircle draws, unconditionally, radius =
            // m_exteriorRadius (not maxRadius). With exteriorRadius 0 every sample is the centre and
            // delta is 0 - but the ten draws are still spent.
            m_gen.GetTerrainDelta(rnd, px, py, pz, location.ExteriorRadius, out float delta, out _);
            if (delta > location.MaxTerrainDelta || delta < location.MinTerrainDelta)
            {
                res.Rejections[(int)RejectReason.TerrainDelta]++;
                trace?.Point(location, attempt, tryIndex, px, py, pz, RejectReason.TerrainDelta, delta,
                             delta > location.MaxTerrainDelta ? location.MaxTerrainDelta : location.MinTerrainDelta);
                return false;
            }

            // Filter 7 - too close to something similar.
            if (location.MinDistanceFromSimilar > 0f
                && HaveLocationInRange(location.AssetId, location.Group, px, py, pz, location.MinDistanceFromSimilar))
            {
                res.Rejections[(int)RejectReason.Similar]++;
                trace?.Point(location, attempt, tryIndex, px, py, pz, RejectReason.Similar, float.NaN, location.MinDistanceFromSimilar);
                return false;
            }

            // Filter 8 - not close enough to something similar. Note the AssetID bucket is consulted
            // for this one too, so a same-prefab instance satisfies it whatever m_groupMax says.
            if (location.MaxDistanceFromSimilar > 0f
                && !HaveLocationInRange(location.AssetId, location.GroupMax, px, py, pz, location.MaxDistanceFromSimilar, maxGroup: true))
            {
                res.Rejections[(int)RejectReason.NotSimilar]++;
                trace?.Point(location, attempt, tryIndex, px, py, pz, RejectReason.NotSimilar, float.NaN, location.MaxDistanceFromSimilar);
                return false;
            }

            // Filter 9 - the height mask's alpha. See the class remarks: it is 1 by default, 0 in Deep
            // North, computed in Mistlands and AshLands.
            float a = mask.a;
            if (location.MinimumVegetation > 0f && a <= location.MinimumVegetation)
            {
                res.Rejections[(int)RejectReason.Vegetation]++;
                trace?.Point(location, attempt, tryIndex, px, py, pz, RejectReason.Vegetation, a, location.MinimumVegetation);
                return false;
            }
            if (location.MaximumVegetation < 1f && a >= location.MaximumVegetation)
            {
                res.Rejections[(int)RejectReason.Vegetation]++;
                trace?.Point(location, attempt, tryIndex, px, py, pz, RejectReason.Vegetation, a, location.MaximumVegetation);
                return false;
            }

            // Filters 10a / 10b - alt-biomes on the 12 m sector the point falls in.
            BiomeSectorData sector = m_field.GetBiomeSector(px, pz);
            if (location.AltBiomeParent != null && !SectorHasAltBiome(sector, location.AltBiomeParent))
            {
                res.Rejections[(int)RejectReason.AltBiomeMissing]++;
                trace?.Point(location, attempt, tryIndex, px, py, pz, RejectReason.AltBiomeMissing, float.NaN, float.NaN);
                return false;
            }
            if (SectorBlocksLocation(sector, location.Name))
            {
                res.Rejections[(int)RejectReason.AltBiomeBlock]++;
                trace?.Point(location, attempt, tryIndex, px, py, pz, RejectReason.AltBiomeBlock, float.NaN, float.NaN);
                return false;
            }

            // Filter 11 - surround vegetation. Consumes no RNG but 6 * layers GetHeight calls.
            if (location.SurroundCheckVegetation)
            {
                float score = 0f;
                for (int layer = 0; layer < location.SurroundCheckLayers; layer++)
                {
                    float r = (float)(((double)(layer + 1) / (double)location.SurroundCheckLayers)
                                      * (double)location.SurroundCheckDistance);
                    for (int k = 0; k < 6; k++)
                    {
                        float f = (float)(((double)k / 6.0) * (double)MathF.PI * 2.0);
                        float vx = px + UMathf.Sin(f) * r;
                        float vz = pz + UMathf.Cos(f) * r;
                        m_gen.GetHeight(vx, vz, out ColorRGBA mask2);
                        // The outermost ring has weight 0: r == m_surroundCheckDistance there.
                        float w = (float)(((double)location.SurroundCheckDistance - (double)r)
                                          / ((double)location.SurroundCheckDistance * 2.0));
                        score = (float)((double)score + (double)mask2.a * (double)w);
                    }
                }
                m_tempVeg.Add(score);
                if (m_tempVeg.Count < 10)
                {
                    // The first nine qualifying points of this LOCATION TYPE are always thrown away.
                    res.Rejections[(int)RejectReason.SurroundBaseline]++;
                    trace?.Point(location, attempt, tryIndex, px, py, pz, RejectReason.SurroundBaseline, score, float.NaN);
                    return false;
                }
                float max = MaxOf(m_tempVeg);
                float avg = AverageOf(m_tempVeg);
                float cutoff = (float)((double)avg + ((double)max - (double)avg) * (double)location.SurroundBetterThanAverage);
                if (score < cutoff)
                {
                    res.Rejections[(int)RejectReason.SurroundBelowCutoff]++;
                    trace?.Point(location, attempt, tryIndex, px, py, pz, RejectReason.SurroundBelowCutoff, score, cutoff);
                    return false;
                }
            }

            res.Rejections[(int)RejectReason.Accepted]++;
            trace?.Point(location, attempt, tryIndex, px, py, pz, RejectReason.Accepted, float.NaN, float.NaN);
            RegisterLocation(location, px, py, pz, zoneID, res, orderedIndex, attempt);
            return true;
        }

        // -------------------------------------------------------------------------------------------
        // ZoneSystem bookkeeping
        // -------------------------------------------------------------------------------------------

        private Vec2s ZoneOfGridPoint(int packed)
        {
            // GetZone(AltBiomeWorldData.MapSpaceToWorldSpace(point)) - the Vector3 overload, so the grid
            // y index becomes world Z and GetZone reads it from .z.
            float wx = BiomeGrid.MapSpaceToWorldSpace(BiomeGrid.XOf(packed));
            float wz = BiomeGrid.MapSpaceToWorldSpace(BiomeGrid.YOf(packed));
            return ZoneMath.GetZone(wx, wz);
        }

        /// <summary>
        /// <c>ZoneSystem.RegisterLocation</c>. The zone is recomputed FROM THE POINT, not taken from the
        /// candidate zone; when a location's <c>MaxRadius &gt; 32</c> the two can differ, and if the
        /// recomputed zone is occupied the instance is silently dropped while the caller still counts it.
        /// </summary>
        private void RegisterLocation(ZoneLocationEntry location, float x, float y, float z, Vec2s candidateZone,
                                      LocationTypeResult res, int orderedIndex, int attempt)
        {
            Vec2s zone = ZoneMath.GetZone(x, z);
            if (!zone.Equals(candidateZone)) res.PointsOutsideCandidateZone++;

            if (m_locationInstances.ContainsKey(zone))
            {
                // "Location already exist in zone" - the game logs a warning and returns, and the caller
                // still does placed++.
                res.RegisterCollisions++;
                return;
            }

            LocationInstanceResult inst = new LocationInstanceResult(location, x, y, z, zone, orderedIndex, attempt);
            m_locationInstances.Add(zone, inst);
            m_instances.Add(inst);

            AddToCache(m_idCache, location.AssetId, inst);
            AddToCache(m_groupCache, location.Group, inst);        // the empty key is added too
            AddToCache(m_maxGroupCache, location.GroupMax, inst);

            m_countByPrefabName.TryGetValue(location.PrefabName, out int c);
            m_countByPrefabName[location.PrefabName] = c + 1;
        }

        private static void AddToCache<TKey>(Dictionary<TKey, List<LocationInstanceResult>> dict, TKey key,
                                             LocationInstanceResult inst) where TKey : notnull
        {
            if (!dict.TryGetValue(key, out List<LocationInstanceResult>? list))
            {
                list = new List<LocationInstanceResult>();
                dict.Add(key, list);
            }
            list.Add(inst);
        }

        /// <summary>
        /// <c>ZoneSystem.CountNrOfLocation</c> - compares <c>m_prefab.Name</c> over every registered
        /// instance, placed or not. Kept as a counter rather than a scan; the value is identical.
        /// </summary>
        private int CountNrOfLocation(ZoneLocationEntry location)
            => m_countByPrefabName.TryGetValue(location.PrefabName, out int c) ? c : 0;

        /// <summary>
        /// <c>ZoneSystem.HaveLocationInRange</c>. Distance is 3-D and strict, and both positions carry
        /// their <c>GetHeight</c> y, so terrain height participates - do not simplify to 2-D.
        /// </summary>
        private bool HaveLocationInRange(AssetId assetID, string group, float px, float py, float pz,
                                         float radius, bool maxGroup = false)
        {
            if (m_idCache.TryGetValue(assetID, out List<LocationInstanceResult>? a) && AnyWithin(a)) return true;
            if (group.Length > 0 && !maxGroup && m_groupCache.TryGetValue(group, out List<LocationInstanceResult>? b) && AnyWithin(b)) return true;
            if (((group.Length > 0) & maxGroup) && m_maxGroupCache.TryGetValue(group, out List<LocationInstanceResult>? c) && AnyWithin(c)) return true;
            return false;

            bool AnyWithin(List<LocationInstanceResult> l)
            {
                for (int i = 0; i < l.Count; i++)
                {
                    float dx = l[i].X - px;
                    float dy = l[i].Y - py;
                    float dz = l[i].Z - pz;
                    float sqr = ZoneMath.SqrMagnitude3(dx, dy, dz);
                    if ((double)sqr < (double)radius * (double)radius) return true;
                }
                return false;
            }
        }

        private static bool SectorHasAltBiome(BiomeSectorData sector, string name)
        {
            foreach (AltBiomeRuntime a in sector.AltBiomes)
                if (string.Equals(a.Name, name, StringComparison.Ordinal)) return true;
            return false;
        }

        private static bool SectorBlocksLocation(BiomeSectorData sector, string locationName)
        {
            foreach (AltBiomeRuntime a in sector.AltBiomes)
                foreach (string s in a.BlockLocationNames)
                    if (string.Equals(s, locationName, StringComparison.Ordinal)) return true;
            return false;
        }

        /// <summary><c>Enumerable.Max(IEnumerable&lt;float&gt;)</c> semantics, NaN-aware.</summary>
        private static float MaxOf(List<float> v)
        {
            float m = v[0];
            for (int i = 1; i < v.Count; i++) { if (v[i] > m || float.IsNaN(m)) m = v[i]; }
            return m;
        }

        /// <summary><c>Enumerable.Average(IEnumerable&lt;float&gt;)</c> - sums in DOUBLE, returns float.</summary>
        private static float AverageOf(List<float> v)
        {
            double sum = 0.0;
            for (int i = 0; i < v.Count; i++) sum += v[i];
            return (float)(sum / v.Count);
        }
    }
}
