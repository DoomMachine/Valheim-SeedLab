using System;
using System.Collections.Generic;
using System.Globalization;
using SeedLab.Contracts.Dump;

namespace SeedLab.Data
{
    /// <summary>Where a location's player-facing name came from. Provenance, not decoration.</summary>
    public enum DisplayNameSource
    {
        /// <summary>No name in the dump. <see cref="LocationDisplayName.DisplayName"/> is null and the
        /// prefab stands; every surface shows <c>WoodHouse6</c> because that IS what it is called.</summary>
        Prefab = 0,

        /// <summary>An <c>OfferingBowl</c> with <c>m_bossPrefab</c> whose <c>Character.m_boss</c> is
        /// true: the altar is named after the boss it summons. The one complete boss rule.</summary>
        BossAltar = 1,

        /// <summary>A <c>Trader</c> component whose <c>m_name</c> token resolves - Haldor, Hildir.</summary>
        Trader = 2,

        /// <summary>
        /// A trader whose own <c>m_name</c> is EMPTY in this build, named through the <c>$npc_</c>
        /// token convention instead. It is a separate value from <see cref="Trader"/> precisely so that
        /// "the dump joins these two" and "a human joined these two" never look alike in the data.
        /// Fires for exactly one prefab - see <see cref="LocationDisplayNames"/>' remarks.
        /// </summary>
        TraderNpcTokenConvention = 3,

        /// <summary><c>Location.m_discoverLabel</c>, resolved through the dumped localization table.
        /// 4 of 186 prefabs carry one, so this is a narrow rule, not a general naming source.</summary>
        DiscoverLabel = 4,
    }

    /// <summary>One location prefab's name, where it came from, and everything else it answers to.</summary>
    public sealed class LocationDisplayName
    {
        internal LocationDisplayName(string prefab, string? displayName, DisplayNameSource source,
                                     string provenance, string? nameToken, IReadOnlyList<string> aliases,
                                     int bossOrder)
        {
            Prefab = prefab;
            DisplayName = displayName;
            Source = source;
            Provenance = provenance;
            NameToken = nameToken;
            Aliases = aliases;
            BossOrder = bossOrder;
        }

        /// <summary><c>ZoneLocation.m_prefab.Name</c> - the identity, never translated, never folded.</summary>
        public string Prefab { get; }

        /// <summary>
        /// What a player calls this place, or <b>null</b> when the dump does not name it.
        ///
        /// <para>It is nullable on purpose. <c>Bonemass</c> is a real display name that happens to equal
        /// its prefab, so a non-null string that mirrors the prefab cannot be told apart from "unnamed"
        /// unless the absent case is genuinely absent. <c>"name": "WoodHouse6"</c> would be a lie
        /// dressed as data.</para>
        /// </summary>
        public string? DisplayName { get; }

        public DisplayNameSource Source { get; }

        /// <summary>One sentence naming the component, the token and the value the name came out of.
        /// It is printed on the web card and by <c>vseed locations</c>, so it is written for a reader,
        /// not for a log.</summary>
        public string Provenance { get; }

        /// <summary>The localization token behind <see cref="DisplayName"/>, e.g. <c>$enemy_gdking</c>,
        /// or null when the source is <see cref="DisplayNameSource.Prefab"/>. Carried as evidence: a
        /// surface that shows a name should be able to show what produced it.</summary>
        public string? NameToken { get; }

        /// <summary>
        /// Every spelling that means this prefab: the prefab itself first, then the display name, then
        /// the other names the game itself supplies for the same place (a resolved
        /// <c>m_discoverLabel</c>, a self-referencing runestone's map-pin caption). De-duplicated,
        /// ordinal.
        ///
        /// <para>These are for INPUT and for "also called" secondary text. An alias is never the
        /// caption: a name that is only nearly right fails to find something, whereas a caption that is
        /// only nearly right states a falsehood.</para>
        /// </summary>
        public IReadOnlyList<string> Aliases { get; }

        /// <summary>
        /// <c>Character.m_bossOrder</c> when <see cref="Source"/> is
        /// <see cref="DisplayNameSource.BossAltar"/>, else -1. <b>Provenance only, never a sort key</b>:
        /// <c>DN_Bossroom</c>'s is 0 and Eikthyr's is 1, so ordering by it puts Kall Fimbulbringer
        /// first - which is the game's progression order and not what "bosses A-Z" means.
        /// </summary>
        public int BossOrder { get; }

        /// <summary>True when a human, not the dump, made the join. See
        /// <see cref="DisplayNameSource.TraderNpcTokenConvention"/>.</summary>
        public bool IsDerivedByConvention => Source == DisplayNameSource.TraderNpcTokenConvention;

        public override string ToString()
            => DisplayName == null ? Prefab : DisplayName + " (" + Prefab + ")";
    }

