using System;
using System.Collections.Generic;
using System.Globalization;
using SeedLab.Search.Criteria;
using SeedLab.Search.Feasibility;
using SeedLab.Search.Locations;
using SeedLab.WorldGen;

namespace SeedLab.Web.Search
{
    /// <summary>
    /// The legal interval for every goal the editor can build, so that <b>an impossible value cannot be
    /// typed</b> rather than being typed and refused afterwards.
    ///
    /// <para>The user asked for the limit to be visible "without cluttering it". So the bound is
    /// expressed BY the control - <c>min</c> and <c>max</c> on the number input - a single-sentence
    /// hint names the constraint that binds ("Eikthyr is always within 1 km of the centre"), and the
    /// evidence behind it (<c>m_maxDistance 1000</c>, <c>m_quantity 3</c>, the biome mask) sits behind
    /// an info affordance that is closed by default.</para>
    ///
    /// <para><b>Where the numbers come from.</b> Not from a copied table:
    /// <see cref="LocationFeasibility.LowerBound"/> and <see cref="LocationFeasibility.UpperBound"/>
    /// read the dumped <c>ZoneLocation</c> the search engine itself reasons about, and
    /// <see cref="BiomeGeometry.Band"/> supplies the branch-condition biome bands. They are the same
    /// functions that produce the engine's "no seed can satisfy this" refusal, which is why a value
    /// the control allows is exactly a value the engine will not refuse as impossible. The constraint
    /// atlas (<c>docs\studies\constraint-atlas.md</c>) was validated against 36,829 real instances
    /// with zero violations and agrees with them; it is the study, this is the code.</para>
    /// </summary>
    public static class GoalBounds
    {
        /// <summary>Everything outside the water edge is Ocean and nothing places there.</summary>
        public const double WaterEdge = 10_500.0;

        public static BoundsCatalog Build(ILocationOracle oracle, int genVersion)
        {
            BoundsCatalog cat = new BoundsCatalog { GenVersion = genVersion, WaterEdgeM = WaterEdge };

            foreach (string name in MetricCatalog.BiomeNames)
            {
                if (!Enum.TryParse(name, out Biome b)) continue;
                (double lo, double hi) = BiomeGeometry.Band(b, genVersion);
                cat.Biomes.Add(new BiomeBound
                {
                    Name = name,
                    MinDistanceM = lo,
                    MaxDistanceM = Math.Min(hi, WaterEdge),
                    Hint = HintFor(name, lo, Math.Min(hi, WaterEdge), "the biome's own distance band"),
                    Evidence = BiomeGeometry.Evidence(b, genVersion),
                });
            }

            if (!oracle.Available) return cat;

            foreach (string prefab in oracle.PrefabNames)
            {
                LocationTypeInfo? t = oracle.TypeOf(prefab);
                if (t == null) continue;
                cat.Locations.Add(Describe(t, genVersion));
            }

            foreach (string g in LocationGroups.Names)
            {
                IReadOnlyList<string>? members = oracle.ExpandGroup(g);
                if (members == null || members.Count == 0) continue;

                double lo = double.PositiveInfinity, hi = 0;
                int quantity = 0, known = 0;
                List<string> names = new List<string>();
                foreach (string m in members)
                {
                    LocationTypeInfo? t = oracle.TypeOf(m);
                    if (t == null) continue;
                    known++;
                    names.Add(m);
                    quantity += t.Quantity;
                    lo = Math.Min(lo, LocationFeasibility.LowerBound(t, genVersion));
                    hi = Math.Max(hi, Math.Min(LocationFeasibility.UpperBound(t, genVersion), WaterEdge));
                }

                if (known == 0) continue;
                if (double.IsPositiveInfinity(lo)) lo = 0;

                cat.Groups.Add(new LocationBound
                {
                    Name = g,
                    // A group's bound is the UNION of its members' rings: the nearest any member can be
                    // and the furthest any member can be. It is deliberately the weakest bound of the
                    // set, because a goal about the group is satisfied by any one of them.
                    MinDistanceM = lo,
                    MaxDistanceM = hi,
                    MaxCount = quantity,
                    Members = names,
                    Hint = HintFor(g, lo, hi, "the widest ring any member of the group can sit in"),
                    Evidence = names.Count + " prefabs; m_quantity sums to " + quantity
                               + ", which is the proved cap on a world-wide count "
                               + "(ZoneSystem.PlaceLocations: while (i < attempts && placed < m_quantity))",
                });
            }

            return cat;
        }

