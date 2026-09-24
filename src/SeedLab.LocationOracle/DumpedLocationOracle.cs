using System;
using System.Collections.Generic;
using System.Threading;
using SeedLab.Contracts.Dump;
using SeedLab.Data;
using SeedLab.Locations;
using SeedLab.Search.Locations;
using SeedLab.WorldGen;

namespace SeedLab.LocationOracle
{
    /// <summary>
    /// The real <see cref="ILocationOracle"/>: <c>SeedLab.Locations</c>' placement engine driven by the
    /// dumped <c>ZoneSystem.m_locations</c>, answering boss, trader, dungeon and world-feature goals
    /// for <c>vseed search</c> and <c>vseed explain</c>.
    ///
    /// <para><b>What a seed costs, measured on this machine (16 logical cores, one seed per thread, so
    /// each seed's own work is serial).</b> The placement itself is the small half:</para>
    /// <list type="bullet">
    /// <item>WorldGeneratorPort constructor and lake/river/stream pre-generation - 0.31-0.44 s;</item>
    /// <item>the 2048^2 biome-and-height point grid (4,194,304 <c>GetBiome</c> + <c>GetBiomeHeight</c>
    /// pairs) - 1.44-1.54 s;</item>
    /// <item>the sector decomposition - 0.064-0.069 s, and the alt-biome assignment 0.001-0.003 s;</item>
    /// <item>the placement prefix - 0.002 s for one boss, 0.07 s for all seven, 0.08-0.11 s for
    /// bosses and traders, 1.1-1.4 s to reach Crypt4, 6.5-6.8 s for the full 183 entries.</item>
    /// </list>
    ///
    /// <para><b>So the ordered prefix is worth exactly what it saves and no more.</b> The grid is not
    /// optional - <c>GetRandomPointByBiomes</c> draws the candidate zone out of it, and filters 10a and
    /// 10b read the sector it belongs to - so roughly 1.9 s per seed is a floor that no query can go
    /// under. Cutting the placement from 6.7 s to 0.07 s takes a boss query from ~8.5 s to ~1.9 s per
    /// seed per thread, which is a 4.4x speed-up, not a 100x one. The honest whole-space figures are in
    /// the task report and in <c>vseed search</c>'s own cost line; none of them is short.</para>
    ///
    /// <para><b>Thread model.</b> One <see cref="Worker"/> per calling thread, held in a
    /// <see cref="ThreadLocal{T}"/>: the alt-biome runtime objects carry a per-world sector list that
    /// <c>AltBiomeAssignment.Generate</c> resets, so one set cannot serve two worlds at once. The
    /// 20 MB of grid arrays are owned by the worker and reused across seeds; the table itself is
    /// immutable and shared.</para>
    ///
    /// <para><b>It fails closed.</b> Construction runs <c>GameData.RequireUsableForAssetData</c>, so a
    /// data folder that does not describe the installed game refuses to answer location questions
    /// rather than returning coordinates that look right (<see cref="DataPolicy"/>).</para>
    /// </summary>
    public sealed class DumpedLocationOracle : ILocationOracle, IDisposable
    {
        private readonly GameData _data;
        private readonly LocationTable _table;
        private readonly Dictionary<string, LocationTypeInfo> _types;
        private readonly List<AltBiomeDef> _altBiomeDefs;
        private readonly ThreadLocal<Worker> _workers;
        private readonly NameIndex _names;

        /// <summary><c>ZoneSystem</c>'s own name for the world's spawn location.</summary>
        public const string StartTemplePrefab = "StartTemple";

