namespace SeedLab.Contracts.Dump
{
    /// <summary>
    /// <c>search.json</c> - the dump's explicit answer to "where does prefab X live?", for a list of
    /// names configured before the run (<c>Assets/SoughtPrefabNames</c>, default <c>piece_maypole</c>).
    ///
    /// <b>It exists because an absence must be stated, not implied.</b> Every other file in the dump is
    /// a table: if a name is not in it, the reader has to decide whether the name is absent from the
    /// game, absent from what the dumper reached, or absent because something failed half way. That is
    /// exactly the guess a user must never be left making after spending a game launch on a dump. So
    /// this file says, per sought name, <see cref="SearchTermDef.found"/> or NOT FOUND <b>in as many
    /// words</b>, and when not found it publishes <see cref="SearchTermDef.coverage"/>: what was
    /// actually searched, what was skipped, and what failed - and marks the verdict
    /// <see cref="SearchTermDef.inconclusive"/> when anything the name could have hidden behind did not
    /// complete.
    ///
    /// <b>Everything matches on the normalised name</b> - <c>Utils.GetPrefabName(name)</c>, the name
    /// truncated at the first <c>'('</c> or <c>' '</c>, which is the game's own identity for a
    /// GameObject. A child authored as <c>piece_maypole (1)</c> is a hit. The raw spelling is kept on
    /// every hit so nothing is hidden by the normalisation. The sought names themselves are normalised
    /// on the way in, so a config entry with a stray suffix still searches for the right thing.
    ///
    /// <b>What "found" does and does not mean.</b> A hit says the object is authored inside that host
    /// prefab. It does not say the object appears in any particular world: a hit behind a
    /// <c>RandomSpawn</c> appears only when that entry's draw passes and its gates hold, and a hit in a
    /// room appears only when a dungeon generator picks that room. The gate fields on each hit are there
    /// so a reader can say which.
    /// </summary>
    public sealed class SearchFile
    {
        public string? stamp;
        public int schema;

        /// <summary>The configured names as the user wrote them, before normalisation.</summary>
        public string[]? requestedNames;

        /// <summary>One verdict per sought name, in the order they were configured.</summary>
        public SearchTermDef[]? terms;

        /// <summary>A plain-language summary of every verdict, one line per term, so the answer is
        /// legible without a JSON reader. The same lines are printed to the console at the end of the
        /// run.</summary>
        public string[]? verdictLines;
    }

    /// <summary>The verdict for one sought prefab name.</summary>
    public sealed class SearchTermDef
    {
        /// <summary>The name as configured.</summary>
        public string? requested;

        /// <summary><c>Utils.GetPrefabName(requested)</c>: what was actually matched.</summary>
        public string? normalized;

        /// <summary><c>normalized.GetStableHashCode()</c>.</summary>
        public int hash;

        /// <summary><b>The answer.</b> True when <see cref="hitCount"/> is greater than zero.</summary>
        public bool found;

        /// <summary>
        /// True when <see cref="found"/> is false but the search was not complete: a prefab failed to
        /// load, a walk was switched off, or <c>DungeonDB.instance</c> was null. <b>A NOT FOUND with
        /// this set is not evidence of absence</b> - it means the dump did not look everywhere. Fix
        /// what <see cref="coverage"/> reports and run it again.
        /// </summary>
        public bool inconclusive;

        /// <summary>One sentence stating the verdict, including the word FOUND or NOT FOUND and, when
        /// applicable, INCONCLUSIVE. Also printed to the console.</summary>
        public string? verdict;

        /// <summary>
        /// True when <c>ZNetScene.instance.GetPrefab(normalized)</c> returned a GameObject: the piece
        /// exists as a registered network prefab in this build. Together with a NOT FOUND this
        /// separates "the name is wrong" from "the piece exists but nothing in world generation places
        /// it" - a player-built piece, for instance, is registered and appears in no prefab at all.
        /// </summary>
        public bool existsInZNetScene;

        /// <summary>False when <c>ZNetScene.instance</c> was null, so
        /// <see cref="existsInZNetScene"/> means nothing.</summary>
        public bool zNetSceneChecked;

        public int hitCount;

        /// <summary>Every place the name was found, in the order the walk visited them. Never
        /// truncated: this is the field the question is answered from.</summary>
        public SearchHitDef[]? hits;

