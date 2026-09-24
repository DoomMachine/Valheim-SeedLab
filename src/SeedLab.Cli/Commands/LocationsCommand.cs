using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using SeedLab.Cli.Analysis;
using SeedLab.Cli.Infra;
using SeedLab.Data;
using SeedLab.Runtime.Execution;
using SeedLab.Locations;
using SeedLab.Render;
using SeedLab.Search.Locations;
using SeedLab.WorldGen;

namespace SeedLab.Cli.Commands
{
    /// <summary>
    /// <c>vseed locations</c> - where the places are in one seed.
    ///
    /// <para><b>What this prints is the generator's output, not what a player will find.</b> The
    /// placement run produces a candidate set; the game writes all of it into the save and only some of
    /// it ever becomes a building. Three things are therefore reported as what they are rather than
    /// flattened into a coordinate: a <c>m_unique</c> type's candidates (the first zone a player
    /// generates wins, which is exploration order and not a function of the seed), the y-rotation of
    /// every instance (drawn from an unseeded ambient stream at spawn time), and the interior layout of
    /// a dungeon whose generator sits off the location's own axis. See
    /// <see cref="PlacementResult.NotPredictable"/>, which this command prints in full.</para>
    /// </summary>
    public static class LocationsCommand
    {
        public const string Help = @"vseed locations <text-or-int> [options]

  Runs the game's own location placement for one seed and lists what it produced.

Options:
  --type <kind>        boss | trader | dungeon | unique | all      (default: boss,trader)
                       several may be comma-separated: --type boss,trader,dungeon
  --name <name>        only these locations, comma-separated. Either spelling works: the
                       prefab (GDKing, Vendor_BlackForest) or the name a player knows
                       (""The Elder"", Haldor). A leading ""the"" may be left off, and case
                       and spacing are ignored - but only for INPUT: every line this
                       command prints names the prefab too, because that is the identity
  --top <n>            the n nearest instances of each type; 0 lists every one (default 10)
  --max-distance <m>   only instances within this many metres of the world centre
  --sort <by>          distance | prefab       (default distance)
  --threads <n>        worker threads for the 2048^2 biome grid (default: every core)
  --json               machine-readable output
  --text | --int       force how the seed token is read

Cost: a full run of all 183 location types takes a few seconds per seed, most of it the
2048x2048 biome-point grid the game itself builds. Selecting fewer types runs a shorter
prefix of the placement list and is correspondingly faster, but never a shorter one than
the selection needs: every earlier type can take a later one's zones.

Examples:
  vseed locations MWd8eV6svz
  vseed locations -1772362158 --type dungeon --top 5
  vseed locations hnBd9gJf2G --name Vendor_BlackForest
  vseed locations hnBd9gJf2G --name ""The Elder"",Haldor
  vseed locations MWd8eV6svz --type all --top 0 --json";