    /// <summary>
    /// One thing the derivation wants said, and the place it is about.
    ///
    /// <para><b>Why the prefab is carried rather than parsed back out of the text</b> (2026-09-24).
    /// Most of these notes begin with the prefab and a colon, so a caller CAN recover it with string
    /// surgery - and a caller that does will get it wrong on the notes that mention a prefab in the
    /// middle of a sentence, and on any note a later edit rewords. A note is data about a place;
    /// carrying the place is cheaper than recovering it and cannot drift from the wording.</para>
    ///
    /// <para><see cref="Prefab"/> is null when the note is about the TABLE rather than about one
    /// place - an unreadable localization file, a rule that matched nothing, a boss count that no
    /// longer matches the build. Those always belong on screen; a per-prefab note only belongs there
    /// when its place is in front of the reader.</para>
    /// </summary>
    public sealed class LocationDisplayNote
    {
        /// <summary>
        /// Public, not internal: <c>LocationCatalog</c> in the CLI is a second, legitimate producer of
        /// notes about a prefab (a curated list that has drifted from the derived one), and it should
        /// carry its ownership the same way rather than keeping a parallel shape of its own.
        /// </summary>
        public LocationDisplayNote(string? prefab, string text)
        {
            Prefab = prefab;
            Text = text ?? "";
        }

        /// <summary>The location prefab this note is about, or null when it is about the table.</summary>
        public string? Prefab { get; }

        /// <summary>The sentence to print, character for character what
        /// <see cref="LocationDisplayNames.Notes"/> carries, so the two cannot disagree.</summary>
        public string Text { get; }

        public override string ToString() => Text;
    }

    /// <summary>
    /// Prefab to player-facing name, derived from the dump and from nothing else.
    ///
    /// <para><b>The problem.</b> The game's own identifier for the Elder's altar is <c>GDKing</c> and
    /// for Haldor's camp <c>Vendor_BlackForest</c>. A tool that prints those is showing a developer's
    /// string to a player who has never seen it. The obvious fix - ten hand-written names - is the one
    /// thing this project does not do: a curated name drifts silently at the next update and covers ten
    /// prefabs where the game has 6,258 strings.</para>
    ///
    /// <para><b>The rules, first match wins.</b></para>
    /// <list type="number">
    /// <item><b>BossAltar</b> - an <c>OfferingBowl</c> whose <c>m_bossPrefab</c> is a
    /// <c>Character</c> with <c>m_boss</c> true. Its <c>m_name</c> resolved is the name: "The Elder",
    /// "Moder", "Yagluth". Eight altars, <c>m_bossOrder</c> contiguous 0-7.</item>
    /// <item><b>Trader</b> - a <c>Trader</c> component whose <c>m_name</c> resolves. Haldor, Hildir.</item>
    /// <item><b>DiscoverLabel</b> - <c>Location.m_discoverLabel</c> resolved. Forge of Potential,
    /// Sealed Tower, Charred Fortress. An unresolved token is NOT a name and emits a Note.</item>
    /// <item><b>Prefab</b> - no name. <see cref="LocationDisplayName.DisplayName"/> is null.</item>
    /// </list>
    ///
    /// <para><b>Rules that were tried and rejected, so they are not re-litigated.</b> A
    /// <c>RuneStone</c>'s <c>m_name</c> / <c>m_label</c> name the OBJECT, not the place: the 27 stones
    /// in this dump carry only "Runestone" (21), "Sacrificial Stone" (5) and "A mysterious text" (1),
    /// so that rule would caption <c>StartTemple</c> "Sacrificial Stone" and the Mistlands boss
    /// entrance "A mysterious text". A stone's <c>m_locationName</c> / <c>m_pinName</c> name the
    /// location the stone makes the game DISCOVER, which is routinely a different location - the five
    /// <c>BossStone_*</c> inside <c>StartTemple</c> are exactly that. The pin survives as an ALIAS
    /// where the stone names its own host, and nowhere else.</para>
    ///
    /// <para><b>The one human join, and why it is allowed.</b> <c>BogWitch_Camp</c>'s <c>Trader</c> has
    /// an empty <c>m_name</c> in this build, so rule 2 finds nothing; the string
    /// <c>$npc_bogwitch</c> = "The Bog Witch" is in the same dump but nothing in the dump connects the
    /// two. The user confirmed from the game that this trader IS the Bog Witch, so the name is
    /// displayed - and carries <see cref="DisplayNameSource.TraderNpcTokenConvention"/> plus an
    /// <c>Unverified:</c> note so the provenance stays visible. It is pinned to that one prefab and
    /// that one token: a general "<c>$npc_</c> + lowercased prefab" rule would silently name a future
    /// trader nobody has confirmed, which is the guessing this class exists to avoid.</para>
    ///
    /// <para><b>Cost, and the one join that is recorded rather than read.</b> Building this table reads
    /// <c>localization.json</c> (0.4 MB, 6-7 ms) and the occupant slice of <c>locationchildren.json</c>
    /// (28.5 MB, 172-182 ms), once per process. It deliberately does NOT read
    /// <c>roomchildren.json</c>: that file is 71.0 MB and costs 290-390 ms cold, measured over three
    /// fresh processes on 2026-09-24, which is over the 250 ms line the plan set for the naming layer
    /// and would be paid by every <c>vseed locations</c> run. The Mistlands queen's bowl is the only
    /// thing in it that a name needs, so the ROOM half of that join is recorded as a constant here
    /// (<see cref="RoomBossBridge"/>) and verified against the real file by
    /// <see cref="BuildWithRoomJoin"/>, which the test suite runs and the CLI never does. The
    /// LOCATION half - which prefab owns room theme 256 - is still derived at run time from
    /// <c>locationprefabs.json</c>, and the NAME is still <see cref="GameData.Localize"/>d from the
    /// dumped table. What is curated is a join, never a string.</para>
    /// </summary>
    public sealed class LocationDisplayNames
    {
        /// <summary>
        /// The room side of the Mistlands boss join, recorded so the 71 MB room file stays off the
        /// runtime naming path. Every field here was read out of
        /// <c>data\1.0.15-59f53fb5\roomchildren.json</c> on 2026-09-24 and is re-checked against it by
        /// <see cref="BuildWithRoomJoin"/>: of all 358 rooms exactly ONE carries an
        /// <c>offeringBowls</c> entry, and its <c>roomTheme</c> and <c>roomDataTheme</c> are both 256.
        /// </summary>
        private sealed class RoomBossBridge
        {
            public RoomBossBridge(int theme, string roomPrefab, string roomListName, string bowlPrefab,
                                  string bossPrefab, string bossNameToken, int bossOrder)
            {
                Theme = theme;
                RoomPrefab = roomPrefab;
                RoomListName = roomListName;
                BowlPrefab = bowlPrefab;
                BossPrefab = bossPrefab;
                BossNameToken = bossNameToken;
                BossOrder = bossOrder;
            }

