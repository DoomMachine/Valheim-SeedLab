using System;
using System.Collections.Generic;
using System.Diagnostics;
using SeedLab.Cli.Infra;
using SeedLab.Locations;
using SeedLab.Render;
using SeedLab.WorldGen;

namespace SeedLab.Cli.Analysis
{
    /// <summary>One location type's instances in one world, reduced to what a summary prints.</summary>
    public sealed class Landmark
    {
        public Landmark(ZoneLocationEntry e, LocationKind kind, int generators, int placed,
                        IReadOnlyList<LocationInstanceResult> instances, IReadOnlyList<Biome> biomes)
        {
            Entry = e;
            Kind = kind;
            Generators = generators;
            Placed = placed;
            Instances = instances;
            Biomes = biomes;
        }

        public ZoneLocationEntry Entry { get; }
        public LocationKind Kind { get; }
        public int Generators { get; }

        /// <summary>The game's own <c>placed</c> counter for this type.</summary>
        public int Placed { get; }

        /// <summary>Every instance, nearest to the origin first.</summary>
        public IReadOnlyList<LocationInstanceResult> Instances { get; }

        /// <summary>The generator's biome at each instance, parallel to <see cref="Instances"/>.</summary>
        public IReadOnlyList<Biome> Biomes { get; }

        public string Prefab => Entry.PrefabName;

        public bool Shortfall => Placed < Entry.Quantity;

        /// <summary>
        /// False for a <c>m_unique</c> type with more than one surviving candidate: the winner is
        /// exploration order, not seed. A caller must not print one of these as "the" position.
        /// </summary>
        public bool SinglePositionIsPredictable => !Entry.Unique || Instances.Count <= 1;

        public double NearestDistance => Instances.Count == 0
            ? double.PositiveInfinity
            : Math.Sqrt((double)Instances[0].X * Instances[0].X + (double)Instances[0].Z * Instances[0].Z);
    }

    /// <summary>
    /// The boss altars, trader camps and (optionally) dungeons of one seed, for the summary section of
    /// <c>vseed seed</c>. <c>vseed locations</c> is the full listing; this is the headline.
    ///
    /// <para><b>Why bosses and traders are cheap and dungeons are not.</b> The placement list is walked
    /// <c>m_prioritized</c> first, and every boss altar and trader camp is prioritized - they are
    /// Ordered[0..22] in 1.0.15. Running that prefix is correct and takes a fraction of a second on top
    /// of the biome grid. Most dungeon types are not prioritized and sit late in the list, so a dungeon
    /// census needs the whole run. Nothing shorter than "everything up to the target" is ever safe:
    /// zone occupancy is global, so an earlier type can take a later one's zone.</para>
    /// </summary>
    public sealed class Landmarks
    {
        private Landmarks(LocationCatalog catalog, IReadOnlyList<Landmark> all, int typesRun, int typesTotal,
                          double worldSeconds, double placementSeconds, bool includesDungeons)
        {
            Catalog = catalog;
            All = all;
            TypesRun = typesRun;
            TypesTotal = typesTotal;
            WorldSeconds = worldSeconds;
            PlacementSeconds = placementSeconds;
            IncludesDungeons = includesDungeons;
        }

        public LocationCatalog Catalog { get; }
        public IReadOnlyList<Landmark> All { get; }
        public int TypesRun { get; }
        public int TypesTotal { get; }
        public double WorldSeconds { get; }
        public double PlacementSeconds { get; }

        /// <summary>False when only the prioritized prefix was run, so late dungeon types are absent.</summary>
        public bool IncludesDungeons { get; }

        /// <summary>The <c>StartTemple</c> instance - <c>Game.FindSpawnPoint</c>'s anchor. Null if none was placed.</summary>
        public LocationInstanceResult? Spawn { get; private set; }

        public IEnumerable<Landmark> Of(LocationKind k)
        {
            foreach (Landmark l in All) if ((l.Kind & k) != 0) yield return l;
        }