        public static int Run(Args a, Out o, CliRuntime rt)
        {
            if (a.Positional.Count < 1) throw new CliException("give a seed text or an int32.", ExitCodes.Usage, Help);
            SeedRef sr = SeedArg.Resolve(a, a.Positional[0]);

            string typeArg = a.Get("type") ?? "boss,trader";
            string? nameArg = a.Get("name");
            int top = a.Int("top", 10);
            double maxDistance = a.Double("max-distance", double.PositiveInfinity);
            string sort = a.Get("sort") ?? "distance";
            // Placement runs on the game's own hard-coded 2048^2 point grid whatever is asked for, so
            // the footprint is the location tier's and the grid argument does not move it.
            int threads = rt.Workers(WorkTier.LocationsAll, 12.0);
            a.RejectUnknown();

            if (sort != "distance" && sort != "prefab")
                throw new CliException("--sort takes 'distance' or 'prefab', not '" + sort + "'.");
            if (sr.AmbiguityNote != null) Out.Warn(sr.AmbiguityNote);

            LocationCatalog cat = LocationCatalog.Load();

            // Notes about the TABLE go out now, before anything can fail: "the localization table
            // could not be read" has to reach a user whose --name then does not resolve, or the failure
            // reads as arbitrary. Notes about one PLACE wait until the selection is known (below).
            //
            // A note about one place belongs on screen when that place is on screen and nowhere else.
            // The Bog Witch's hand-made join used to print on every run, including '--type boss' where
            // that trader is not in the selection at all, and it shares Out.Warn with the m_unique
            // caveats and the seed-ambiguity note: a caveat repeated where it does not apply is how a
            // reader learns to skip the one channel where this tool says what it cannot know. Nothing
            // is suppressed - see LocationCatalog.NotesFor.
            foreach (string n in cat.NotesFor(null)) Out.Warn(n);

            // ---- what was asked for ----------------------------------------------------------
            List<ZoneLocationEntry> wanted;
            string selection;
            string? selectionAsTyped = null;
            if (nameArg != null)
            {
                // The one resolver, borrowed rather than reimplemented. The five passes (exact prefab,
                // exact display name or alias, then the same three folded, then a leading "the"
                // dropped) and the ambiguity rule live in DumpedLocationOracle, which is what
                // 'location:' in a search query goes through. A second copy here is how --name and
                // location: would come to disagree about what "hildir" means.
                ILocationOracle oracle = SearchCommand.Oracle(out string? oracleProblem);
                if (oracleProblem != null) Out.Warn("names cannot be resolved: " + oracleProblem);

                wanted = new List<ZoneLocationEntry>();
                List<string> resolved = new List<string>();
                List<string> unknown = new List<string>();
                List<string> neverPlaced = new List<string>();
                HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);

                foreach (string raw in nameArg.Split(','))
                {
                    string n = raw.Trim();
                    if (n.Length == 0) continue;

                    // Null from an unavailable oracle means "cannot say", not "no such place", so the
                    // typed string is kept and the exact-prefab scan below still works - a build with
                    // no name table must not lose the spelling it always accepted.
                    LocationNameMatch? m = oracle.Available ? oracle.ResolveLocationName(n) : null;
                    string prefab = m != null ? m.Prefab : n;
                    if (m != null && !string.Equals(m.Prefab, m.AsTyped, StringComparison.Ordinal))
                    {
                        Out.Warn("'" + m.AsTyped + "' is the " + m.How + " of " + m.Prefab
                                 + ", so that is what is listed below"
                                 + (m.Note != null ? ". " + m.Note : ""));
                    }

                    // Table.All, not Table.Ordered: the ordered list is the 183 types the placement run
                    // walks, so a row that is in the build but disabled or m_quantity 0 - the three
                    // StoneHouse*_heath entries - used to come back as "no location called that runs in
                    // this build", which reads as a typo and is not one. The two answers are now
                    // different sentences because they call for different actions.
                    ZoneLocationEntry? e = null;
                    foreach (ZoneLocationEntry x in cat.Table.All)
                        if (string.Equals(x.PrefabName, prefab, StringComparison.Ordinal)) { e = x; break; }

                    if (e == null) { unknown.Add(n); continue; }
                    if (cat.Table.OrderedIndexOf(prefab) < 0) { neverPlaced.Add(prefab); continue; }
                    // Two spellings of one place - "The Elder,GDKing" - are one selection, not two.
                    if (!seen.Add(prefab)) continue;
                    wanted.Add(e);
                    resolved.Add(prefab);
                }

                if (unknown.Count > 0)
                {
                    // A name the game gives to several places does not resolve, by design - Crypt2,
                    // Crypt3 and Crypt4 are all "Burial Chambers" - and "no location called Burial
                    // Chambers" would then be false. Say which prefabs carry it instead, and how to ask
                    // for them; listing all of them silently would make --name and a query's
                    // location: disagree about what one name means.
                    List<string> sharedSentences = new List<string>();
                    List<string> sharedPrefabs = new List<string>();
                    foreach (string u in unknown)
                    {
                        List<string> carriers = PrefabsCalled(oracle, u);
                        if (carriers.Count < 2) continue;
                        sharedSentences.Add("'" + u + "' is the name the game gives to "
                                            + string.Join(", ", carriers.ToArray()));
                        foreach (string c in carriers)
                        {
                            if (!sharedPrefabs.Contains(c)) sharedPrefabs.Add(c);
                        }
                    }

                    if (sharedSentences.Count == unknown.Count)
                    {
                        throw new CliException(
                            string.Join("; ", sharedSentences.ToArray()) + ", so it does not pick one place.",
                            ExitCodes.NotFound,
                            "--name takes their prefabs, comma-separated: --name "
                            + string.Join(",", sharedPrefabs.ToArray()));
                    }

                    throw new CliException(
                        "this build has no location called " + string.Join(", ", unknown.ToArray()) + ".",
                        ExitCodes.NotFound,
                        "--name takes either the prefab (GDKing) or the name a player knows (\"The "
                        + "Elder\"); case and spacing are ignored and a leading \"the\" may be left off. "
                        + "'vseed data --names' lists every prefab with the name it answers to, and "
                        + "'vseed locations <seed> --type all' lists the whole placement table");
                }

                if (neverPlaced.Count > 0)
                {
                    throw new CliException(
                        string.Join(", ", neverPlaced.ToArray())
                        + (neverPlaced.Count == 1 ? " is" : " are")
                        + " in this build's location table but never placed in any seed.",
                        ExitCodes.NotFound,
                        "the entry is present with m_enable false or m_quantity 0, so the placement run "
                        + "does not walk it and every seed holds exactly zero of them - which is a "
                        + "measurement, not a missing name. 'vseed data' reports how many of the table's "
                        + "rows are placed");
                }

                selectionAsTyped = "--name " + nameArg;
                selection = "--name " + string.Join(",", resolved.ToArray());
            }
            else
            {
                LocationKind mask = LocationKind.None;
                bool all = false;
                foreach (string raw in typeArg.Split(','))
                {
                    string t = raw.Trim();
                    if (t.Length == 0) continue;
                    LocationKind k;
                    try { k = LocationCatalog.ParseKind(t); }
                    catch (ArgumentException ex) { throw new CliException(ex.Message); }
                    if (k == LocationKind.None) all = true; else mask |= k;
                }

                wanted = cat.Select(all ? LocationKind.None : mask);
                selection = "--type " + typeArg;
            }