        private DumpedLocationOracle(GameData data, LocationTable table)
        {
            _data = data;
            _table = table;

            _altBiomeDefs = new List<AltBiomeDef>(data.AltBiomes);

            // ONCE, here, and never inside Worker: the name table costs about 180 ms of cold file
            // load and the presentation costs one sort of ~216 rows, and a Worker runs per seed on
            // every thread. Nothing below this line is reachable from Run().
            _names = NameIndex.Build(data, table);

            _types = new Dictionary<string, LocationTypeInfo>(StringComparer.Ordinal);
            foreach (ZoneLocationEntry e in table.All)
            {
                if (_types.ContainsKey(e.PrefabName)) continue;   // SetupLocations keeps the first
                int ordered = table.OrderedIndexOf(e.PrefabName);
                _types.Add(e.PrefabName, new LocationTypeInfo(
                    e.PrefabName, e.Name, ordered >= 0, ordered, e.Quantity, e.Unique, e.Prioritized,
                    e.Biome, e.MinDistance, e.MaxDistance, e.MinDistanceFromCenter, e.MaxDistanceFromCenter,
                    e.AltBiomeParent, (int)e.BiomeArea, _names.PresentationOf(e.PrefabName)));
            }

            _workers = new ThreadLocal<Worker>(() => new Worker(_table, _altBiomeDefs), trackAllValues: false);
        }

        /// <summary>
        /// Opens the shipped data and builds the table, or explains why it cannot. Never throws for a
        /// missing or stale dump - the caller keeps the refusing oracle and the message.
        /// </summary>
        public static ILocationOracle Create(out string? problem)
        {
            try
            {
                GameData data = GameData.Load();
                data.RequireUsableForAssetData("a boss, trader, dungeon or world-feature search goal");
                LocationTable table = LocationTable.FromDump(data.Locations);
                problem = null;
                return new DumpedLocationOracle(data, table);
            }
            // StaleGameDataException is the EXPECTED outcome of the RequireUsableForAssetData call
            // above, not a fault: DataPolicy refuses locations on a mismatched build and lets terrain
            // through. It is a separate Exception subclass, not a GameDataException, so leaving it out
            // of this filter sent it to Program.cs's last-resort handler - "this is a bug", exit 4 -
            // for every query, including one with no location goal at all. It belongs here, with its
            // message, exactly like a missing dump.
            catch (Exception ex) when (ex is GameDataException || ex is StaleGameDataException
                                       || ex is InvalidOperationException
                                       || ex is System.IO.IOException)
            {
                problem = ex.Message;
                return UnavailableLocationOracle.Instance;
            }
        }

        /// <summary>The same, but throwing - for a caller that has already decided it needs locations.</summary>
        public static DumpedLocationOracle Open()
        {
            GameData data = GameData.Load();
            data.RequireUsableForAssetData("a boss, trader, dungeon or world-feature search goal");
            return new DumpedLocationOracle(data, LocationTable.FromDump(data.Locations));
        }

        public bool Available => true;

        public string UnavailableReason => "";

        public string Provenance => _data.Stamp.GameVersion + " / "
                                    + _data.Stamp.AssemblyValheimSha256.Substring(0, 8)
                                    + " (" + _table.All.Count + " entries, " + _table.Ordered.Count + " ordered)";

        /// <summary>The table this oracle drives, for a caller that wants to report on it.</summary>
        public LocationTable Table => _table;

        public bool KnowsPrefab(string prefabName) => _types.ContainsKey(prefabName);

        public LocationTypeInfo? TypeOf(string prefabName)
            => _types.TryGetValue(prefabName, out LocationTypeInfo? t) ? t : null;

        public IEnumerable<string> PrefabNames => _types.Keys;

        public IReadOnlyList<string>? ExpandGroup(string groupName) => LocationGroups.Expand(groupName);

        /// <summary>
        /// What a typed name means, built once at construction - see <see cref="NameIndex"/> for the
        /// order of the passes and why an exact prefab spelling always wins. Null here means "no
        /// prefab, display name or alias matches", because this oracle IS available; the
        /// cannot-say null is <see cref="UnavailableLocationOracle"/>'s.
        /// </summary>
        public LocationNameMatch? ResolveLocationName(string typed) => _names.Resolve(typed);

        public IEnumerable<(string Prefab, string? Display)> LocationNames => _names.Pairs;

        /// <summary>
        /// The derived name table itself, for a caller that wants the provenance, the aliases or the
        /// language rather than just the name. Null only when the naming files could not be read at
        /// all - placements still work then, and <see cref="NameNotes"/> says why.
        /// </summary>
        public LocationDisplayNames? DisplayNames => _names.Names;

