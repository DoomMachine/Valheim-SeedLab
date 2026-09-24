namespace SeedLab.Contracts.Dump
{
    /// <summary>
    /// <c>localization.json</c> - the game's own token-to-text table, and the language it is in.
    ///
    /// <para><b>Why it is dumped rather than written down.</b> Every player-facing name in Valheim is a
    /// token: the altar prefab is <c>GDKing</c> and the creature inside it is <c>$enemy_gdking</c>,
    /// which the game renders as "The Elder". A tool that shows <c>GDKing</c> is showing a developer's
    /// identifier to a player who has never seen it. The obvious fix - a hand-written table of ten
    /// names - is the thing this project does not do: a curated name is a name that drifts silently at
    /// the next update, and it covers ten prefabs where the game has thousands of strings.</para>
    ///
    /// <para><b>The language matters and is recorded.</b> These strings are whatever
    /// <c>Localization.GetSelectedLanguage()</c> was at dump time. A dump taken with the game in
    /// Swedish holds Swedish names, and every consumer has to be able to see that rather than infer
    /// it - so the language is a field, not an assumption.</para>
    ///
    /// <para><b>A token that is missing is left missing.</b> <c>Localization.Localize</c> returns the
    /// token itself, decorated, when it has no translation. The dump records the raw table instead, so
    /// a consumer can tell "this has no name" from "this is named '$enemy_gdking'".</para>
    /// </summary>
    public sealed class LocalizationFile
    {
        public string? stamp;
        public int schema;

        /// <summary><c>Localization.GetSelectedLanguage()</c> at dump time, e.g. <c>English</c>.</summary>
        public string? language;

        /// <summary>Every language the build offers, from <c>GetLanguages()</c>.</summary>
        public string[]? languages;

        /// <summary>Entries in <see cref="translations"/>.</summary>
        public int count;

        /// <summary>
        /// The whole <c>Localization.m_translations</c> dictionary: key WITHOUT the leading <c>$</c>
        /// (that is how the game stores it), value the rendered text. Read by reflection, because the
        /// field is private and the public <c>Localize</c> would have to be asked one token at a time
        /// for names nobody has thought of yet.
        /// </summary>
        public System.Collections.Generic.Dictionary<string, string>? translations;

        /// <summary>Non-null when the table could not be read at all; then <see cref="translations"/>
        /// is empty and no name in this dump is resolvable.</summary>
        public string? skipped;
    }

    /// <summary>
    /// One <c>Character</c> standing in a location or room prefab.
    ///
    /// <para><b>This is NOT how a boss altar is identified</b>, though it was added believing it was.
    /// The dump of 2026-09-23 found just TWO Characters across all 186 location prefabs - the Deep
    /// North boss room's <c>FrozenKing</c> ("Kall Fimbulbringer", <c>m_boss</c> true) and
    /// <c>BogWitch_Camp</c>'s <c>BogWitchKvastur</c> ("Kvastur", not a boss) - because a classic altar
    /// SUMMONS its boss rather than containing it. Use <see cref="OfferingBowlDef"/> for that. What
    /// this array is genuinely good for is the creatures that really do stand in a prefab, and for
    /// <c>Trader</c>'s neighbours. (Corrected 2026-09-24: this said ONE, counting only the boss.)</para>
    ///
    /// <para><c>m_boss</c> and <c>m_bossOrder</c> are still read here, and they are still the game's
    /// own answer for any Character that IS present.</para>
    /// </summary>
    public sealed class CharacterDef
    {
        /// <summary>Transform path from the host prefab's root.</summary>
        public string? path;

        /// <summary><c>Utils.GetPrefabName</c> of the GameObject.</summary>
        public string? prefabName;

        /// <summary><c>Character.m_name</c> - a localization token, e.g. <c>$enemy_gdking</c>.</summary>
        public string? nameToken;

        /// <summary>That token resolved through the dumped table, or null when it has no entry.</summary>
        public string? localizedName;

        /// <summary><c>Character.m_boss</c>. The authority for "this location is a boss altar".</summary>
        public bool boss;

        /// <summary><c>Character.m_bossOrder</c> - the progression order the game itself uses.</summary>
        public int bossOrder;

        /// <summary><c>Character.m_faction</c> as its raw enum value.</summary>
        public int faction;

        /// <summary><c>Character.m_group</c>, verbatim.</summary>
        public string? group;

        /// <summary>
        /// <c>Utils.IsEnabledInheirarcy(go, root)</c> - the game's own test, walking <c>activeSelf</c> up
        /// to the prefab root.
        ///
        /// <para>It is NOT <c>activeInHierarchy</c>, which is false for every node of a prefab asset
        /// because the asset is not in a scene. That was the first version and it recorded false for all
        /// six occupants in the dump while the same file reported them enabled three fields away.</para>
        /// </summary>
        public bool enabledInHierarchy;

        /// <summary><c>gameObject.activeSelf</c>, which does work on an asset.</summary>
        public bool activeSelf;
    }

    /// <summary>
    /// An <c>OfferingBowl</c> - the altar a boss is summoned at, and the ONLY link from a boss altar
    /// to the boss.
    ///
    /// <para><b>Why this exists, and what it corrects.</b> The occupant walk was added on the
    /// assumption that a boss altar contains its boss as a <c>Character</c>. It does not: the dump of
    /// 2026-09-23 found just TWO Characters across all 186 location prefabs (<c>DN_Bossroom</c>'s
    /// <c>FrozenKing</c> and <c>BogWitch_Camp</c>'s <c>BogWitchKvastur</c>), because every classic
    /// altar SPAWNS its boss at runtime. What the altar does carry is an <c>OfferingBowl</c> with
    /// <c>m_bossPrefab</c>, and that prefab's <c>Character.m_name</c> is the name a player knows -
    /// "Eikthyr" for <c>Eikthyrnir</c>, "The Elder" for <c>GDKing</c>. Eight location prefabs have
    /// one: the seven altars and <c>NorthMemorialPlace</c>.</para>
    ///
    /// <para><b>The location walk alone does not find every boss</b> (2026-09-24). The Mistlands
    /// queen's bowl is not in a location prefab at all: it is in the ROOM prefab
    /// <c>dvergr_new_bossroom_ENTRANCE02</c> (<c>roomTheme</c> 256), the only bowl among all 358
    /// rooms, because <c>Mistlands_DvergrBossEntrance1</c> generates its interior. Joining
    /// <c>locationprefabs</c> <c>generators[].themes</c> to <c>roomchildren</c> <c>roomTheme</c>
    /// completes the set and makes <c>m_bossOrder</c> contiguous 0-7. Reading only
    /// <c>locationchildren.json</c> silently reports seven bosses and misses The Queen.</para>
    /// </summary>
    public sealed class OfferingBowlDef
    {
        public string? path;
        public string? prefabName;

        /// <summary><c>OfferingBowl.m_name</c>, a localization token.</summary>
        public string? nameToken;

        public string? localizedName;

        /// <summary><c>Utils.GetPrefabName(m_bossPrefab)</c> - the creature this altar summons.</summary>
        public string? bossPrefabName;

        /// <summary>That creature's <c>Character.m_name</c> token, e.g. <c>$enemy_gdking</c>.</summary>
        public string? bossNameToken;

        /// <summary>And resolved - "The Elder". This is the name the GUI and the CLI should show.</summary>
        public string? bossLocalizedName;

        /// <summary><c>Character.m_boss</c> on the summoned creature: the derived proof of "boss altar".</summary>
        public bool bossFlag;

        /// <summary><c>Character.m_bossOrder</c> - the game's own progression order.</summary>
        public int bossOrder;

        /// <summary><c>m_bossItem</c>'s prefab name - the offering that summons it.</summary>
        public string? offeringItemName;

        /// <summary>That item's display token, and the count the bowl wants.</summary>
        public string? offeringItemToken;

        public string? offeringItemLocalizedName;
        public int offeringItemCount;

        /// <summary><c>m_setGlobalKey</c>, set when the boss dies.</summary>
        public string? setGlobalKey;
    }

    /// <summary>
    /// A <c>RuneStone</c> - the other place the game names things a player reads. 23 location prefabs
    /// carry one (27 stones in all), which is nearly six times the coverage of
    /// <c>Location.m_discoverLabel</c> (4 of 186). Counted 2026-09-24; this said 24.
    ///
    /// <para><b>A runestone does not name its host.</b> <c>m_locationName</c> is the location the stone
    /// makes the game DISCOVER (a raw prefab name, not a token) and <c>m_pinName</c> is the map-pin
    /// caption the game writes for it - so a stone inside location X routinely names a different
    /// location Y. <c>StartTemple</c>'s five <c>BossStone_*</c> are exactly that. And
    /// <c>nameToken</c>/<c>labelToken</c> name the OBJECT, not the place: the 27 stones carry only
    /// "Runestone" (21), "Sacrificial Stone" (5) and "A mysterious text" (1). None of these three
    /// fields is a source of location names.</para>
    /// </summary>
    public sealed class RuneStoneDef
    {
        public string? path;
        public string? prefabName;
        public string? nameToken;
        public string? localizedName;

        /// <summary><c>m_topic</c> and <c>m_label</c>, tokens and resolved.</summary>
        public string? topicToken;

        public string? topicLocalized;
        public string? labelToken;
        public string? labelLocalized;

        /// <summary><c>m_locationName</c> and <c>m_pinName</c>: what the map calls the place.</summary>
        public string? locationNameToken;

        public string? locationNameLocalized;
        public string? pinNameToken;
        public string? pinNameLocalized;
    }

    /// <summary>One <c>Trader</c> found inside a prefab - Haldor, Hildir, the Bog Witch.</summary>
    public sealed class TraderDef
    {
        public string? path;
        public string? prefabName;

        /// <summary><c>Trader.m_name</c> - a localization token, e.g. <c>$npc_haldor</c>.</summary>
        public string? nameToken;

        public string? localizedName;

        /// <summary>See <see cref="CharacterDef.enabledInHierarchy"/>: the game's own test, not
        /// <c>activeInHierarchy</c>.</summary>
        public bool enabledInHierarchy;

        public bool activeSelf;
    }

    /// <summary>
    /// One <c>Teleport</c> inside a location or room prefab - a dungeon's door in or out.
    ///
    /// <para><b>Why it is dumped (2026-09-24).</b> It is the game's own name for a DUNGEON, which
    /// for most dungeons nothing else in this dump carries. <c>Teleport.Interact</c>, after a
    /// successful <c>TeleportTo</c>, calls <c>MessageHud.instance.ShowBiomeFoundMsg(m_enterText,
    /// false)</c> when <c>m_enterText</c> is non-empty - the large caption a player sees on walking
    /// into a crypt. The tokens are not in code (<c>assembly_valheim</c> holds only
    /// <c>location_enter</c>, as the constructor default of <c>m_hoverText</c>); they live on the
    /// prefabs in the SoftRef bundles, so reading them needs a loaded asset - the dumper reads them in
    /// the running game, and a Unity editor of the game's exact version is an untried second
    /// route.</para>
    ///
    /// <para><b>What the first dump that wrote it says</b> (run 6, 2026-09-24): 38 teleports, all in
    /// location prefabs and none in any of the 358 room prefabs, two per prefab over 19 prefabs - an
    /// entrance (<c>$location_enter</c>) with a caption and an exit (<c>$location_exit</c>) whose
    /// <c>m_enterText</c> is empty, each targeting the other inside the same prefab. 18 entrances are
    /// active: 17 name their prefab (Crypt2/3/4 share <c>$location_forestcrypt</c>, the Dvergr town
    /// entrances <c>$location_dvergrtown</c>, MorgenHole1/2/3 <c>$location_morgenhole</c>) and
    /// <c>Mistlands_DvergrBossEntrance1</c>'s ("Infested Citadel") is an alias of The Queen's place;
    /// <c>DN_Bossroom</c>'s entrance (<c>$location_dnbossroomnew</c>, "The Prison") is inactive in
    /// the prefab. Two <c>location_*</c> tokens of the English table are on no door either walk
    /// found: <c>location_darkesthole</c> and <c>location_dnbossroom</c>.</para>
    ///
    /// <para><c>m_targetPoint</c> is a serialized <c>Teleport</c> reference that no code assigns
    /// (only <c>Teleport.Interact</c> reads it, decompiled 2026-09-24), so the door-to-door link is
    /// authored in the prefab. It is recorded as a path when the target sits inside the same prefab;
    /// a target anywhere else is recorded only as present.</para>
    /// </summary>
    public sealed class TeleportDef
    {
        /// <summary>Transform path from the host prefab's root.</summary>
        public string? path;

        /// <summary><c>Utils.GetPrefabName</c> of the GameObject.</summary>
        public string? prefabName;

        /// <summary><c>m_hoverText</c> - the interaction prompt's token, <c>$location_enter</c> by
        /// default - and resolved.</summary>
        public string? hoverTextToken;

        public string? hoverTextLocalized;

        /// <summary><c>m_enterText</c> - the caption shown after the teleport - and resolved. Empty
        /// token (and null resolved) means the game shows no caption for this door.</summary>
        public string? enterTextToken;

        public string? enterTextLocalized;

        /// <summary><c>m_targetPoint != null</c>. <c>Interact</c> returns false without moving
        /// anyone when this is false.</summary>
        public bool hasTarget;

        /// <summary>True when the target is this prefab's root or one of its descendants.</summary>
        public bool targetInPrefab;

        /// <summary>The target's path from the host root when <see cref="targetInPrefab"/>, else null.
        /// A target that IS the root gets the root's own name, as every other <c>path</c> in this file
        /// does for a component on the root - never the empty string.</summary>
        public string? targetPath;

        /// <summary>See <see cref="CharacterDef.enabledInHierarchy"/>: the game's own test, not
        /// <c>activeInHierarchy</c>.</summary>
        public bool enabledInHierarchy;

        public bool activeSelf;
    }

    /// <summary>
    /// One <c>Vegvisir</c> inside a location or room prefab - the stone that reveals other places on
    /// the map.
    ///
    /// <para><b>Like a <see cref="RuneStoneDef"/>, a Vegvisir does not name its host.</b>
    /// <c>Vegvisir.Interact</c> calls <c>Game.DiscoverClosestLocation(m_locationName, position,
    /// m_pinName, (int)m_pinType, m_showMap, m_discoverAll)</c> once per entry of <c>m_locations</c>,
    /// so each entry names the location it REVEALS and the caption of the pin the game writes for it
    /// (decompiled 2026-09-24). That makes it evidence for the name of a DIFFERENT prefab, and a
    /// consumer must join on <see cref="VegvisirLocationDef.locationName"/>, never on the host.</para>
    /// </summary>
    public sealed class VegvisirDef
    {
        public string? path;
        public string? prefabName;

        /// <summary><c>m_name</c>, <c>$piece_vegvisir</c> by default, and resolved.</summary>
        public string? nameToken;

        public string? localizedName;

        /// <summary><c>m_hoverName</c> - shown after <c>m_name</c> in the hover text; the C# default
        /// is the literal <c>Pin</c> - and resolved.</summary>
        public string? hoverNameToken;

        public string? hoverNameLocalized;

        /// <summary><c>m_useText</c>, <c>$piece_register_location</c> by default, and resolved.</summary>
        public string? useTextToken;

        public string? useTextLocalized;

        /// <summary><c>m_setsGlobalKey</c> and <c>m_setsPlayerKey</c>, set by <c>Interact</c> when
        /// non-empty.</summary>
        public string? setsGlobalKey;

        public string? setsPlayerKey;

        /// <summary><c>m_locations</c>, in list order - the order <c>Interact</c> discovers them.</summary>
        public VegvisirLocationDef[]? locations;

        public bool enabledInHierarchy;
        public bool activeSelf;
    }

    /// <summary>One entry of <c>Vegvisir.m_locations</c> (<c>Vegvisir.VegvisrLocation</c> - the
    /// game's spelling).</summary>
    public sealed class VegvisirLocationDef
    {
        /// <summary><c>m_locationName</c>: the location the game discovers - a raw prefab name, the
        /// key <c>DiscoverClosestLocation</c> matches, not a token.</summary>
        public string? locationName;

        /// <summary><c>m_pinName</c>, the pin caption (C# default the literal <c>Pin</c>), and
        /// resolved.</summary>
        public string? pinNameToken;

        public string? pinNameLocalized;

        /// <summary><c>(int)m_pinType</c> and its <c>Minimap.PinType</c> name.</summary>
        public int pinType;

        public string? pinTypeName;

        /// <summary><c>m_discoverAll</c>: every location of that name rather than the closest.</summary>
        public bool discoverAll;

        /// <summary><c>m_showMap</c>: whether discovering opens the map.</summary>
        public bool showMap;
    }
}