            if (wanted.Count == 0)
                throw new CliException("that selection matches no location type.", ExitCodes.NotFound, Help);

            // Now that the selection is settled: the notes about the places this run is about. The
            // table-wide ones were printed above and are not repeated.
            List<string> selected = new List<string>(wanted.Count);
            foreach (ZoneLocationEntry e in wanted) selected.Add(e.PrefabName);
            foreach (string n in cat.NotesFor(selected, includeTableWide: false)) Out.Warn(n);

            // ---- run -------------------------------------------------------------------------
            int prefix = cat.PrefixFor(wanted);
            Stopwatch sw = Stopwatch.StartNew();
            WorldLocations wl = WorldLocations.Build(sr.Seed, 2, cat.AltBiomes, threads == 0 ? -1 : threads);
            double worldS = sw.Elapsed.TotalSeconds;
            sw.Restart();
            PlacementResult res = LocationPlacementEngine.Run(wl.Generator, wl.Field, cat.Table,
                new PlacementOptions { StopAfterOrderedIndex = prefix - 1, AltBiomesComputed = true });
            double placeS = sw.Elapsed.TotalSeconds;

            // ---- collect ---------------------------------------------------------------------
            List<Row> rows = new List<Row>();
            HashSet<string> wantedNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (ZoneLocationEntry e in wanted) wantedNames.Add(e.PrefabName);

