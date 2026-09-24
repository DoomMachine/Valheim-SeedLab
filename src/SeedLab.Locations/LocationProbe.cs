using System;
using System.Collections.Generic;
using SeedLab.WorldGen;
using SeedLab.WorldGen.Unity;

namespace SeedLab.Locations
{
    /// <summary>One filter's verdict at one point.</summary>
    public readonly struct FilterVerdict
    {
        public FilterVerdict(RejectReason filter, bool applicable, bool passed, float value, float threshold, string note)
        {
            Filter = filter; Applicable = applicable; Passed = passed; Value = value; Threshold = threshold; Note = note;
        }

        public RejectReason Filter { get; }

        /// <summary>False when the entry's fields switch this filter off, so it cannot reject anything.</summary>
        public bool Applicable { get; }

        public bool Passed { get; }
        public float Value { get; }
        public float Threshold { get; }

        /// <summary>Empty unless the verdict needs qualifying (RNG-dependent, run-state-dependent, ...).</summary>
        public string Note { get; }

        public override string ToString()
        {
            string head = Filter + ": " + (!Applicable ? "n/a" : Passed ? "pass" : "REJECT");
            if (!float.IsNaN(Value)) head += " (" + Value + " vs " + Threshold + ")";
            return Note.Length > 0 ? head + " - " + Note : head;
        }
    }

