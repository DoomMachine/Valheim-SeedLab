using System;
using System.Collections.Generic;
using System.Threading;

namespace SeedLab.Web.Api
{
    /// <summary>Which slice of the placement list to run.</summary>
    public enum LocationSet
    {
        /// <summary>
        /// Boss altars and trader camps only - the shortest prefix that answers "where do I go first".
        /// Measured under a second on this machine, because the curated names sit near the front of the
        /// ordered list: 0.38 s end to end for seed 1 on 2026-09-24, of which 0.33 s is the world and
        /// 0.05 s the placement.
        ///
        /// <para>Naming the rows costs this path nothing per request. The derived name table is built
        /// ONCE per process (measured 2026-09-24: ~177 ms cold - 4 ms localization.json, ~172 ms the
        /// occupant slice of locationchildren.json, 3 ms the derivation; 0 ms every touch after), and
        /// <c>vseed serve</c> has already paid it at startup because the search panel's oracle builds
        /// the same table. So a Core request is the same placement run it was before.</para>
        /// </summary>
        Core = 0,

        /// <summary>Every location type. About seven seconds a seed, most of it the 2048² biome grid.</summary>
        All = 1,
    }

    public sealed class LocationsRequest
    {
        public int Seed { get; set; }

        public int WorldGenVersion { get; set; } = 2;

        public LocationSet Set { get; set; }

        /// <summary>Worker threads for the biome grid. 0 means every core.</summary>
        public int Threads { get; set; }

        /// <summary>
        /// Called as the run moves through its phases, so the page can show what it is waiting for
        /// instead of a spinner: <c>("world", 0.0)</c>, <c>("placement", 0.6)</c>, ...
        /// </summary>
        public Action<string, double>? OnPhase { get; set; }
    }

    /// <summary>
    /// Where the places are in one seed.
    ///
    /// <para>The web project deliberately does not know what a boss is. "Boss altar" and "trader camp"
    /// are curated lists, not anything the dumped <c>ZoneLocation</c> table marks, and the placement
    /// engine needs the dumped asset table and its version gate - so all of that stays in the CLI and
    /// arrives here through <see cref="LocationsProvider"/>, exactly as the seed panel's figures do.
    /// The server's job is to cache it, stream a progress state while it is being computed, and hand
    /// it to the map.</para>
    ///
    /// <para><b>The same is now true of NAMES.</b> "The Elder" is derived from the dumped offering
    /// bowls and the dumped localization table, neither of which this project can read, so the name,
    /// where it came from, its aliases and the group it is listed under all arrive already decided
    /// on <see cref="LocationTypeRow"/>. The page renders what it is given and never re-derives a
    /// name or a sort order of its own - that is the whole point of shipping
    /// <see cref="LocationTypeRow.SortIndex"/> as an integer.</para>
    /// </summary>
    public sealed class LocationsReport
    {
        public int Seed { get; set; }

        public int WorldGenVersion { get; set; }

        /// <summary>core | all</summary>
        public string Set { get; set; } = "";

        public int TypesRun { get; set; }

        public int TypesTotal { get; set; }

        public double SecondsWorld { get; set; }

        public double SecondsPlacement { get; set; }

        public string GameVersion { get; set; } = "";

        /// <summary>The DATA-STAMP the location table was dumped under. A mismatch fails the request closed.</summary>
        public string DataStamp { get; set; } = "";

        public string DataDumped { get; set; } = "";

        /// <summary>
        /// Which language the display names are in - <c>LocalizationFile.language</c>, "English" for
        /// this dump. Null when this build could not read the naming files at all, which is also the
        /// state in which every row's <see cref="LocationTypeRow.DisplayName"/> is null.
        ///
        /// <para>It is shown, not just recorded: a player reading "The Elder" is entitled to know
        /// that the name came from the game's own 1.0.15 English table and not from this tool.</para>
        /// </summary>
        public string? NamesLanguage { get; set; }

        public List<LocationTypeRow> Types { get; set; } = new List<LocationTypeRow>();

        public List<LocationInstanceRow> Instances { get; set; } = new List<LocationInstanceRow>();

        /// <summary>What is NOT a function of the seed. Shown next to the markers, never omitted.</summary>
        public List<string> NotPredictable { get; set; } = new List<string>();

        /// <summary>
        /// Anything the catalogue wants said out loud - a curated name the table no longer has, and
        /// now also anything the NAME derivation wants said: a rule that matched nothing, a
        /// <c>$</c> token with no entry, the Bog Witch's hand-made join. The last one is permanent,
        /// so this list is expected to be non-empty; it is not an error channel.
        /// </summary>
        public List<string> Notes { get; set; } = new List<string>();
    }

    public sealed class LocationTypeRow
    {
        public string Prefab { get; set; } = "";