            /// <summary><c>Room.m_theme</c>, the raw bitmask. The location side of the join is
            /// <c>DungeonGeneratorDef.themes</c>, which is what <c>SetupAvailableRooms</c> tests.</summary>
            public int Theme { get; }

            public string RoomPrefab { get; }
            public string RoomListName { get; }
            public string BowlPrefab { get; }
            public string BossPrefab { get; }

            /// <summary>The token the NAME still comes from. Nothing here is a player-facing string.</summary>
            public string BossNameToken { get; }

            public int BossOrder { get; }
        }

        /// <summary>
        /// One entry: theme 256 -> the Queen. It is a list rather than a field because a future dump
        /// that puts a second boss in a room must extend a table, not edit a special case.
        /// </summary>
        private static readonly RoomBossBridge[] RoomBossBridges =
        {
            new RoomBossBridge(256, "dvergr_new_bossroom_ENTRANCE02", "_RoomList_Mistlands",
                               "offeraltar_queen", "SeekerQueen", "$enemy_seekerqueen", 6),
        };

        /// <summary>
        /// The one prefab whose trader carries no name token, and the token the user confirmed belongs
        /// to it. Pinned, so this cannot grow into a convention - see the class remarks.
        /// </summary>
        private const string BogWitchCampPrefab = "BogWitch_Camp";

        private const string BogWitchNameToken = "$npc_bogwitch";

        /// <summary>How many boss altars and traders this build is known to have, so a rule that
        /// quietly stops matching is visible. Cross-checked against the curated arrays in
        /// <c>SeedLab.Cli\Analysis\LocationCatalog.cs</c> by the test suite - this assembly cannot see
        /// that one, and inverting the dependency to reach a literal would be worse than a count.</summary>
        private const int ExpectedBossAltars = 8;

        private const int ExpectedTraders = 3;

        private readonly Dictionary<string, LocationDisplayName> _byPrefab;
        private readonly List<LocationDisplayName> _all;
        private readonly List<LocationDisplayNote> _notes;
        private readonly List<string> _bossPrefabs;
        private readonly List<string> _traderPrefabs;

        /// <summary>
        /// Collects the notes as they are made. <see cref="Add"/> is for a note about the TABLE and
        /// <see cref="AddFor"/> for a note about one place; the plain <c>Add</c> is the table-wide one
        /// on purpose, so a site that says nothing about ownership keeps the meaning that is always
        /// safe to print.
        /// </summary>
        private sealed class NoteList
        {
            public readonly List<LocationDisplayNote> Items = new List<LocationDisplayNote>();

            public void Add(string text) => Items.Add(new LocationDisplayNote(null, text));

            public void AddFor(string prefab, string text) => Items.Add(new LocationDisplayNote(prefab, text));
        }

        private LocationDisplayNames(string language, List<LocationDisplayName> all,
                                     List<LocationDisplayNote> notes,
                                     List<string> bossPrefabs, List<string> traderPrefabs)
        {
            Language = language;
            _all = all;
            _notes = notes;
            _bossPrefabs = bossPrefabs;
            _traderPrefabs = traderPrefabs;
            _byPrefab = new Dictionary<string, LocationDisplayName>(all.Count, StringComparer.Ordinal);
            foreach (LocationDisplayName n in all) _byPrefab[n.Prefab] = n;
        }