        /// <summary>What was searched and what was not. Always written, whether or not the name was
        /// found, so a hit and a miss are equally auditable.</summary>
        public SearchCoverageDef? coverage;
    }

    /// <summary>One occurrence of a sought name.</summary>
    public sealed class SearchHitDef
    {
        /// <summary>Where it was found: <c>location</c> (inside a location prefab),
        /// <c>room</c> (inside a dungeon/camp room prefab), <c>randomObjectEntry</c> (one of the
        /// weighted options of a <c>RandomObject</c>, which is a prefab reference rather than a child),
        /// <c>locationTable</c> (the name IS a location in <c>ZoneSystem.m_locations</c>),
        /// <c>vegetationTable</c> (the name IS a vegetation entry), or <c>containerDrop</c> (an item in
        /// a container's default drop table).
        ///
        /// <c>location</c> and <c>room</c> also cover the host prefab's OWN root name, in which case
        /// <see cref="path"/> is null and <see cref="note"/> says so: the match IS the host, not
        /// something inside it.
        ///
        /// A <c>containerDrop</c> whose <see cref="randomObjectIndex"/> is not -1 is an item in a
        /// container that lives inside a <c>RandomObject</c> WEIGHTED OPTION rather than in the host's
        /// own children - it exists only when that option wins its draw.</summary>
        public string? kind;

        /// <summary>The prefab that contains it - the location or room prefab name, or null for a
        /// table hit.</summary>
        public string? hostName;

        /// <summary>For a room hit, the room list it belongs to; null otherwise.</summary>
        public string? hostRoomList;

        /// <summary>For a room hit, <c>RoomData.m_theme</c> as the raw <c>Room.Theme</c> bitmask - i.e.
        /// which <c>DungeonGenerator</c> themes can pick this room. 0 otherwise.</summary>
        public int hostTheme;

        /// <summary>For a room hit, <c>RoomData.m_enabled</c>: a disabled room is never picked.</summary>
        public bool hostEnabled;

        /// <summary>Hierarchy path from the host prefab's root, '/' separated, raw names. Null for a
        /// table hit.</summary>
        public string? path;

        /// <summary>The raw <c>GameObject.name</c> that matched, before normalisation.</summary>
        public string? rawName;

        /// <summary>True when the matching object carries its own <c>ZNetView</c>, i.e. it is
        /// instantiated as a real world object rather than being scenery or a grouping node.</summary>
        public bool hasNetView;

        /// <summary><c>Utils.IsEnabledInheirarcy</c> to the host root at dump time.</summary>
        public bool enabledInHierarchy;

        /// <summary>True when the object sits inside some <c>RandomSpawn</c>'s <c>m_OffObject</c>: it
        /// appears only when that entry does NOT spawn.</summary>
        public bool underAnOffObject;

        // ---- the gate ---------------------------------------------------------------------------------

        /// <summary>Index in the host's <c>randomSpawns</c> array of the nearest ancestor-or-self that
        /// is a <c>RandomSpawn</c>, or <b>-1 when nothing gates it</b>: an object with -1 here is
        /// unconditional in that prefab and appears in every instance of it.</summary>
        public int gatedByRandomSpawnIndex;

        /// <summary>That RandomSpawn's path, or null.</summary>
        public string? gatedByRandomSpawnPath;

        /// <summary>That RandomSpawn's <c>m_chanceToSpawn</c>, 0..100, compared with <c>&lt;=</c>
        /// against the draw. 0 when nothing gates it - read <see cref="gatedByRandomSpawnIndex"/>
        /// first.</summary>
        public float chanceToSpawn;

        /// <summary>That RandomSpawn's <c>m_requireBiome</c> bitmask. Inert inside a room.</summary>
        public int requireBiome;

        public int minElevation;
        public int maxElevation;
        public bool notInLava;

        /// <summary>That RandomSpawn's <c>m_dungeonRequireTheme</c> bitmask. Inert inside a location;
        /// live inside a room.</summary>
        public int dungeonRequireTheme;

        /// <summary>True when the gating RandomSpawn's own
        /// <see cref="RandomSpawnDef.prefabPositionIsInstanceDependent"/> is set, so its elevation and
        /// lava gates cannot be evaluated offline.</summary>
        public bool gatePositionIsInstanceDependent;