        /// <summary>
        /// Anything a surface should print about the NAMES, as opposed to the placements: a rule that
        /// matched nothing, a token with no entry, the Bog Witch's hand-made join, a fold collision
        /// that cost a spelling. Empty is the expected state; it is not empty today, because the
        /// Bog Witch note is deliberately permanent.
        /// </summary>
        public IReadOnlyList<string> NameNotes => _names.Notes;

        public LocationPlan Plan(IReadOnlyList<string> prefabs, bool needSpawn)
        {
            if (prefabs == null) throw new ArgumentNullException(nameof(prefabs));

            List<LocationTypeInfo> types = new List<LocationTypeInfo>(prefabs.Count + 1);
            foreach (string p in prefabs)
            {
                if (!_types.TryGetValue(p, out LocationTypeInfo? t))
                    throw new KeyNotFoundException("'" + p + "' is not a location prefab in this dump.");
                types.Add(t);
            }

            if (needSpawn && _types.TryGetValue(StartTemplePrefab, out LocationTypeInfo? temple))
            {
                bool present = false;
                foreach (LocationTypeInfo t in types) if (t.Prefab == StartTemplePrefab) present = true;
                if (!present) types.Add(temple);
            }

            // THE ordered-prefix rule. A type's own stream is independent of every other type, so
            // nothing after it can change it; but zone occupancy is global and the AssetID, group and
            // CountNrOfLocation buckets are shared, so no predecessor can be skipped. The correct
            // prefix is max(orderedIndex) + 1 over the query's own types, and nothing shorter is sound
            // (LocationTable.PrefixLengthForTarget).
            int prefix = 0;
            foreach (LocationTypeInfo t in types)
            {
                if (t.Placeable && t.PrefixLength > prefix) prefix = t.PrefixLength;
            }

            types.Sort((a, b) => a.OrderedIndex.CompareTo(b.OrderedIndex));
            List<string> ordered = new List<string>(types.Count);
            foreach (LocationTypeInfo t in types) ordered.Add(t.Prefab);

            return new LocationPlan(ordered, prefix, _table.Ordered.Count, needSpawn, types);
        }

        public LocationWorld Run(LocationPlan plan, int seed, int worldGenVersion, LocationTypeGate? gate)
        {
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            return _workers.Value!.Run(plan, seed, worldGenVersion, gate);
        }

        public void Dispose() => _workers.Dispose();

        /// <summary>
        /// Prefab-to-name, name-to-prefab and where-each-row-is-listed, all computed ONCE at
        /// construction.
        ///
        /// <para><b>Resolution order - the first pass that hits wins.</b> An exact ordinal PREFAB
        /// match is pass 1, so no invocation that works today can change meaning; display names and
        /// aliases are only reached afterwards, and only then are case and spacing forgiven. That
        /// asymmetry is deliberate: a prefab name is an identifier the user copies out of this
        /// tool's own output, while a display name is prose typed from memory.</para>
        /// <list type="number">
        /// <item>exact <c>Ordinal</c> prefab</item>
        /// <item>exact <c>Ordinal</c> display name or alias</item>
        /// <item>folded prefab (<c>gdking</c>, <c>hildir camp</c>)</item>
        /// <item>folded display name or alias (<c>the elder</c>, <c>THE-ELDER</c>)</item>
        /// <item>folded with a leading <c>the</c> dropped (<c>elder</c>) - input tolerance only</item>
        /// </list>
        ///
        /// <para><b>No prefix match, no substring match, no edit distance.</b> Measured on this dump:
        /// <c>Hildir</c> is a prefix of four placed prefabs, and <c>queen</c> is a substring of
        /// <c>Dragonqueen</c> - which is MODER's altar, in the Mountains, while "The Queen" is a
        /// Mistlands boss entrance. A substring matcher answers a Mistlands question with a Mountain
        /// altar. Near misses belong in a hint, never in the resolver.</para>
        ///
        /// <para><b>A collision drops the key and says so.</b> If two different prefabs ever claim one
        /// folded key, that key stops resolving to either and a Note records which spelling stopped
        /// working - the alternative, picking a winner, would answer a question the data does not
        /// answer. All three fold indexes are collision-free in 1.0.15.</para>
        ///
        /// <para><b>A name the game gives to several prefabs resolves to none, silently.</b> Since the
        /// dungeon doors were dumped (2026-09-24) three captions are shared: "Burial Chambers"
        /// (Crypt2/3/4), "Infested Mine" (both Dvergr town entrances) and "Putrid Hole"
        /// (MorgenHole1/2/3). That is the game's naming, not a lost spelling, so it drops the key
        /// without a note; typing it is refused and the refusal lists the prefabs
        /// (<see cref="SharedDisplayNames"/>).</para>
        /// </summary>
        private sealed class NameIndex
        {
            /// <summary>Sentinel for a key two different prefabs claim. A value, not a deletion, so a
            /// third spelling of the same collision cannot quietly re-add it.</summary>
            private const string Ambiguous = "\u0000ambiguous";