        /// <summary>
        /// <c>LocalizationFile.language</c> - "English" for this dump. A dump taken with the game in
        /// Swedish names everything in Swedish, and every consumer has to be able to SAY which it is
        /// showing rather than assume. Empty when the dump did not record it.
        /// </summary>
        public string Language { get; }

        /// <summary>
        /// Everything the caller should print: a rule that matched nothing, a token with no entry, a
        /// bowl that claims a boss it cannot name, a count that no longer matches this build. Modelled
        /// on <c>LocationCatalog.Mark</c> - a rule that stops matching is reported, never silently
        /// absorbed - and flows into <c>vseed locations</c>' existing notes loop.
        /// </summary>
        public IReadOnlyList<string> Notes
        {
            get
            {
                List<string> flat = new List<string>(_notes.Count);
                foreach (LocationDisplayNote note in _notes) flat.Add(note.Text);
                return flat;
            }
        }

        /// <summary>The same notes with the place each one is about, for a caller that shows one
        /// selection at a time. See <see cref="LocationDisplayNote"/> for why the prefab is carried
        /// rather than parsed back out of the sentence.</summary>
        public IReadOnlyList<LocationDisplayNote> NoteDetails => _notes;

        /// <summary>
        /// Every table-wide note, plus the per-prefab notes for the places in <paramref name="prefabs"/>.
        ///
        /// <para>This exists because the alternative - printing all of them every time - is how an
        /// honesty channel stops being read. The Bog Witch's <c>Unverified:</c> note shares
        /// <c>Out.Warn</c> with the <c>m_unique</c> caveats and the seed-ambiguity note, and repeating
        /// it on a run that never mentions that trader teaches the reader to skip the one channel where
        /// SeedLab says what it cannot know. Nothing is suppressed: a note about a place is shown
        /// whenever that place is.</para>
        ///
        /// <para>A null or empty selection means no place is in front of the reader, so only the
        /// table-wide notes come back. Passing every prefab gives exactly <see cref="Notes"/>.</para>
        ///
        /// <para><paramref name="includeTableWide"/> exists so a caller can print the table-wide notes
        /// EARLY - before it knows the selection, and therefore before any refusal - and the per-prefab
        /// ones later without repeating them. "the localization table could not be read" has to reach a
        /// user whose <c>--name</c> then fails to resolve, or the failure looks arbitrary.</para>
        ///
        /// </summary>
        public IReadOnlyList<string> NotesFor(IEnumerable<string>? prefabs, bool includeTableWide = true)
        {
            HashSet<string> want = new HashSet<string>(StringComparer.Ordinal);
            if (prefabs != null)
            {
                foreach (string p in prefabs) want.Add(p);
            }

            List<string> outp = new List<string>();
            foreach (LocationDisplayNote note in _notes)
            {
                bool wanted = note.Prefab == null ? includeTableWide : want.Contains(note.Prefab);
                if (wanted) outp.Add(note.Text);
            }
            return outp;
        }

        /// <summary>Every prefab that got a name, plus every prefab the occupant file knows, in the
        /// occupant file's order.</summary>
        public IReadOnlyList<LocationDisplayName> All => _all;

        /// <summary>The prefabs rule 1 named, in <c>m_bossOrder</c> order. 8 in this build.</summary>
        public IReadOnlyList<string> BossAltarPrefabs => _bossPrefabs;

        /// <summary>The prefabs rule 2 (or the one pinned convention) named. 3 in this build.</summary>
        public IReadOnlyList<string> TraderPrefabs => _traderPrefabs;

        /// <summary>
        /// Never null: a prefab this table has never heard of comes back as an unnamed
        /// <see cref="DisplayNameSource.Prefab"/> row, because "I do not name this" is the same answer
        /// whether the reason is a missing entry or an entry with nothing in it.
        /// </summary>
        public LocationDisplayName For(string prefabName)
        {
            if (prefabName == null) throw new ArgumentNullException(nameof(prefabName));
            return _byPrefab.TryGetValue(prefabName, out LocationDisplayName? n)
                ? n
                : Unnamed(prefabName, "this prefab is not in the dumped occupant table, so nothing in "
                                      + "the dump names it.");
        }

        /// <summary>
        /// Builds the table. Reads <c>localization.json</c> and the occupant slice of
        /// <c>locationchildren.json</c>; does NOT read <c>roomchildren.json</c> (see the class remarks
        /// for the measurement that decided that).
        /// </summary>
        public static LocationDisplayNames Build(GameData data) => Build(data, useRoomJoin: false);

        /// <summary>
        /// The same table, with the Mistlands boss derived from <c>roomchildren.json</c> for real
        /// instead of from <see cref="RoomBossBridges"/>. <b>For tests and for a future dump's
        /// re-verification only</b> - it costs 290-390 ms of cold file load and buys exactly one row,
        /// which is the whole reason the constant exists. A test that runs both and compares them row
        /// for row is what keeps the constant honest.
        /// </summary>
        public static LocationDisplayNames BuildWithRoomJoin(GameData data) => Build(data, useRoomJoin: true);