            Dictionary<string, List<Row>> byPrefab = new Dictionary<string, List<Row>>(StringComparer.Ordinal);
            foreach (LocationInstanceResult i in res.Instances)
            {
                if (!wantedNames.Contains(i.PrefabName)) continue;
                double d = Math.Sqrt((double)i.X * i.X + (double)i.Z * i.Z);
                if (d > maxDistance) continue;
                Row r = new Row(i, d, wl.Generator.GetBiome(i.X, i.Z));
                rows.Add(r);
                if (!byPrefab.TryGetValue(i.PrefabName, out List<Row>? l)) { l = new List<Row>(); byPrefab[i.PrefabName] = l; }
                l.Add(r);
            }

            foreach (List<Row> l in byPrefab.Values) l.Sort((x, y) => x.Distance.CompareTo(y.Distance));

            if (o.Json) WriteJson(o, sr, cat, wanted, byPrefab, res, prefix, top, worldS, placeS, selection, selectionAsTyped);
            else WriteHuman(o, sr, cat, wanted, byPrefab, res, prefix, top, sort, worldS, placeS, selection, selectionAsTyped);
            return ExitCodes.Ok;
        }

        private readonly struct Row
        {
            public Row(LocationInstanceResult i, double distance, Biome biome)
            { I = i; Distance = distance; Biome = biome; }

            public LocationInstanceResult I { get; }
            public double Distance { get; }
            public Biome Biome { get; }
        }

        // -------------------------------------------------------------------------------------