            /// <summary>Sentinel for a key the game itself gives to several prefabs - "Burial Chambers"
            /// is Crypt2, Crypt3 and Crypt4. It resolves to nothing, like <see cref="Ambiguous"/>, but
            /// it is not a lost spelling and says nothing; see <see cref="SharedDisplayNames"/>.</summary>
            private const string SharedName = "\u0000shared";

            private readonly Dictionary<string, string> _exactDisplay;
            private readonly Dictionary<string, string> _foldedPrefab;
            private readonly Dictionary<string, string> _foldedDisplay;
            private readonly Dictionary<string, string> _articleDisplay;
            private readonly Dictionary<string, LocationPresentation> _presentation;
            private readonly HashSet<string> _prefabs;
            private readonly List<(string Prefab, string? Display)> _pairs;
            private readonly List<string> _notes;

            private NameIndex(LocationDisplayNames? names, HashSet<string> prefabs,
                              Dictionary<string, string> exactDisplay,
                              Dictionary<string, string> foldedPrefab,
                              Dictionary<string, string> foldedDisplay,
                              Dictionary<string, string> articleDisplay,
                              Dictionary<string, LocationPresentation> presentation,
                              List<(string Prefab, string? Display)> pairs, List<string> notes)
            {
                Names = names;
                _prefabs = prefabs;
                _exactDisplay = exactDisplay;
                _foldedPrefab = foldedPrefab;
                _foldedDisplay = foldedDisplay;
                _articleDisplay = articleDisplay;
                _presentation = presentation;
                _pairs = pairs;
                _notes = notes;
            }

            /// <summary>Null only when the naming files could not be read at all; prefab spellings
            /// still work in that case and the reason is in <see cref="Notes"/>.</summary>
            public LocationDisplayNames? Names { get; }

            public IReadOnlyList<string> Notes => _notes;

            public IEnumerable<(string Prefab, string? Display)> Pairs => _pairs;

            public LocationPresentation? PresentationOf(string prefab)
                => _presentation.TryGetValue(prefab, out LocationPresentation? p) ? p : null;