        /// <summary>For a <c>randomObjectEntry</c> hit: the index of the <c>RandomObject</c> in the
        /// host's array, otherwise -1.</summary>
        public int randomObjectIndex;

        /// <summary>For a <c>randomObjectEntry</c> hit: the entry's index in <c>m_objects</c> and its
        /// share of the weighted draw. -1 / 0 otherwise.</summary>
        public int randomObjectEntryIndex;

        public float randomObjectEntryWeight;
        public float randomObjectTotalWeight;

        /// <summary>Anything else worth saying about this particular hit, in plain language.</summary>
        public string? note;
    }

    /// <summary>What the search actually covered, so a NOT FOUND can be trusted - or explicitly
    /// distrusted.</summary>
    public sealed class SearchCoverageDef
    {
        /// <summary>Plain-language list of the sources that WERE searched.</summary>
        public string[]? searched;

        /// <summary>Plain-language list of the sources that were NOT searched although they should
        /// have been, with the reason - a walk that was switched off, a walk that ran but contributed
        /// nothing, a prefab that failed to load, a subtree that threw. <b>A non-empty list here is
        /// what makes a NOT FOUND <see cref="SearchTermDef.inconclusive"/>.</b> Empty when this run's
        /// coverage was complete.
        ///
        /// <b>Driven by COUNTS, not by flags.</b> Until 2026-09-23 a source was called covered as soon
        /// as its walk had "run", and a walk that loaded zero prefabs - an empty
        /// <c>DungeonDB.GetRooms()</c>, a location list with nothing enabled - therefore reported FULL
        /// coverage and let a NOT FOUND come back confident. Every entry below is now decided by how
        /// many items the source actually contributed, so a source that contributed none is listed
        /// here whatever its flag says.</summary>
        public string[]? notSearched;

        /// <summary>What no run of this plugin can reach, however well it goes. Kept apart from
        /// <see cref="notSearched"/> on purpose: a standing limitation is not a defect of this run, and
        /// counting it as one would mark every verdict inconclusive forever and make the word
        /// meaningless.</summary>
        public string[]? standingLimitations;

        public int locationPrefabsWalked;
        public int locationPrefabsLoaded;
        public int locationPrefabsFailed;

        public int roomPrefabsWalked;
        public int roomPrefabsLoaded;
        public int roomPrefabsFailed;

        /// <summary>How many prefab child walks recorded an <c>error</c> even though the asset loaded.
        /// Each one is a subtree that was not fully indexed.</summary>
        public int interiorWalksWithErrors;

        // ---- RandomObject options: the prefab references that are not children ------------------------

        /// <summary>How many <c>RandomObject</c> weighted options were RESOLVED - the option prefab's
        /// own subtree read for <c>Container</c>s. An option is a prefab reference rather than a child,
        /// so before this it was matched by name only and anything inside it, chests included, was
        /// invisible.</summary>
        public int randomObjectOptionsResolved;

        /// <summary>How many <c>Container</c>s were found inside those options, each with its full
        /// drop table.</summary>
        public int randomObjectOptionContainersRead;

        /// <summary>How many option scans threw. <b>Greater than zero means some option's contents
        /// were never read</b>, so a NOT FOUND is inconclusive.</summary>
        public int randomObjectOptionScanErrors;

        /// <summary>How many <c>RandomObject</c>s that have options of their own sit INSIDE a
        /// RandomObject option. Options are resolved exactly ONE level deep, so anything reachable only
        /// through such a nested option is not in the dump. Reported in
        /// <see cref="standingLimitations"/> rather than <see cref="notSearched"/>, and therefore does
        /// <b>not</b> make a verdict <see cref="SearchTermDef.inconclusive"/>: a village whose house
        /// options carry their own RandomObjects makes this non-zero in essentially every run, and a
        /// limitation that is present every time tells a reader nothing about THIS run.</summary>
        public int nestedRandomObjectsInOptions;

        public int locationTableEntries;
        public int vegetationTableEntries;

        /// <summary>True when the location prefab child walk ran at all
        /// (<c>WalkLocationPrefabs</c> and <c>WalkPrefabChildren</c> both on).</summary>
        public bool locationChildWalkRan;

        /// <summary>True when the room walk ran at all.</summary>
        public bool roomWalkRan;

        /// <summary>Non-null when the room walk was skipped, with the reason.</summary>
        public string? roomWalkSkipped;
    }
}