        private static void WriteHuman(Out o, SeedRef sr, LocationCatalog cat, List<ZoneLocationEntry> wanted,
                                       Dictionary<string, List<Row>> byPrefab, PlacementResult res, int prefix,
                                       int top, string sort, double worldS, double placeS, string selection,
                                       string? selectionAsTyped)
        {
            o.Header("Locations");
            if (sr.Text != null) o.Field("as typed", "\"" + sr.Text + "\"  (" + sr.How + ")");
            o.Field("int32", sr.Seed.ToString(CultureInfo.InvariantCulture));
            o.Field("game data", cat.Data.Stamp.GameVersion + ", dumped " + cat.Data.Stamp.Dumped
                                + " (" + cat.Data.Stamp.AssemblyValheimSha256.Substring(0, 12) + ")");
            // Both spellings when they differ, so the user can see what their words were read as - the
            // resolved one is what the rest of this report, and any query written from it, will use.
            o.Field("selection", (selectionAsTyped != null && selectionAsTyped != selection
                                     ? selectionAsTyped + "  ->  " + selection + "  ->  "
                                     : selection + "  ->  ")
                                 + wanted.Count + " of " + cat.Table.Ordered.Count + " location types");
            o.Field("names", cat.Names.Language + ", from the " + cat.Data.Stamp.GameVersion + " dump");
            o.Field("placement run", prefix + " of " + cat.Table.Ordered.Count + " types ("
                                     + Out.F(worldS, 2) + " s world, " + Out.F(placeS, 2) + " s placement)");
            o.Note("");
            o.Note("Everything before the selection has to run too: one location per zone, globally, so an");
            o.Note("earlier type can take a later one's zone. The prefix is never shortened past that.");

            List<ZoneLocationEntry> ordered = new List<ZoneLocationEntry>(wanted);
            if (sort == "prefab") ordered.Sort((x, y) => string.CompareOrdinal(x.PrefabName, y.PrefabName));
            else
                ordered.Sort((x, y) =>
                {
                    double dx = Nearest(byPrefab, x.PrefabName), dy = Nearest(byPrefab, y.PrefabName);
                    int c = dx.CompareTo(dy);
                    return c != 0 ? c : string.CompareOrdinal(x.PrefabName, y.PrefabName);
                });

            foreach (ZoneLocationEntry e in ordered)
            {
                byPrefab.TryGetValue(e.PrefabName, out List<Row>? l);
                int have = l?.Count ?? 0;
                LocationTypeResult? t = TypeOf(res, e.PrefabName);

                o.Line();
                string kinds = Kinds(cat, e);
                o.Line("  " + Heading(cat, e.PrefabName) + (kinds.Length > 0 ? "   [" + kinds + "]" : ""));
                o.Line("    " + have + " shown of " + (t?.Placed ?? 0) + " placed, m_quantity "
                       + e.Quantity + ", biome " + BiomeMask(e.Biome)
                       + (t != null && t.Shortfall ? "  (the generator ran out of attempts: "
                          + t.Placed + " of " + e.Quantity + ")" : ""));

                if (e.Unique)
                {
                    o.Line("    m_unique: the game keeps exactly ONE of these " + (t?.Placed ?? 0)
                           + " candidates. Which one is NOT a");
                    o.Line("    function of the seed - the first candidate whose zone a player (or a peer)");
                    o.Line("    generates wins, and ZoneSystem.RemoveUnplacedLocations deletes the others.");
                    o.Line("    All of them are listed; none of them is 'the' position.");
                }

                // The OTHER kind of "this coordinate is not a promise". An m_unique note is about which
                // of these positions is real; this one is about what is inside a position that
                // certainly is. Neither axe-head house is m_unique, so nothing above would have said a
                // word about them - which is exactly why the user could not find the houses with the
                // axe heads. The feature's own sentence is printed verbatim, never paraphrased.
                foreach (WorldFeature f in WorldFeatures.ForPrefab(e.PrefabName))
                {
                    o.Line("    " + f.Name + " - CANDIDATES, not chests you are promised:");
                    o.Line("      " + SearchCommand.Wrap(f.Note, "      ", 88));
                }

                if (have == 0)
                {
                    o.Line("    (none within the distance filter)");
                    continue;
                }

                List<string[]> tr = new List<string[]>();
                int n = top <= 0 ? l!.Count : Math.Min(top, l!.Count);
                for (int i = 0; i < n; i++)
                {
                    Row r = l![i];
                    tr.Add(new[]
                    {
                        Out.F(r.I.X, 0), Out.F(r.I.Z, 0), Out.F(r.Distance, 0),
                        Out.Bearing(WorldSummary.BearingFromOrigin(r.I.X, r.I.Z)),
                        MapPalette.Name(r.Biome), Out.F(r.I.Y, 1),
                        "(" + r.I.Zone.x + ", " + r.I.Zone.y + ")",
                    });
                }

                o.Table(new[] { "x", "z", "dist m", "bearing", "biome", "gen y", "zone" }, tr,
                        new[] { true, true, true, false, false, true, false });
                if (n < l!.Count) o.Note("    ... and " + (l.Count - n) + " more (--top 0 for all)");
            }

            o.Header("What is NOT a function of the seed");
            foreach (string s in PlacementResult.NotPredictable) o.Note("- " + s);
            o.Note("");
            o.Note("'gen y' is WorldGenerator.GetHeight at the point, which is the y the save stores. The");
            o.Note("ground a building finally sits on comes from the built heightmap and the location's own");
            o.Note("terrain edits, so it is not this number.");
            o.Flush();
        }

        private static double Nearest(Dictionary<string, List<Row>> byPrefab, string prefab)
            => byPrefab.TryGetValue(prefab, out List<Row>? l) && l.Count > 0 ? l[0].Distance : double.PositiveInfinity;

        private static LocationTypeResult? TypeOf(PlacementResult res, string prefab)
        {
            foreach (LocationTypeResult t in res.Types)
                if (string.Equals(t.Location.PrefabName, prefab, StringComparison.Ordinal)) return t;
            return null;
        }