        public static Landmarks Compute(int seed, int worldGenVersion, int threads, bool includeDungeons)
        {
            LocationCatalog cat = LocationCatalog.Load();

            LocationKind want = LocationKind.Boss | LocationKind.Trader;
            if (includeDungeons) want |= LocationKind.Dungeon;
            List<ZoneLocationEntry> wanted = cat.Select(want);

            // StartTemple is where the player actually appears, so it is always worth the zero extra
            // cost: it is Ordered[0].
            ZoneLocationEntry? temple = null;
            foreach (ZoneLocationEntry e in cat.Table.Ordered)
                if (e.PrefabName == "StartTemple") { temple = e; break; }
            if (temple != null && !wanted.Contains(temple)) wanted.Add(temple);

            int prefix = cat.PrefixFor(wanted);

            Stopwatch sw = Stopwatch.StartNew();
            WorldLocations wl = WorldLocations.Build(seed, worldGenVersion, cat.AltBiomes, threads == 0 ? -1 : threads);
            double worldS = sw.Elapsed.TotalSeconds;
            sw.Restart();
            PlacementResult res = LocationPlacementEngine.Run(wl.Generator, wl.Field, cat.Table,
                new PlacementOptions { StopAfterOrderedIndex = prefix - 1, AltBiomesComputed = true });
            double placeS = sw.Elapsed.TotalSeconds;

            Dictionary<string, List<LocationInstanceResult>> byPrefab =
                new Dictionary<string, List<LocationInstanceResult>>(StringComparer.Ordinal);
            foreach (LocationInstanceResult i in res.Instances)
            {
                if (!byPrefab.TryGetValue(i.PrefabName, out List<LocationInstanceResult>? l))
                { l = new List<LocationInstanceResult>(); byPrefab[i.PrefabName] = l; }
                l.Add(i);
            }

            List<Landmark> all = new List<Landmark>(wanted.Count);
            foreach (ZoneLocationEntry e in wanted)
            {
                byPrefab.TryGetValue(e.PrefabName, out List<LocationInstanceResult>? l);
                l ??= new List<LocationInstanceResult>();
                l.Sort((x, y) => Dist(x).CompareTo(Dist(y)));
                List<Biome> biomes = new List<Biome>(l.Count);
                foreach (LocationInstanceResult i in l) biomes.Add(wl.Generator.GetBiome(i.X, i.Z));

                int placed = 0;
                foreach (LocationTypeResult t in res.Types)
                    if (t.Location.PrefabName == e.PrefabName) { placed = t.Placed; break; }

                all.Add(new Landmark(e, cat.KindOf(e.PrefabName), cat.GeneratorsOf(e.PrefabName), placed, l, biomes));
            }

            all.Sort((a, b) =>
            {
                int c = Rank(a).CompareTo(Rank(b));
                if (c != 0) return c;
                c = a.NearestDistance.CompareTo(b.NearestDistance);
                return c != 0 ? c : string.CompareOrdinal(a.Prefab, b.Prefab);
            });

            Landmarks lm = new Landmarks(cat, all, prefix, cat.Table.Ordered.Count, worldS, placeS, includeDungeons);
            if (byPrefab.TryGetValue("StartTemple", out List<LocationInstanceResult>? st) && st.Count > 0)
                lm.Spawn = st[0];
            return lm;
        }

        private static double Dist(LocationInstanceResult i)
            => Math.Sqrt((double)i.X * i.X + (double)i.Z * i.Z);

        /// <summary>A 16-point compass name plus the bearing, short enough for a wide table.</summary>
        private static string Compass(double degrees)
        {
            string[] pts = { "N", "NNE", "NE", "ENE", "E", "ESE", "SE", "SSE",
                             "S", "SSW", "SW", "WSW", "W", "WNW", "NW", "NNW" };
            double d = ((degrees % 360) + 360) % 360;
            return pts[(int)Math.Round(d / 22.5) % 16] + " " + Out.F(d, 0);
        }

        private static int Rank(Landmark l)
            => (l.Kind & LocationKind.Boss) != 0 ? 0
             : (l.Kind & LocationKind.Trader) != 0 ? 1
             : 2;

        // -----------------------------------------------------------------------------------------

        public void WriteHuman(Out o)
        {
            o.Header("Landmarks  (the game's own location placement, run for this seed)");
            o.Field("game data", Catalog.Data.Stamp.GameVersion + ", dumped " + Catalog.Data.Stamp.Dumped);
            o.Field("types run", TypesRun + " of " + TypesTotal + "   ("
                                 + Out.F(WorldSeconds, 2) + " s biome grid, " + Out.F(PlacementSeconds, 2) + " s placement)");
            o.Field("spawn (StartTemple)", Spawn == null
                ? "none placed"
                : "(" + Out.F(Spawn.X, 0) + ", " + Out.F(Spawn.Z, 0) + ")   this is Game.FindSpawnPoint's anchor, "
                  + "not (0, 0)");

            List<string[]> rows = new List<string[]>();
            foreach (Landmark l in All)
            {
                if (l.Prefab == "StartTemple") continue;
                string what = (l.Kind & LocationKind.Boss) != 0 ? "boss altar"
                            : (l.Kind & LocationKind.Trader) != 0 ? "trader"
                            : "dungeon";
                string where = l.Instances.Count == 0
                    ? "-"
                    : "(" + Out.F(l.Instances[0].X, 0) + ", " + Out.F(l.Instances[0].Z, 0) + ")";
                rows.Add(new[]
                {
                    // The name a player knows goes FIRST and the prefab stays beside it, never
                    // instead of it: the prefab is what --name, a query file and the --json output all
                    // take, so a row that showed only "The Elder" would leave the reader holding a
                    // string none of them accepts. A dash, not the prefab repeated, where the dump
                    // does not name the place.
                    Catalog.DisplayNameOf(l.Prefab) ?? "-",
                    l.Prefab,
                    what,
                    l.Placed.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + (l.Shortfall ? "/" + l.Entry.Quantity : ""),
                    l.Instances.Count == 0 ? "-" : Out.F(l.NearestDistance, 0),
                    l.Instances.Count == 0 ? "-" : Compass(WorldSummary.BearingFromOrigin(l.Instances[0].X, l.Instances[0].Z)),
                    where,
                    l.Instances.Count == 0 ? "-" : MapPalette.Name(l.Biomes[0]),
                    l.SinglePositionIsPredictable ? "yes" : "1 of " + l.Instances.Count,
                });
            }

            o.Table(new[] { "name", "prefab", "kind", "count", "nearest m", "dir", "at", "biome", "one position?" },
                    rows, new[] { false, false, false, true, true, false, false, false, false });

            o.Note("");
            o.Note("'count' is how many of that type the generator placed; a boss has SEVERAL altars, and the");
            o.Note("nearest is the one measured here. 'one position? 1 of N' means the type is m_unique: the game");
            o.Note("keeps exactly one of those candidates and which one depends on which zone a player");
            o.Note("generates first, not on the seed. Use 'vseed locations --name <name or prefab>' for the full");
            o.Note("set. 'name' is the game's own string for the place, from the dumped localization table; a");
            o.Note("dash means the dump names it nothing and the prefab is what it is called.");
            if (!IncludesDungeons)
            {
                o.Note("");
                o.Note("Only the prioritized prefix of the placement list ran, which is every boss and trader but");
                o.Note("not most dungeons. 'vseed seed <seed> --dungeons', or 'vseed locations <seed> --type");
                o.Note("dungeon', runs all " + TypesTotal + " types.");
            }
        }