        /// <summary>
        /// The prefab's own <c>Location.m_discoverLabel</c>, when it has one - NOT
        /// <c>m_group</c>, which this comment claimed until 2026-09-24. It is filled from
        /// <c>locationprefabs.json</c> via <c>LocationCatalog.LabelOf</c>.
        ///
        /// <para><b>It is almost always null, and it is not the display name.</b> Only 4 of 186
        /// prefabs carry one and all four are unresolved <c>$</c> tokens. Until 2026-09-24 the page
        /// fell back to the prefab for all 183 placed types, because <c>placeName()</c> rejected any
        /// label starting with <c>$</c>; that guard is gone and the page now reads
        /// <see cref="DisplayName"/>, which resolves those tokens properly and names 14 of 183. This
        /// field is kept as the raw evidence behind three of those names - read it to see WHERE a
        /// name came from, never as the name itself.</para>
        /// </summary>
        public string? Label { get; set; }

        /// <summary>
        /// What a player calls this place - "The Elder" for <c>GDKing</c>, "Haldor" for
        /// <c>Vendor_BlackForest</c>.
        ///
        /// <para><b>NULL when the dump does not name it, and never the prefab spelled out as if it
        /// were a name.</b> Only null tells "this place has no player-facing name" apart from "this
        /// place is named the same string as its prefab", and the second really happens:
        /// <c>Bonemass</c>'s offering bowl localizes to "Bonemass". A row that said
        /// <c>"displayName": "WoodHouse6"</c> would be a guess dressed up as data, so the page reads
        /// <c>displayName || prefab</c> and this field stays honest.</para>
        /// </summary>
        public string? DisplayName { get; set; }

        /// <summary>
        /// Which rule produced <see cref="DisplayName"/> - <c>BossAltar</c>, <c>Trader</c>,
        /// <c>TraderNpcTokenConvention</c> or <c>DiscoverLabel</c>; null exactly when the name is
        /// null. The card shows it as a "name from" row, because a name derived by convention
        /// (<c>TraderNpcTokenConvention</c>, the Bog Witch, the one join the dump does not make
        /// itself) must not look like one the game states outright.
        /// </summary>
        public string? DisplayNameSource { get; set; }

        /// <summary>The localization token the name came from, <c>$enemy_gdking</c>. Evidence, so a
        /// reader can check the claim against the game's own table; null when unnamed.</summary>
        public string? NameToken { get; set; }

        /// <summary>
        /// Every spelling that means this place: <b>[0] is always the prefab</b>, [1] the display
        /// name when there is one, and the rest are the other names the game supplies ("Aesir
        /// Passage" for <c>DN_Bossroom</c>, "The Emerald Flame" for <c>FaderLocation</c>). The card's
        /// "also called" row skips the first two; the type filter matches all of them.
        /// </summary>
        public List<string> Aliases { get; set; } = new List<string>();

        /// <summary>
        /// Which group this type is listed under: <c>boss</c> | <c>trader</c> |
        /// <c>biome:&lt;Biome enum name&gt;</c> | <c>multi</c>. A KEY, never drawn - the heading is
        /// <see cref="GroupHeading"/>. Empty when this build has no name table.
        /// </summary>
        public string GroupKey { get; set; } = "";

        /// <summary>The heading a human reads: "Bosses", "Traders", "Black Forest", "Several
        /// biomes". Biome headings are <c>MapPalette.Name</c>, so they are the same strings as the
        /// map legend and <c>/api/meta</c>'s palette.</summary>
        public string GroupHeading { get; set; } = "";

        /// <summary>
        /// Group order: 0 bosses, 1 traders, 2-10 the biomes in map-legend order, 11 the
        /// several-biome residue. <b>-1 when this build has no name table</b>, so a page that sorts
        /// by it keeps a stable order instead of scattering unnamed rows.
        /// </summary>
        public int GroupOrder { get; set; } = -1;

        /// <summary>
        /// Position inside the group. <b>Sort by <c>(groupOrder, sortIndex)</c> and do not compare
        /// the names in the browser</b>: the order was computed once, by
        /// <c>LocationDisplayOrder</c>, over the exact visible string - a second collation in
        /// JavaScript would drift from it the first time an underscore met a capital letter. -1 when
        /// this build has no name table.
        /// </summary>
        public int SortIndex { get; set; } = -1;

        /// <summary>boss | trader | dungeon | feature</summary>
        public string Category { get; set; } = "feature";

        /// <summary><c>ZoneLocation.m_unique</c>.</summary>
        public bool Unique { get; set; }

        /// <summary>
        /// True when this is a <c>m_unique</c> type that finished with more than one surviving
        /// candidate. The map must draw every one of them as a CANDIDATE, never as a position: the
        /// game keeps whichever zone a player generates first, which is exploration order, not seed.
        /// </summary>
        public bool CandidateSet { get; set; }

        public int Quantity { get; set; }

        /// <summary>The game's own <c>placed</c> counter for this type.</summary>
        public int Placed { get; set; }

        /// <summary>Instances of this type actually in the world.</summary>
        public int Count { get; set; }