        /// <summary>
        /// Every prefab whose DISPLAY name is <paramref name="typed"/> under the resolver's own folds
        /// (case and spacing, then a leading "the"). Used only to explain a name that did not resolve:
        /// two or more hits mean the game gives that name to several places, which the resolver
        /// refuses to pick between.
        /// </summary>
        private static List<string> PrefabsCalled(ILocationOracle oracle, string typed)
        {
            List<string> hits = new List<string>();
            if (!oracle.Available) return hits;

            string folded = LocationNameKey.Fold(typed);
            string article = LocationNameKey.FoldDroppingArticle(typed);
            foreach ((string prefab, string? display) in oracle.LocationNames)
            {
                if (display == null) continue;
                if (LocationNameKey.Fold(display) == folded
                    || LocationNameKey.FoldDroppingArticle(display) == article)
                {
                    hits.Add(prefab);
                }
            }

            return hits;
        }

        /// <summary>
        /// The per-type heading: <c>The Elder  (GDKing)</c> when the dump names the place, the bare
        /// prefab when it does not.
        ///
        /// <para>The prefab is always there, in brackets, because it is the identity every other
        /// surface of this tool takes - a query file, <c>--name</c>, the <c>--json</c> output and a
        /// results file all speak prefab, and a heading that hid it would leave the user with a string
        /// none of them accepts. It is collapsed to one word when the two are equal: the game really
        /// does call Bonemass's altar Bonemass, and <c>Bonemass  (Bonemass)</c> reads as two places.</para>
        /// </summary>
        private static string Heading(LocationCatalog cat, string prefab)
        {
            string? display = cat.DisplayNameOf(prefab);
            return display == null || string.Equals(display, prefab, StringComparison.Ordinal)
                ? prefab
                : display + "  (" + prefab + ")";
        }

        private static string Kinds(LocationCatalog cat, ZoneLocationEntry e)
        {
            LocationKind k = cat.KindOf(e.PrefabName);
            List<string> p = new List<string>();
            if ((k & LocationKind.Boss) != 0) p.Add("boss altar");
            if ((k & LocationKind.Trader) != 0) p.Add("trader");
            if ((k & LocationKind.Dungeon) != 0) p.Add("dungeon x" + cat.GeneratorsOf(e.PrefabName));
            if ((k & LocationKind.Unique) != 0) p.Add("m_unique");
            return string.Join(", ", p.ToArray());
        }

        private static string BiomeMask(Biome b)
        {
            List<string> p = new List<string>();
            foreach (Biome one in MapPalette.LegendOrder)
                if (((int)b & (int)one) != 0) p.Add(MapPalette.Name(one));
            return p.Count == 0 ? "(none)" : string.Join("/", p.ToArray());
        }

        // -------------------------------------------------------------------------------------