            public static NameIndex Build(GameData data, LocationTable table)
            {
                List<string> notes = new List<string>();

                // Every distinct prefab in the table, INCLUDING the rows that never place: --name has
                // to be able to answer "in the table, never placed" as something other than "no such
                // place", and that needs the unplaced ones in the index.
                HashSet<string> prefabs = new HashSet<string>(StringComparer.Ordinal);
                List<string> order = new List<string>();
                foreach (ZoneLocationEntry e in table.All)
                {
                    if (prefabs.Add(e.PrefabName)) order.Add(e.PrefabName);
                }

                LocationDisplayNames? names = null;
                try
                {
                    names = data.DisplayNames;
                    foreach (string n in names.Notes) notes.Add(n);
                }
                catch (Exception ex) when (ex is GameDataException || ex is System.IO.IOException)
                {
                    // Naming is not what a SEARCH needs, so a dump that cannot be named must still be
                    // able to place locations. Prefab spellings keep working, display names simply do
                    // not exist, and the reason is printed rather than swallowed.
                    notes.Add("no location can be shown by its player-facing name in this dump: "
                              + ex.Message);
                }

                Dictionary<string, string> exactDisplay = new Dictionary<string, string>(StringComparer.Ordinal);
                Dictionary<string, string> foldedPrefab = new Dictionary<string, string>(StringComparer.Ordinal);
                Dictionary<string, string> foldedDisplay = new Dictionary<string, string>(StringComparer.Ordinal);
                Dictionary<string, string> articleDisplay = new Dictionary<string, string>(StringComparer.Ordinal);

                foreach (string prefab in order)
                {
                    Add(foldedPrefab, LocationNameKey.Fold(prefab), prefab, notes, "folded prefab name");
                }

                List<(string Prefab, string? Display)> pairs =
                    new List<(string Prefab, string? Display)>(order.Count);

                Dictionary<string, HashSet<string>> shared = SharedDisplayNames(names, order);

                foreach (string prefab in order)
                {
                    LocationDisplayName? n = names?.For(prefab);
                    pairs.Add((prefab, n?.DisplayName));
                    if (n == null) continue;

                    foreach (string alias in n.Aliases)
                    {
                        // Aliases[0] IS the prefab; it is already passes 1 and 3, and indexing it here
                        // as well would only manufacture self-collisions in the display passes.
                        if (string.Equals(alias, prefab, StringComparison.Ordinal)) continue;
                        HashSet<string>? sharers = shared.TryGetValue(alias, out HashSet<string>? s) ? s : null;
                        Add(exactDisplay, alias, prefab, notes, "display name", sharers);
                        Add(foldedDisplay, LocationNameKey.Fold(alias), prefab, notes,
                            "folded display name", sharers);
                        Add(articleDisplay, LocationNameKey.FoldDroppingArticle(alias), prefab, notes,
                            "display name with a leading 'the' dropped", sharers);
                    }
                }

                Dictionary<string, LocationPresentation> presentation =
                    BuildPresentation(table, order, names, notes);

                return new NameIndex(names, prefabs, exactDisplay, foldedPrefab, foldedDisplay,
                                     articleDisplay, presentation, pairs, notes);
            }

            /// <summary>Indexes one key, or - when a DIFFERENT prefab already holds it - marks it
            /// ambiguous and records which spelling stopped working. <paramref name="sharers"/> is the
            /// set of prefabs the game gives this very name (see <see cref="SharedDisplayNames"/>); a
            /// collision INSIDE that set is the game's naming, not a fold that went wrong, so it is
            /// marked ambiguous without a note.</summary>
            private static void Add(Dictionary<string, string> index, string key, string prefab,
                                    List<string> notes, string what, HashSet<string>? sharers = null)
            {
                if (key.Length == 0) return;
                if (!index.TryGetValue(key, out string? existing))
                {
                    index[key] = prefab;
                    return;
                }

                if (string.Equals(existing, prefab, StringComparison.Ordinal)) return;
                if (string.Equals(existing, Ambiguous, StringComparison.Ordinal)) return;

                if (string.Equals(existing, SharedName, StringComparison.Ordinal))
                {
                    // Another sharer of the same name: nothing new. Anything else reaching this key
                    // IS a collision, and a real one must not hide behind the game's shared name.
                    if (sharers != null && sharers.Contains(prefab)) return;
                    index[key] = Ambiguous;
                    notes.Add("the " + what + " '" + key + "' is the game's name for several places and "
                              + "now also means '" + prefab + "'; spell the one you mean as its prefab name.");
                    return;
                }

                bool shared = sharers != null && sharers.Contains(existing) && sharers.Contains(prefab);
                index[key] = shared ? SharedName : Ambiguous;
                if (shared) return;

                notes.Add("the " + what + " '" + key + "' now means both '" + existing + "' and '"
                          + prefab + "', so it no longer resolves to either; spell the one you mean as "
                          + "its prefab name.");
            }