    /// <summary>
    /// "Why would this location not be here?" - evaluates every placement filter that does not depend
    /// on the RNG stream at an arbitrary world point, in the game's order.
    ///
    /// <para>Two filters cannot be answered this way and say so in their <see cref="FilterVerdict.Note"/>:
    /// filter 6 (terrain delta) samples ten random offsets, so its verdict depends on where in the
    /// stream the try happened; and filter 11's first phase rejects the first nine qualifying points of
    /// the type whatever they scored. Filters 7 and 8 are answered only when a
    /// <see cref="PlacementResult"/> is supplied, and then against the FINAL instance set, whereas the
    /// game tested them against whatever existed at that moment.</para>
    /// </summary>
    public static class LocationProbe
    {
        public static IReadOnlyList<FilterVerdict> Explain(WorldGeneratorPort gen, BiomeField field,
                                                           ZoneLocationEntry e, float x, float z,
                                                           PlacementResult? afterRun = null)
        {
            if (gen == null) throw new ArgumentNullException(nameof(gen));
            if (field == null) throw new ArgumentNullException(nameof(field));
            if (e == null) throw new ArgumentNullException(nameof(e));

            List<FilterVerdict> outp = new List<FilterVerdict>(14);

            // Zone-level: the biome area of the candidate zone's centre.
            Vec2s zone = ZoneMath.GetZone(x, z);
            BiomeArea area = gen.GetBiomeArea(ZoneMath.GetZoneCenter(zone));
            outp.Add(new FilterVerdict(RejectReason.BiomeArea, e.BiomeArea != BiomeArea.Everything,
                                       ((int)e.BiomeArea & (int)area) != 0, (int)area, (int)e.BiomeArea,
                                       "zone " + zone + " is " + area));

            if (afterRun != null)
            {
                bool taken = afterRun.ByZone.TryGetValue(zone, out LocationInstanceResult? occupant);
                outp.Add(new FilterVerdict(RejectReason.ZoneOccupied, true, !taken, float.NaN, float.NaN,
                                           taken ? "zone already holds " + occupant!.PrefabName : "zone free at end of run"));
            }

            float mag = ZoneMath.Magnitude3(x, 0f, z);
            bool d1 = !(e.MinDistance != 0f && mag < e.MinDistance) && !(e.MaxDistance != 0f && mag > e.MaxDistance);
            outp.Add(new FilterVerdict(RejectReason.CenterDistance, e.MinDistance != 0f || e.MaxDistance != 0f,
                                       d1, mag, mag < e.MinDistance ? e.MinDistance : e.MaxDistance, ""));

            Biome b = gen.GetBiome(x, z);
            outp.Add(new FilterVerdict(RejectReason.Biome, true, ((int)e.Biome & (int)b) != 0, (int)b, (int)e.Biome,
                                       "point is " + b + ", entry wants " + e.Biome));

            float y = gen.GetHeight(x, z, out ColorRGBA mask);
            float alt = (float)((double)y - 30.0);
            outp.Add(new FilterVerdict(RejectReason.Altitude, true, alt >= e.MinAltitude && alt <= e.MaxAltitude,
                                       alt, alt < e.MinAltitude ? e.MinAltitude : e.MaxAltitude, "height " + y));

            if (e.InForest)
            {
                float ff = WorldGeneratorPort.GetForestFactor(x, y, z);
                outp.Add(new FilterVerdict(RejectReason.Forest, true,
                                           ff >= e.ForestTresholdMin && ff <= e.ForestTresholdMax, ff,
                                           ff < e.ForestTresholdMin ? e.ForestTresholdMin : e.ForestTresholdMax, ""));
            }
            else outp.Add(new FilterVerdict(RejectReason.Forest, false, true, float.NaN, float.NaN, "m_inForest is false"));

            bool d5applicable = e.MinDistanceFromCenter > 0f || e.MaxDistanceFromCenter > 0f;
            float lxz = ZoneMath.LengthXZ(x, z);
            bool d5 = !((e.MinDistanceFromCenter > 0f && lxz < e.MinDistanceFromCenter)
                        || (e.MaxDistanceFromCenter > 0f && lxz > e.MaxDistanceFromCenter));
            outp.Add(new FilterVerdict(RejectReason.DistanceFromCenter, d5applicable, d5, lxz,
                                       lxz < e.MinDistanceFromCenter ? e.MinDistanceFromCenter : e.MaxDistanceFromCenter,
                                       "Utils.LengthXZ, not Vector3.magnitude - the two differ by a rounding"));

            outp.Add(new FilterVerdict(RejectReason.TerrainDelta, true, true, float.NaN,
                                       e.MaxTerrainDelta,
                                       "NOT EVALUATED: 10 Random.insideUnitCircle samples of radius "
                                       + e.ExteriorRadius + ", so the verdict depends on the stream position"));

            if (afterRun != null)
            {
                outp.Add(new FilterVerdict(RejectReason.Similar, e.MinDistanceFromSimilar > 0f,
                                           !AnyWithin(afterRun, e, x, y, z, e.MinDistanceFromSimilar, e.Group, false),
                                           float.NaN, e.MinDistanceFromSimilar,
                                           "against the FINAL instance set, not the set that existed at that attempt"));
                outp.Add(new FilterVerdict(RejectReason.NotSimilar, e.MaxDistanceFromSimilar > 0f,
                                           AnyWithin(afterRun, e, x, y, z, e.MaxDistanceFromSimilar, e.GroupMax, true),
                                           float.NaN, e.MaxDistanceFromSimilar,
                                           "against the FINAL instance set"));
            }

            float a = mask.a;
            bool veg = !(e.MinimumVegetation > 0f && a <= e.MinimumVegetation)
                       && !(e.MaximumVegetation < 1f && a >= e.MaximumVegetation);
            outp.Add(new FilterVerdict(RejectReason.Vegetation, e.MinimumVegetation > 0f || e.MaximumVegetation < 1f,
                                       veg, a, e.MinimumVegetation > 0f ? e.MinimumVegetation : e.MaximumVegetation,
                                       "mask.a defaults to 1 (Color.black), is 0 in Deep North and computed in Mistlands/AshLands"));

            BiomeSectorData sector = field.GetBiomeSector(x, z);
            bool has = e.AltBiomeParent == null;
            if (!has) foreach (AltBiomeRuntime ab in sector.AltBiomes) if (ab.Name == e.AltBiomeParent) { has = true; break; }
            outp.Add(new FilterVerdict(RejectReason.AltBiomeMissing, e.AltBiomeParent != null, has, float.NaN, float.NaN,
                                       "sector #" + sector.Index + " (" + sector.Biome + ", " + sector.AltBiomes.Count + " alt-biomes)"));

            bool blocked = false;
            foreach (AltBiomeRuntime ab in sector.AltBiomes)
                foreach (string s in ab.BlockLocationNames)
                    if (s == e.Name) { blocked = true; break; }
            outp.Add(new FilterVerdict(RejectReason.AltBiomeBlock, sector.AltBiomes.Count > 0, !blocked, float.NaN, float.NaN, ""));

            outp.Add(new FilterVerdict(RejectReason.SurroundBaseline, e.SurroundCheckVegetation, true, float.NaN, float.NaN,
                                       e.SurroundCheckVegetation
                                           ? "NOT EVALUATED: depends on the 10-value baseline this type had collected"
                                           : "m_surroundCheckVegetation is false"));

            return outp;
        }

        private static bool AnyWithin(PlacementResult run, ZoneLocationEntry e, float px, float py, float pz,
                                      float radius, string group, bool maxGroup)
        {
            foreach (LocationInstanceResult i in run.Instances)
            {
                bool sameBucket = i.Location.AssetId.Equals(e.AssetId)
                                  || (group.Length > 0 && (maxGroup
                                        ? string.Equals(i.Location.GroupMax, group, StringComparison.Ordinal)
                                        : string.Equals(i.Location.Group, group, StringComparison.Ordinal)));
                if (!sameBucket) continue;
                float sqr = ZoneMath.SqrMagnitude3(i.X - px, i.Y - py, i.Z - pz);
                if ((double)sqr < (double)radius * (double)radius) return true;
            }
            return false;
        }
    }
}