        private static LocationDisplayNames Build(GameData data, bool useRoomJoin)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));

            NoteList notes = new NoteList();
            LocalizationFile loc = data.Localization;

            // An empty translation table is not a failure of this class, but every name in it would
            // then be null and every surface would show prefabs with no explanation. Say so once.
            if (loc.skipped != null)
            {
                notes.Add("the dumped localization table could not be read (" + loc.skipped
                          + "), so no location can be named in this dump and every place shows its "
                          + "prefab name.");
            }

            Dictionary<string, string> discoverLabelToken =
                new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (LocationPrefabDef p in data.LocationPrefabs)
            {
                if (p.prefabName == null || string.IsNullOrEmpty(p.discoverLabel)) continue;
                discoverLabelToken[p.prefabName] = p.discoverLabel!;
            }

            // The LOCATION half of the room join, derived from hashed data every run: which location
            // prefab can generate a room of this theme. SetupAvailableRooms tests
            // DungeonGenerator.m_themes against RoomData.m_theme, so this is the game's own link.
            Dictionary<int, List<string>> byRoomTheme = new Dictionary<int, List<string>>();
            foreach (RoomBossBridge b in RoomBossBridges)
            {
                List<string> hosts = new List<string>();
                foreach (LocationPrefabDef p in data.LocationPrefabs)
                {
                    if (p.prefabName == null || p.generators == null) continue;
                    foreach (DungeonGeneratorDef g in p.generators)
                    {
                        if ((g.themes & b.Theme) == 0) continue;
                        if (!hosts.Contains(p.prefabName)) hosts.Add(p.prefabName);
                    }
                }

                byRoomTheme[b.Theme] = hosts;
            }

            Dictionary<string, RoomBoss> roomBosses = useRoomJoin
                ? DeriveRoomBossesFromDump(data, byRoomTheme, notes)
                : DeriveRoomBossesFromBridges(data, byRoomTheme, notes);

            List<LocationDisplayName> all = new List<LocationDisplayName>();
            List<string> bossPrefabs = new List<string>();
            List<string> traderPrefabs = new List<string>();
            List<int> bossOrders = new List<int>();
            int discoverLabelNames = 0;

            foreach (LocationOccupantsDef d in data.LocationOccupants)
            {
                string prefab = d.prefabName!;   // GameData.OccupantFile has already required it

                List<string> aliases = new List<string> { prefab };

                // Aliases the game supplies for this place whatever rule ends up naming it: the
                // discover label, and a runestone that names its OWN host (never another location).
                string? labelToken = discoverLabelToken.TryGetValue(prefab, out string? lt) ? lt : null;
                string? labelText = labelToken == null ? null : data.Localize(labelToken);
                if (labelToken != null && labelText == null)
                {
                    notes.AddFor(prefab, prefab + ": Location.m_discoverLabel '" + labelToken + "' has no entry in "
                              + "the dumped localization table, so it is not a name; the prefab stands.");
                }

                AddAlias(aliases, labelText);
                AddSelfReferencingPinNames(d, prefab, aliases);

                // ---- rule 1: the offering bowl, over locations UNION the theme-joined room ---------
                OfferingBowlDef? best = null;
                int qualifying = 0;
                int tiedWithBest = 0;
                if (d.offeringBowls != null)
                {
                    foreach (OfferingBowlDef b in d.offeringBowls)
                    {
                        if (!b.bossFlag) continue;
                        if (string.IsNullOrEmpty(b.bossLocalizedName))
                        {
                            notes.AddFor(prefab, prefab + ": OfferingBowl '" + (b.prefabName ?? "?")
                                      + "' has bossFlag but no localized boss name (m_bossPrefab '"
                                      + (b.bossPrefabName ?? "?") + "', token '"
                                      + (b.bossNameToken ?? "") + "'); the prefab stands.");
                            continue;
                        }

                        qualifying++;
                        if (best == null || b.bossOrder < best.bossOrder) { best = b; tiedWithBest = 1; }
                        else if (b.bossOrder == best.bossOrder) tiedWithBest++;
                    }
                }

                if (qualifying > 1)
                {
                    // Say which rule actually decided it. The loop above keeps the first bowl in FILE
                    // order and only replaces it on a strictly lower m_bossOrder, so on a tie the
                    // order did not decide anything and a note claiming it did would send the next
                    // reader looking for a difference that is not there. Latent in 1.0.15 - no
                    // location has two qualifying bowls - but a note is only worth having if it is
                    // true of the case that fires it.
                    notes.AddFor(prefab, prefab + ": " + qualifying.ToString(CultureInfo.InvariantCulture)
                              + " OfferingBowls qualify as boss altars; took "
                              + (tiedWithBest > 1
                                 ? "the first in file order, because "
                                   + tiedWithBest.ToString(CultureInfo.InvariantCulture)
                                   + " of them share m_bossOrder "
                                   + best!.bossOrder.ToString(CultureInfo.InvariantCulture) + "."
                                 : "the lower m_bossOrder ("
                                   + best!.bossOrder.ToString(CultureInfo.InvariantCulture) + ")."));
                }

                if (best != null)
                {
                    AddDisplayAlias(aliases, best.bossLocalizedName);
                    all.Add(new LocationDisplayName(
                        prefab, best.bossLocalizedName, DisplayNameSource.BossAltar,
                        "named after the boss its OfferingBowl '" + (best.prefabName ?? "?")
                        + "' summons: " + (best.bossPrefabName ?? "?") + ", Character.m_name "
                        + (best.bossNameToken ?? "") + ".",
                        best.bossNameToken, Freeze(aliases), best.bossOrder));
                    bossPrefabs.Add(prefab);
                    bossOrders.Add(best.bossOrder);
                    continue;
                }

                if (roomBosses.TryGetValue(prefab, out RoomBoss room))
                {
                    AddDisplayAlias(aliases, room.Name);
                    all.Add(new LocationDisplayName(
                        prefab, room.Name, DisplayNameSource.BossAltar, room.Provenance,
                        room.NameToken, Freeze(aliases), room.BossOrder));
                    bossPrefabs.Add(prefab);
                    bossOrders.Add(room.BossOrder);
                    continue;
                }

                // ---- rule 2: the trader ----------------------------------------------------------
                TraderDef? trader = null;
                if (d.traders != null)
                {
                    foreach (TraderDef t in d.traders)
                    {
                        if (trader == null) trader = t;
                        if (!string.IsNullOrEmpty(t.localizedName)) { trader = t; break; }
                    }
                }

                if (trader != null && !string.IsNullOrEmpty(trader.localizedName))
                {
                    AddDisplayAlias(aliases, trader.localizedName);
                    all.Add(new LocationDisplayName(
                        prefab, trader.localizedName, DisplayNameSource.Trader,
                        "named after the Trader standing in it: " + (trader.prefabName ?? "?")
                        + ", Trader.m_name " + (trader.nameToken ?? "") + ".",
                        trader.nameToken, Freeze(aliases), -1));
                    traderPrefabs.Add(prefab);
                    continue;
                }

                if (trader != null && string.Equals(prefab, BogWitchCampPrefab, StringComparison.Ordinal))
                {
                    string? bogWitch = data.Localize(BogWitchNameToken);
                    if (bogWitch == null)
                    {
                        notes.AddFor(prefab, prefab + ": the token " + BogWitchNameToken + " has no entry in this "
                                  + "dump's localization table, so the confirmed Bog Witch join cannot "
                                  + "be resolved; the prefab stands.");
                    }
                    else
                    {
                        notes.AddFor(prefab, "Unverified: " + prefab + " is captioned '" + bogWitch + "' from "
                                  + BogWitchNameToken + " because its Trader.m_name is EMPTY in this "
                                  + "build - nothing in the dump joins the two. The user confirmed the "
                                  + "trader from the game; a later build could change it.");
                        AddDisplayAlias(aliases, bogWitch);
                        all.Add(new LocationDisplayName(
                            prefab, bogWitch, DisplayNameSource.TraderNpcTokenConvention,
                            "its Trader (" + (trader.prefabName ?? "?") + ") has an EMPTY m_name in "
                            + "this build; the name is the game's own string for " + BogWitchNameToken
                            + ", joined by hand and confirmed in game, not by the dump.",
                            BogWitchNameToken, Freeze(aliases), -1));
                        traderPrefabs.Add(prefab);
                        continue;
                    }
                }
                else if (trader != null)
                {
                    notes.AddFor(prefab, prefab + ": its Trader (" + (trader.prefabName ?? "?") + ") has no name "
                              + "token in this build and is not the one join that was confirmed in "
                              + "game, so the prefab stands rather than a guessed name.");
                }

                // ---- rule 3: the discover label ---------------------------------------------------
                if (labelText != null)
                {
                    all.Add(new LocationDisplayName(
                        prefab, labelText, DisplayNameSource.DiscoverLabel,
                        "named by Location.m_discoverLabel " + labelToken
                        + ", the caption the game writes when the place is discovered.",
                        labelToken, Freeze(aliases), -1));
                    discoverLabelNames++;
                    continue;
                }

                // ---- rule 4: nothing names it -----------------------------------------------------
                all.Add(new LocationDisplayName(
                    prefab, null, DisplayNameSource.Prefab,
                    "nothing in this dump names it: no OfferingBowl with a boss, no Trader, no "
                    + "Location.m_discoverLabel. The prefab name is what it is called.",
                    null, Freeze(aliases), -1));
            }

            NoteEmptyRules(notes, bossPrefabs.Count, traderPrefabs.Count, discoverLabelNames);
            NoteBossOrders(notes, bossOrders);
            NoteCounts(notes, bossPrefabs.Count, traderPrefabs.Count);

            return new LocationDisplayNames(loc.language ?? "", all, notes.Items, bossPrefabs, traderPrefabs);
        }

        /// <summary>A boss whose bowl is in a room prefab, not in the location prefab.</summary>
        private readonly struct RoomBoss
        {
            public RoomBoss(string name, string nameToken, int bossOrder, string provenance)
            {
                Name = name;
                NameToken = nameToken;
                BossOrder = bossOrder;
                Provenance = provenance;
            }

            public string Name { get; }
            public string NameToken { get; }
            public int BossOrder { get; }
            public string Provenance { get; }
        }

        /// <summary>
        /// The runtime path: the location half of the join is read from <c>locationprefabs.json</c>, the
        /// name from the dumped localization table, and only "which boss is in the theme-256 room" is
        /// the recorded constant. A theme that no longer resolves to exactly one host is a Note and no
        /// name, never a guess.
        /// </summary>
        private static Dictionary<string, RoomBoss> DeriveRoomBossesFromBridges(
            GameData data, Dictionary<int, List<string>> byRoomTheme, NoteList notes)
        {
            Dictionary<string, RoomBoss> outp = new Dictionary<string, RoomBoss>(StringComparer.Ordinal);
            foreach (RoomBossBridge b in RoomBossBridges)
            {
                List<string> hosts = byRoomTheme[b.Theme];
                if (hosts.Count != 1)
                {
                    notes.Add("room theme " + b.Theme.ToString(CultureInfo.InvariantCulture)
                              + " is claimed by " + hosts.Count.ToString(CultureInfo.InvariantCulture)
                              + " location prefabs in this dump, not by exactly one, so the recorded "
                              + "room join for '" + b.RoomPrefab + "' is ambiguous and was not applied. "
                              + "Re-verify it against roomchildren.json.");
                    continue;
                }

                string? name = data.Localize(b.BossNameToken);
                if (name == null)
                {
                    notes.AddFor(hosts[0], "the token " + b.BossNameToken + " has no entry in this dump's "
                              + "localization table, so the room-derived boss of '" + hosts[0]
                              + "' cannot be named; the prefab stands.");
                    continue;
                }

                outp[hosts[0]] = new RoomBoss(
                    name, b.BossNameToken, b.BossOrder,
                    "its interior is GENERATED, so its altar is in a room and not in the location "
                    + "prefab: room " + b.RoomPrefab + " (" + b.RoomListName + ", Room.m_theme "
                    + b.Theme.ToString(CultureInfo.InvariantCulture) + ") carries OfferingBowl "
                    + b.BowlPrefab + ", which summons " + b.BossPrefab + " (" + b.BossNameToken
                    + "). The room half of that join is a value recorded from roomchildren.json and "
                    + "re-checked against it by the test suite, because reading the 71 MB file would "
                    + "cost every command 0.3 s for this one row.");
            }

            return outp;
        }

        /// <summary>
        /// The verification path: the same answer taken from <c>roomchildren.json</c> itself. It walks
        /// all 358 rooms and accepts a room only when the room and the bowl both say boss, so a
        /// disagreement with <see cref="RoomBossBridges"/> shows up as a different table rather than as
        /// a silently equal one.
        /// </summary>
        private static Dictionary<string, RoomBoss> DeriveRoomBossesFromDump(
            GameData data, Dictionary<int, List<string>> byRoomTheme, NoteList notes)
        {
            Dictionary<string, RoomBoss> outp = new Dictionary<string, RoomBoss>(StringComparer.Ordinal);

            foreach (RoomOccupantsDef r in data.RoomOccupants)
            {
                if (r.offeringBowls == null) continue;
                foreach (OfferingBowlDef b in r.offeringBowls)
                {
                    if (!b.bossFlag || string.IsNullOrEmpty(b.bossLocalizedName)) continue;

                    List<string> hosts = new List<string>();
                    foreach (LocationPrefabDef p in data.LocationPrefabs)
                    {
                        if (p.prefabName == null || p.generators == null) continue;
                        foreach (DungeonGeneratorDef g in p.generators)
                        {
                            if ((g.themes & r.roomTheme) == 0) continue;
                            if (!hosts.Contains(p.prefabName)) hosts.Add(p.prefabName);
                        }
                    }

                    if (hosts.Count != 1)
                    {
                        notes.Add("room '" + (r.prefabName ?? "?") + "' carries a boss OfferingBowl but "
                                  + "its Room.m_theme " + r.roomTheme.ToString(CultureInfo.InvariantCulture)
                                  + " is claimed by " + hosts.Count.ToString(CultureInfo.InvariantCulture)
                                  + " location prefabs, so it cannot be attributed to one place.");
                        continue;
                    }

                    outp[hosts[0]] = new RoomBoss(
                        b.bossLocalizedName!, b.bossNameToken ?? "", b.bossOrder,
                        "its interior is GENERATED, so its altar is in a room and not in the location "
                        + "prefab: room " + (r.prefabName ?? "?") + " (" + (r.roomListName ?? "?")
                        + ", Room.m_theme " + r.roomTheme.ToString(CultureInfo.InvariantCulture)
                        + ") carries OfferingBowl " + (b.prefabName ?? "?") + ", which summons "
                        + (b.bossPrefabName ?? "?") + " (" + (b.bossNameToken ?? "") + ").");
                }
            }

            // byRoomTheme is computed for the bridges either way; reading it here keeps the two paths
            // honest about looking at the same location side.
            foreach (RoomBossBridge bridge in RoomBossBridges)
            {
                if (byRoomTheme[bridge.Theme].Count == 1) continue;
                notes.Add("room theme " + bridge.Theme.ToString(CultureInfo.InvariantCulture)
                          + " no longer resolves to exactly one location prefab.");
            }

            return outp;
        }

        /// <summary>
        /// A runestone's map-pin caption, but only where the stone names its OWN host. A stone names
        /// the location it makes the game DISCOVER, so <c>StartTemple</c>'s five <c>BossStone_*</c>
        /// name five other altars and must never leak into this prefab's aliases.
        /// <c>m_locationName</c> is a raw prefab name, not a token, which is why it is compared
        /// ordinally against <paramref name="prefab"/>.
        /// </summary>
        private static void AddSelfReferencingPinNames(LocationOccupantsDef d, string prefab,
                                                       List<string> aliases)
        {
            if (d.runeStones == null) return;
            foreach (RuneStoneDef s in d.runeStones)
            {
                if (s.locationNameToken == null) continue;
                if (!string.Equals(s.locationNameToken, prefab, StringComparison.Ordinal)) continue;
                AddAlias(aliases, s.pinNameLocalized);
            }
        }

        private static void AddAlias(List<string> aliases, string? value)
        {
            if (string.IsNullOrEmpty(value)) return;
            foreach (string a in aliases)
            {
                if (string.Equals(a, value, StringComparison.Ordinal)) return;
            }

            aliases.Add(value!);
        }

        /// <summary>
        /// The chosen display name goes in right after the prefab, so the list reads prefab, name,
        /// then the other names the game supplies for the same place. A surface that prints "also
        /// called" skips the first two; one that builds an input index takes all of them.
        /// </summary>
        private static void AddDisplayAlias(List<string> aliases, string? value)
        {
            if (string.IsNullOrEmpty(value)) return;
            for (int i = 0; i < aliases.Count; i++)
            {
                if (!string.Equals(aliases[i], value, StringComparison.Ordinal)) continue;
                if (i > 1)
                {
                    aliases.RemoveAt(i);
                    break;
                }

                return;
            }

            aliases.Insert(Math.Min(1, aliases.Count), value!);
        }

        private static IReadOnlyList<string> Freeze(List<string> aliases) => aliases.ToArray();

        private static LocationDisplayName Unnamed(string prefab, string why)
            => new LocationDisplayName(prefab, null, DisplayNameSource.Prefab, why, null,
                                       new[] { prefab }, -1);

        /// <summary>A rule that matched nothing at all is the failure mode that looks like success:
        /// every place keeps a name of some sort, so nobody notices the boss rule died.</summary>
        private static void NoteEmptyRules(NoteList notes, int bosses, int traders, int labels)
        {
            if (bosses == 0) notes.Add("no location prefab produced a boss-altar name in this dump.");
            if (traders == 0) notes.Add("no location prefab produced a trader name in this dump.");
            if (labels == 0)
            {
                notes.Add("no location prefab produced a Location.m_discoverLabel name in this dump.");
            }
        }

        /// <summary><c>m_bossOrder</c> is the game's own progression order and it is contiguous 0-7 in
        /// this build. A gap means the derivation lost a boss, which no other check would catch.</summary>
        private static void NoteBossOrders(NoteList notes, List<int> orders)
        {
            if (orders.Count == 0) return;
            List<int> sorted = new List<int>(orders);
            sorted.Sort();
            for (int i = 0; i < sorted.Count; i++)
            {
                if (sorted[i] == i) continue;
                notes.Add("the derived boss altars' m_bossOrder values are not contiguous from 0 ("
                          + string.Join(", ", sorted) + "), so a boss is probably missing or "
                          + "duplicated.");
                return;
            }
        }

        private static void NoteCounts(NoteList notes, int bosses, int traders)
        {
            if (bosses != ExpectedBossAltars)
            {
                notes.Add("this build derives " + bosses.ToString(CultureInfo.InvariantCulture)
                          + " boss altars where " + ExpectedBossAltars.ToString(CultureInfo.InvariantCulture)
                          + " were verified on 2026-09-24; re-check the derivation against the dump "
                          + "before trusting a boss list.");
            }

            if (traders != ExpectedTraders)
            {
                notes.Add("this build derives " + traders.ToString(CultureInfo.InvariantCulture)
                          + " traders where " + ExpectedTraders.ToString(CultureInfo.InvariantCulture)
                          + " were verified on 2026-09-24; re-check the derivation against the dump "
                          + "before trusting a trader list.");
            }
        }
    }
}