            /// <summary>
            /// Display names the derivation gives to MORE than one prefab, each with the prefabs that
            /// carry it - "Burial Chambers" for <c>Crypt2</c>, <c>Crypt3</c> and <c>Crypt4</c>.
            ///
            /// <para>These are not collisions in the sense the note above is written for. That note
            /// exists for a spelling that used to mean one place and silently stopped - two different
            /// names folding onto one key. Here the game itself captions three variants of one dungeon
            /// identically (the same <c>m_enterText</c> token on each entrance door, first dumped
            /// 2026-09-24), so the name never meant one prefab and no spelling was lost. It still does
            /// not RESOLVE - picking one of the three would answer a question the data does not answer
            /// - and a query that types it is refused with the three prefabs listed, which is where the
            /// user needs to hear it. A permanent note printed on every run would say the same thing
            /// to everyone who never typed it.</para>
            ///
            /// <para>Only a name that is the DISPLAY name of every prefab in the set, from the same
            /// source rule and the same token, qualifies. A display name that equals some other place's
            /// alias is still a real collision and still notes.</para>
            /// </summary>
            private static Dictionary<string, HashSet<string>> SharedDisplayNames(
                LocationDisplayNames? names, List<string> order)
            {
                Dictionary<string, List<LocationDisplayName>> byName =
                    new Dictionary<string, List<LocationDisplayName>>(StringComparer.Ordinal);
                if (names != null)
                {
                    foreach (string prefab in order)
                    {
                        LocationDisplayName n = names.For(prefab);
                        if (n.DisplayName == null) continue;
                        if (!byName.TryGetValue(n.DisplayName, out List<LocationDisplayName>? list))
                        {
                            list = new List<LocationDisplayName>();
                            byName[n.DisplayName] = list;
                        }

                        list.Add(n);
                    }
                }

                Dictionary<string, HashSet<string>> outp =
                    new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
                foreach (KeyValuePair<string, List<LocationDisplayName>> kv in byName)
                {
                    if (kv.Value.Count < 2) continue;

                    bool sameOrigin = true;
                    foreach (LocationDisplayName n in kv.Value)
                    {
                        if (n.Source != kv.Value[0].Source
                            || !string.Equals(n.NameToken, kv.Value[0].NameToken, StringComparison.Ordinal))
                        {
                            sameOrigin = false;
                        }
                    }

                    if (!sameOrigin) continue;

                    HashSet<string> set = new HashSet<string>(StringComparer.Ordinal);
                    foreach (LocationDisplayName n in kv.Value) set.Add(n.Prefab);
                    outp[kv.Key] = set;
                }

                return outp;
            }

            /// <summary>
            /// Which group each type is listed in, and where inside it.
            ///
            /// <para>The boss and trader groups are keyed off the DERIVED name source, so the
            /// grouping cannot disagree with the naming: a place named by its OfferingBowl is a boss
            /// altar by the same evidence that gave it its name. Everything else is decided by the raw
            /// <c>ZoneLocation.m_biome</c> mask, and a mask naming several biomes lands in the
            /// several-biomes group rather than being repeated in each.</para>
            ///
            /// <para><see cref="LocationPresentation.SortIndex"/> is computed over EVERY known prefab,
            /// including the ones that never place. Only the order matters, and dropping rows cannot
            /// reorder what is left, so a consumer that lists placeable types only still gets exactly
            /// the golden listing.</para>
            /// </summary>
            private static Dictionary<string, LocationPresentation> BuildPresentation(
                LocationTable table, List<string> order, LocationDisplayNames? names, List<string> notes)
            {
                Dictionary<string, Biome> biome = new Dictionary<string, Biome>(StringComparer.Ordinal);
                foreach (ZoneLocationEntry e in table.All)
                {
                    if (!biome.ContainsKey(e.PrefabName)) biome[e.PrefabName] = e.Biome;
                }

                Dictionary<string, List<string>> members =
                    new Dictionary<string, List<string>>(StringComparer.Ordinal);
                Dictionary<string, LocationGroupHeading> groupOf =
                    new Dictionary<string, LocationGroupHeading>(StringComparer.Ordinal);

                foreach (string prefab in order)
                {
                    LocationDisplayName? n = names?.For(prefab);
                    bool isBoss = n != null && n.Source == DisplayNameSource.BossAltar;
                    bool isTrader = n != null && (n.Source == DisplayNameSource.Trader
                                                  || n.Source == DisplayNameSource.TraderNpcTokenConvention);

                    LocationGroupHeading g = LocationGroupTaxonomy.GroupFor(
                        isBoss, isTrader, biome.TryGetValue(prefab, out Biome b) ? b : Biome.None);

                    groupOf[prefab] = g;
                    if (!members.TryGetValue(g.Key, out List<string>? list))
                    {
                        list = new List<string>();
                        members[g.Key] = list;
                    }

                    list.Add(prefab);
                }

                Dictionary<string, LocationPresentation> outp =
                    new Dictionary<string, LocationPresentation>(order.Count, StringComparer.Ordinal);

                foreach (KeyValuePair<string, List<string>> group in members)
                {
                    List<string> list = group.Value;
                    list.Sort((a, b) => LocationDisplayOrder.Compare(TextOf(names, a), a,
                                                                     TextOf(names, b), b));

                    for (int i = 0; i < list.Count; i++)
                    {
                        string prefab = list[i];
                        LocationDisplayName? n = names?.For(prefab);
                        LocationGroupHeading g = groupOf[prefab];
                        string? source = n == null || n.DisplayName == null ? null : n.Source.ToString();
                        outp[prefab] = new LocationPresentation(
                            prefab, n?.DisplayName, source, n?.NameToken,
                            n?.Aliases ?? new[] { prefab }, g.Key, g.Heading, g.Order, i);
                    }
                }

                // A display name that IS its own prefab makes "named" and "unnamed" indistinguishable
                // on every surface downstream. Bonemass is the one place the game really does use the
                // same string for both, which is why this is a note and not a refusal.
                foreach (KeyValuePair<string, LocationPresentation> kv in outp)
                {
                    if (kv.Value.DisplayName == null) continue;
                    if (!string.Equals(kv.Value.DisplayName, kv.Key, StringComparison.Ordinal)) continue;
                    if (string.Equals(kv.Key, "Bonemass", StringComparison.Ordinal)) continue;
                    notes.Add(kv.Key + ": its display name is the same string as its prefab name, which "
                              + "makes 'named' and 'unnamed' indistinguishable on every surface. Check "
                              + "the derivation before trusting it.");
                }

                return outp;
            }