        private static void WriteJson(Out o, SeedRef sr, LocationCatalog cat, List<ZoneLocationEntry> wanted,
                                      Dictionary<string, List<Row>> byPrefab, PlacementResult res, int prefix,
                                      int top, double worldS, double placeS, string selection,
                                      string? selectionAsTyped)
        {
            o.J.WriteStartObject();
            o.J.WriteNumber("seed", sr.Seed);
            if (sr.Text != null) o.J.WriteString("seed_text", sr.Text);
            o.J.WriteNumber("world_gen_version", res.WorldGenVersion);
            o.J.WriteString("game_version", cat.Data.Stamp.GameVersion);
            o.J.WriteString("data_stamp_sha256", cat.Data.Stamp.AssemblyValheimSha256);
            // 'selection' is the RESOLVED one - prefabs, always, because that is what a machine reader
            // has to feed back into --name, into a query file or into a results comparison. What the
            // user typed is kept beside it rather than instead of it, so a script can echo their words
            // without ever mistaking them for the identity.
            o.J.WriteString("selection", selection);
            if (selectionAsTyped != null) o.J.WriteString("selection_as_typed", selectionAsTyped);
            o.J.WriteString("names_language", cat.Names.Language);
            o.J.WriteNumber("types_run", prefix);
            o.J.WriteNumber("types_total", cat.Table.Ordered.Count);
            o.J.WriteNumber("seconds_world", worldS);
            o.J.WriteNumber("seconds_placement", placeS);

            o.J.WriteStartArray("types");
            foreach (ZoneLocationEntry e in wanted)
            {
                LocationTypeResult? t = TypeOf(res, e.PrefabName);
                byPrefab.TryGetValue(e.PrefabName, out List<Row>? l);

                LocationDisplayName name = cat.Names.For(e.PrefabName);

                o.J.WriteStartObject();
                // 'prefab' is unchanged and remains THE identity: every name field below is additive,
                // and a reader that ignores them all reads exactly the document it read before. The
                // display fields are null - not the prefab repeated - where the dump has no name, so
                // "unnamed" and "named the same as its prefab" (Bonemass) stay distinguishable.
                o.J.WriteString("prefab", e.PrefabName);
                if (name.DisplayName == null) o.J.WriteNull("display_name");
                else o.J.WriteString("display_name", name.DisplayName);
                if (name.DisplayName == null) o.J.WriteNull("display_name_source");
                else o.J.WriteString("display_name_source", name.Source.ToString());
                if (name.NameToken == null) o.J.WriteNull("name_token");
                else o.J.WriteString("name_token", name.NameToken);
                o.J.WriteStartArray("aliases");
                foreach (string alias in name.Aliases) o.J.WriteStringValue(alias);
                o.J.WriteEndArray();
                o.J.WriteString("kinds", Kinds(cat, e));
                o.J.WriteNumber("quantity", e.Quantity);
                o.J.WriteNumber("placed", t?.Placed ?? 0);
                o.J.WriteBoolean("shortfall", t != null && t.Shortfall);
                o.J.WriteBoolean("unique", e.Unique);
                o.J.WriteBoolean("single_position_is_predictable", !e.Unique || (t?.Placed ?? 0) <= 1);
                o.J.WriteNumber("dungeon_generators", cat.GeneratorsOf(e.PrefabName));
                o.J.WriteString("biome_mask", BiomeMask(e.Biome));

                // Keyed by PREFAB, so a run that asked for WoodHouse6 by itself carries the same
                // caveat a run that asked for the whole axe_head_houses group does.
                foreach (WorldFeature f in WorldFeatures.ForPrefab(e.PrefabName))
                {
                    o.J.WriteString("feature_name", f.Name);
                    o.J.WriteString("contents_note", f.Note);
                    break;
                }

                o.J.WriteStartArray("instances");
                int n = l == null ? 0 : (top <= 0 ? l.Count : Math.Min(top, l.Count));
                for (int i = 0; i < n; i++)
                {
                    Row r = l![i];
                    o.J.WriteStartObject();
                    o.J.WriteNumber("x", r.I.X);
                    o.J.WriteNumber("z", r.I.Z);
                    o.J.WriteNumber("y", r.I.Y);
                    o.J.WriteNumber("distance_m", r.Distance);
                    o.J.WriteNumber("bearing_deg", WorldSummary.BearingFromOrigin(r.I.X, r.I.Z));
                    o.J.WriteString("biome", MapPalette.Name(r.Biome));
                    o.J.WriteNumber("zone_x", r.I.Zone.x);
                    o.J.WriteNumber("zone_y", r.I.Zone.y);
                    o.J.WriteString("x_bits", "0x" + ((uint)BitConverter.SingleToInt32Bits(r.I.X)).ToString("X8"));
                    o.J.WriteString("z_bits", "0x" + ((uint)BitConverter.SingleToInt32Bits(r.I.Z)).ToString("X8"));
                    o.J.WriteString("y_bits", "0x" + ((uint)BitConverter.SingleToInt32Bits(r.I.Y)).ToString("X8"));
                    o.J.WriteEndObject();
                }

                o.J.WriteEndArray();
                o.J.WriteNumber("instances_omitted", (l?.Count ?? 0) - n);
                o.J.WriteEndObject();
            }

            o.J.WriteEndArray();

            o.J.WriteStartArray("not_predictable");
            foreach (string s in PlacementResult.NotPredictable) o.J.WriteStringValue(s);
            o.J.WriteEndArray();
            o.J.WriteEndObject();
        }
    }
}