        /// <summary>True when the generator ran out of attempts before reaching <c>m_quantity</c>.</summary>
        public bool Shortfall { get; set; }

        public int DungeonGenerators { get; set; }

        public string BiomeMask { get; set; } = "";

        /// <summary>
        /// The RAW <c>ZoneLocation.m_biome</c> bitmask.
        ///
        /// <para><b><see cref="BiomeMask"/> is a display string and is NOT a key.</b> It is built by
        /// joining <c>MapPalette.Name</c> with slashes for a human to read ("Swamp/Black
        /// Forest/Plains/Ocean"), so anything that wants to TEST a biome - a filter, a group, a
        /// legend swatch - must test these bits instead of parsing that sentence back apart.</para>
        /// </summary>
        public int BiomeBits { get; set; }

        /// <summary>
        /// The curated world feature this prefab belongs to - "Axe-head houses" - or null, which is
        /// the normal case. The type filter matches on it, and that one clause is what lets someone
        /// type "axe" and find both houses; they are neither adjacent in the listing nor named
        /// anything like each other.
        /// </summary>
        public string? FeatureName { get; set; }

        /// <summary>
        /// The feature's honesty sentence, printed verbatim beside the <c>m_unique</c> caveat. It is
        /// long on purpose: for the axe-head houses it has to keep TWO different uncertainties
        /// apart - whether the chest exists (zone-seeded, so the seed decides it and an offline tool
        /// could compute it, though this build does not yet) and what is inside it (an ambient
        /// <c>UnityEngine.Random</c> draw at first zone load, never a function of the seed at all).
        /// Multiplying them into one percentage would read like a forecast and would not be one.
        /// </summary>
        public string? ContentsNote { get; set; }
    }

    /// <summary>One group in the ordered location listing, as <c>/api/meta</c> ships it.</summary>
    public sealed class LocationGroupRow
    {
        /// <summary>boss | trader | biome:&lt;Biome enum name&gt; | multi.</summary>
        public string Key { get; set; } = "";

        public string Heading { get; set; } = "";

        public int Order { get; set; }
    }

    /// <summary>
    /// The seed-independent half of the location panel: how the listing is grouped and every
    /// spelling that means a place.
    ///
    /// <para><b>Why it is on <c>/api/meta</c> and not on every report.</b> None of it depends on the
    /// seed, and the page needs it before the first placement run has finished - the type dropdown
    /// and the search panel's location goals are usable while the map is still computing.</para>
    /// </summary>
    public sealed class LocationVocabulary
    {
        /// <summary>
        /// Every group, in render order, <b>including empty ones</b>. Of the 183 placed types none is
        /// Ocean-only, so the Ocean group has no members in this build; a renderer omits a group
        /// with nothing under it rather than hard-coding which one that is, because that is a fact
        /// about the dump and not about the taxonomy.
        /// </summary>
        public List<LocationGroupRow> Groups { get; set; } = new List<LocationGroupRow>();

        /// <summary>
        /// Every spelling a user might type mapped to the prefab it means - the prefab itself, the
        /// display name, and every alias, for all known types and not only the placed ones.
        ///
        /// <para><b>The browser needs this to canonicalise before it builds a goal target.</b> A
        /// query's goal id is derived from the raw target string, so <c>location:The Elder</c> would
        /// otherwise become a column header with a space in it. Matching here is exact; the server's
        /// own resolver additionally forgives case, spacing and a leading "the", so a page that
        /// misses on this map should send what the user typed and let the server answer.</para>
        ///
        /// <para>A spelling claimed by two different prefabs is DROPPED rather than given to either,
        /// and <c>LocationsReport.Notes</c> says so. There are none in this dump.</para>
        /// </summary>
        public Dictionary<string, string> Names { get; set; } = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary><c>LocalizationFile.language</c> - "English" here. Null when this build could not
        /// read the naming files, in which case <see cref="Names"/> holds prefabs only.</summary>
        public string? NamesLanguage { get; set; }
    }

    /// <summary>
    /// One instance. Short names on purpose: a full world is about 12,300 of these and the page reads
    /// them all at once.
    /// </summary>
    public sealed class LocationInstanceRow
    {
        /// <summary>Index into <see cref="LocationsReport.Types"/>.</summary>
        public int T { get; set; }

        public float X { get; set; }

        public float Z { get; set; }

        /// <summary><c>WorldGenerator.GetHeight</c> at the point - the y the save stores.</summary>
        public float Y { get; set; }

        /// <summary>Biome index, <c>Heightmap.BiomeIndex</c>; the palette on /api/meta names it.</summary>
        public int B { get; set; }

        public int Zx { get; set; }

        public int Zz { get; set; }
    }

    /// <summary>The seam: <c>vseed serve</c> owns the placement run, the server owns the caching.</summary>
    public delegate LocationsReport LocationsProvider(LocationsRequest request, CancellationToken ct);
}