            private static string TextOf(LocationDisplayNames? names, string prefab)
                => names?.For(prefab).DisplayName ?? prefab;

            public LocationNameMatch? Resolve(string typed)
            {
                if (string.IsNullOrEmpty(typed)) return null;
                string t = typed.Trim();
                if (t.Length == 0) return null;

                if (_prefabs.Contains(t)) return Match(t, typed, "prefab name");

                string? hit = Lookup(_exactDisplay, t);
                if (hit != null) return Match(hit, typed, "display name");

                hit = Lookup(_foldedPrefab, LocationNameKey.Fold(t));
                if (hit != null) return Match(hit, typed, "prefab name (case and spacing folded)");

                hit = Lookup(_foldedDisplay, LocationNameKey.Fold(t));
                if (hit != null) return Match(hit, typed, "display name (case and spacing folded)");

                hit = Lookup(_articleDisplay, LocationNameKey.FoldDroppingArticle(t));
                if (hit != null) return Match(hit, typed, "display name (leading 'the' dropped)");

                return null;
            }

            private static string? Lookup(Dictionary<string, string> index, string key)
            {
                if (key.Length == 0) return null;
                if (!index.TryGetValue(key, out string? prefab)) return null;
                return string.Equals(prefab, Ambiguous, StringComparison.Ordinal)
                       || string.Equals(prefab, SharedName, StringComparison.Ordinal)
                    ? null
                    : prefab;
            }

            private LocationNameMatch Match(string prefab, string typed, string how)
            {
                LocationDisplayName? n = Names?.For(prefab);
                string? note = n != null && n.IsDerivedByConvention
                    ? "Unverified: '" + n.DisplayName + "' comes from " + n.NameToken + " by a join "
                      + "made by hand and confirmed in game, not by the dump itself."
                    : null;
                return new LocationNameMatch(prefab, typed, n?.DisplayName, how, note);
            }
        }

        /// <summary>
        /// One thread's reusable world-building state. The 4 MB biome array and the 16 MB height array
        /// are kept for the life of the thread rather than handed to the collector once per seed; the
        /// alt-biome runtime objects are rebuilt from the dump once and then reset per world by
        /// <c>AltBiomeAssignment.Generate</c>, which is why they cannot be shared between threads.
        /// </summary>
        private sealed class Worker
        {
            private readonly LocationTable _table;
            private readonly List<AltBiomeRuntime> _alts;
            private readonly byte[] _biomes = new byte[BiomeGrid.PointCount];
            private readonly float[] _heights = new float[BiomeGrid.PointCount];