        public void WriteJson(System.Text.Json.Utf8JsonWriter j)
        {
            j.WriteStartObject("landmarks");
            j.WriteString("game_version", Catalog.Data.Stamp.GameVersion);
            j.WriteString("data_stamp_sha256", Catalog.Data.Stamp.AssemblyValheimSha256);
            j.WriteString("names_language", Catalog.Names.Language);
            j.WriteNumber("types_run", TypesRun);
            j.WriteNumber("types_total", TypesTotal);
            j.WriteBoolean("includes_dungeons", IncludesDungeons);
            j.WriteNumber("seconds_world", WorldSeconds);
            j.WriteNumber("seconds_placement", PlacementSeconds);

            if (Spawn == null) j.WriteNull("spawn");
            else
            {
                j.WriteStartObject("spawn");
                j.WriteString("prefab", Spawn.PrefabName);
                j.WriteNumber("x", Spawn.X);
                j.WriteNumber("z", Spawn.Z);
                j.WriteNumber("y", Spawn.Y);
                j.WriteEndObject();
            }

            j.WriteStartArray("types");
            foreach (Landmark l in All)
            {
                j.WriteStartObject();
                // 'prefab' unchanged and still the identity; 'display_name' is additive and null - not
                // the prefab repeated - where the dump names nothing. The key is display_name rather
                // than name because ZoneLocation already HAS an m_name (the string an alt-biome's
                // m_blockLocationNames matches), and one key meaning two different fields in the same
                // document is how a reader ends up matching the wrong one.
                j.WriteString("prefab", l.Prefab);
                string? display = Catalog.DisplayNameOf(l.Prefab);
                if (display == null) j.WriteNull("display_name");
                else j.WriteString("display_name", display);
                j.WriteBoolean("boss", (l.Kind & LocationKind.Boss) != 0);
                j.WriteBoolean("trader", (l.Kind & LocationKind.Trader) != 0);
                j.WriteNumber("dungeon_generators", l.Generators);
                j.WriteBoolean("unique", l.Entry.Unique);
                j.WriteNumber("quantity", l.Entry.Quantity);
                j.WriteNumber("placed", l.Placed);
                j.WriteBoolean("single_position_is_predictable", l.SinglePositionIsPredictable);
                SeedCommandJson(j, l);
                j.WriteEndObject();
            }

            j.WriteEndArray();

            j.WriteStartArray("not_predictable");
            foreach (string s in PlacementResult.NotPredictable) j.WriteStringValue(s);
            j.WriteEndArray();
            j.WriteEndObject();
        }

        private static void SeedCommandJson(System.Text.Json.Utf8JsonWriter j, Landmark l)
        {
            j.WriteStartArray("instances");
            for (int i = 0; i < l.Instances.Count; i++)
            {
                LocationInstanceResult x = l.Instances[i];
                j.WriteStartObject();
                j.WriteNumber("x", x.X);
                j.WriteNumber("z", x.Z);
                j.WriteNumber("y", x.Y);
                j.WriteNumber("distance_m", Dist(x));
                j.WriteNumber("bearing_deg", WorldSummary.BearingFromOrigin(x.X, x.Z));
                j.WriteString("biome", MapPalette.Name(l.Biomes[i]));
                j.WriteEndObject();
            }

            j.WriteEndArray();
        }
    }
}