        private static LocationBound Describe(LocationTypeInfo t, int genVersion)
        {
            double lo = LocationFeasibility.LowerBound(t, genVersion);
            double hi = Math.Min(LocationFeasibility.UpperBound(t, genVersion), WaterEdge);

            // Which test actually binds. Naming the wrong one would be worse than naming none: the user
            // would go and change a filter that was never the limit.
            string bindLo = "nothing constrains how close it can be";
            if (t.MinDistance != 0f && Math.Abs(lo - t.MinDistance) < 0.5) bindLo = "m_minDistance " + Num(t.MinDistance);
            else if (t.MinDistanceFromCenter > 0f && Math.Abs(lo - t.MinDistanceFromCenter) < 0.5) bindLo = "m_minDistanceFromCenter " + Num(t.MinDistanceFromCenter);
            else if (lo > 0) bindLo = "the " + BiomeNames(t.Biome) + " band starts at " + Num(lo) + " m";

            string bindHi = "the water edge at 10,500 m";
            if (t.MaxDistance != 0f && Math.Abs(hi - t.MaxDistance) < 0.5) bindHi = "m_maxDistance " + Num(t.MaxDistance);
            else if (t.MaxDistanceFromCenter > 0f && Math.Abs(hi - t.MaxDistanceFromCenter) < 0.5) bindHi = "m_maxDistanceFromCenter " + Num(t.MaxDistanceFromCenter);
            else if (hi < WaterEdge - 0.5) bindHi = "the " + BiomeNames(t.Biome) + " band ends at " + Num(hi) + " m";

            List<string> evidence = new List<string>
            {
                "m_quantity " + t.Quantity.ToString(CultureInfo.InvariantCulture)
                + " - a PROVED cap: the placement loop is while (i < attempts && placed < m_quantity) and "
                + "instances are only ever appended, so no world holds more. There is no lower bound in "
                + "the code at all; a shortfall is a path the game ships.",
                "biome mask: " + BiomeNames(t.Biome),
            };

            if (t.MinDistance != 0f) evidence.Add("m_minDistance " + Num(t.MinDistance));
            if (t.MaxDistance != 0f) evidence.Add("m_maxDistance " + Num(t.MaxDistance));
            if (t.MinDistanceFromCenter > 0f) evidence.Add("m_minDistanceFromCenter " + Num(t.MinDistanceFromCenter));
            if (t.MaxDistanceFromCenter > 0f) evidence.Add("m_maxDistanceFromCenter " + Num(t.MaxDistanceFromCenter));
            if (t.Unique) evidence.Add("m_unique: the instances are CANDIDATES - the first one whose zone a player generates wins and the rest are deleted, which is exploration order, not seed");
            if (t.AltBiomeParent != null) evidence.Add("injected by the alt-biome '" + t.AltBiomeParent + "', so it is absent from most seeds");
            if (!t.Placeable) evidence.Add("NOT placeable: m_enable is false or m_quantity is 0, so every world holds exactly zero of them");

            return new LocationBound
            {
                Name = t.Prefab,
                MinDistanceM = lo,
                MaxDistanceM = hi,
                MaxCount = t.Quantity,
                Placeable = t.Placeable,
                Unique = t.Unique,
                Biomes = BiomeNames(t.Biome),
                Hint = HintFor(t.Prefab, lo, hi, bindHi, bindLo),
                Evidence = string.Join("  ·  ", evidence),
            };
        }

        /// <summary>The one line beside the control. Short by design: the evidence is a click away.</summary>
        private static string HintFor(string name, double lo, double hi, string why, string? whyLo = null)
        {
            bool hasLo = lo > 0.5;
            bool hasHi = hi < WaterEdge - 0.5;

            if (!hasLo && !hasHi) return name + " can be anywhere in the world, out to the 10,500 m water edge.";
            if (hasLo && hasHi)
            {
                return name + " is always between " + Km(lo) + " and " + Km(hi) + " of the centre ("
                       + (whyLo != null ? whyLo + "; " : "") + why + ").";
            }

            if (hasHi) return name + " is always within " + Km(hi) + " of the centre (" + why + ").";
            return name + " is never closer than " + Km(lo) + " to the centre ("
                   + (whyLo ?? why) + ").";
        }

        private static string Km(double m)
            => m >= 1000
                ? (m / 1000.0).ToString("0.###", CultureInfo.InvariantCulture) + " km"
                : m.ToString("0", CultureInfo.InvariantCulture) + " m";

        private static string Num(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

        private static string BiomeNames(Biome mask)
        {
            List<string> names = new List<string>();
            foreach (Biome b in new[]
            {
                Biome.Meadows, Biome.Swamp, Biome.Mountain, Biome.BlackForest, Biome.Plains,
                Biome.AshLands, Biome.DeepNorth, Biome.Ocean, Biome.Mistlands,
            })
            {
                if (((int)mask & (int)b) != 0) names.Add(b.ToString());
            }

            return names.Count == 0 ? "(none)" : string.Join(", ", names);
        }
    }

    /// <summary>The bounds table the editor limits its controls with.</summary>
    public sealed class BoundsCatalog
    {
        public int GenVersion { get; set; }

        public double WaterEdgeM { get; set; }

        public List<BiomeBound> Biomes { get; set; } = new List<BiomeBound>();

        public List<LocationBound> Locations { get; set; } = new List<LocationBound>();

        public List<LocationBound> Groups { get; set; } = new List<LocationBound>();
    }

    public sealed class BiomeBound
    {
        public string Name { get; set; } = "";
        public double MinDistanceM { get; set; }
        public double MaxDistanceM { get; set; }
        public string Hint { get; set; } = "";
        public string Evidence { get; set; } = "";
    }

    public sealed class LocationBound
    {
        public string Name { get; set; } = "";
        public double MinDistanceM { get; set; }
        public double MaxDistanceM { get; set; }

        /// <summary>The proved cap on a world-wide count: <c>m_quantity</c>, or its sum for a group.</summary>
        public int MaxCount { get; set; }

        public bool Placeable { get; set; } = true;
        public bool Unique { get; set; }
        public string Biomes { get; set; } = "";
        public List<string>? Members { get; set; }
        public string Hint { get; set; } = "";
        public string Evidence { get; set; } = "";
    }
}