            /// <summary>Reused per seed; cleared, not reallocated, so a scan does not churn the heap.</summary>
            private readonly List<LocationHit> _hits = new List<LocationHit>(4096);

            private readonly HashSet<string> _wanted = new HashSet<string>(StringComparer.Ordinal);
            private LocationPlan? _plannedFor;

            public Worker(LocationTable table, IReadOnlyList<AltBiomeDef> altBiomeDefs)
            {
                _table = table;
                _alts = new List<AltBiomeRuntime>(altBiomeDefs.Count);
                foreach (AltBiomeDef d in altBiomeDefs) _alts.Add(AltBiomeRuntime.FromDump(d));
            }

            public LocationWorld Run(LocationPlan plan, int seed, int worldGenVersion, LocationTypeGate? gate)
            {
                if (!ReferenceEquals(_plannedFor, plan))
                {
                    _wanted.Clear();
                    foreach (string p in plan.Prefabs) _wanted.Add(p);
                    _plannedFor = plan;
                }

                // Nothing here is cached across seeds: the generator, the grid contents, the sectors
                // and the alt-biome assignment are all functions of THIS seed and are all rebuilt.
                WorldGeneratorPort gen = new WorldGeneratorPort(seed, worldGenVersion, menu: false);
                BiomeGrid grid = BiomeGrid.Build(gen, 1, _biomes, _heights);
                BiomeField field = BiomeField.Build(grid);
                AltBiomeAssignment.Generate(field, _alts, seed);

                _hits.Clear();
                List<LocationHit> hits = _hits;
                HashSet<string> wanted = _wanted;
                ZoneLocationEntry? stoppedAfter = null;

                // The engine hands us the WHOLE instance list after each type; we copy out only the
                // prefabs this query asked about, so the gate and the final read see the same view.
                int copied = 0;
                Func<ZoneLocationEntry, IReadOnlyList<LocationInstanceResult>, bool>? hook = null;
                if (gate != null)
                {
                    hook = (entry, instances) =>
                    {
                        Harvest(instances, ref copied, hits, wanted);
                        if (!wanted.Contains(entry.PrefabName)) return true;
                        return gate(entry.PrefabName, hits);
                    };
                }

                PlacementResult res = LocationPlacementEngine.Run(gen, field, _table, new PlacementOptions
                {
                    StopAfterOrderedIndex = plan.PrefixLength - 1,
                    AltBiomesComputed = true,
                    ContinueAfterType = hook,
                });

                stoppedAfter = res.StoppedEarly;
                Harvest(res.Instances, ref copied, hits, wanted);

                bool hasSpawn = false;
                float sx = 0f, sz = 0f;
                if (plan.NeedSpawn)
                {
                    foreach (LocationHit h in hits)
                    {
                        if (!string.Equals(h.Prefab, StartTemplePrefab, StringComparison.Ordinal)) continue;
                        hasSpawn = true;
                        sx = h.X;
                        sz = h.Z;
                        break;
                    }
                }

                // A copy, because the worker's list is about to be reused for the next seed.
                return new LocationWorld(hits.ToArray(), hasSpawn, sx, sz,
                                         stoppedAfter != null, stoppedAfter?.PrefabName,
                                         res.LastOrderedIndexRun);
            }

            /// <summary>
            /// Copies the instances the engine has appended since the last call, keeping only the
            /// query's prefabs. <c>PlacementResult.Instances</c> is append-only during a run, so the
            /// running index is all the state this needs.
            /// </summary>
            private static void Harvest(IReadOnlyList<LocationInstanceResult> instances, ref int from,
                                        List<LocationHit> hits, HashSet<string> wanted)
            {
                for (int i = from; i < instances.Count; i++)
                {
                    LocationInstanceResult r = instances[i];
                    if (!wanted.Contains(r.PrefabName)) continue;
                    hits.Add(new LocationHit(r.PrefabName, r.X, r.Z, r.Location.Unique));
                }

                from = instances.Count;
            }
        }
    }
}
