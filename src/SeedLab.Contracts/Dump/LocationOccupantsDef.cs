namespace SeedLab.Contracts.Dump
{
    /// <summary>
    /// <c>locationchildren.json</c> read as OCCUPANTS ONLY - the slice that answers "what is this place
    /// called", with none of the slice that answers "what does its RNG stream do".
    ///
    /// <para><b>Why a second DTO for a file that already has one.</b>
    /// <see cref="LocationChildrenFile"/> is the full contract: 29.9 MB of RandomSpawn draw order,
    /// container drop tables and per-prefab name indices, which is what a stream replay needs and what
    /// a NAME needs none of. Naming reads four arrays - <c>characters</c>, <c>traders</c>,
    /// <c>offeringBowls</c>, <c>runeStones</c> - out of 186 entries. Deserializing into the full DTO to
    /// reach them would allocate every <see cref="RandomSpawnDef"/>, <see cref="ContainerDef"/> and
    /// <see cref="PrefabNameDef"/> in the file and throw them away. <c>System.Text.Json</c> skips a
    /// member no DTO declares, so a trimmed type parses the same bytes and builds only these objects.
    /// The parse still walks the whole file; the allocation does not.</para>
    ///
    /// <para><b>Why it is a fresh <c>sealed class</c> rather than a base of the real one.</b>
    /// <c>gen_schema.py</c> transcribes the fields DECLARED on a class and does not follow a base
    /// class - which is why <c>LocationChildrenDef : InteriorDef</c> has a schema covering its own six
    /// fields and none of <see cref="InteriorDef"/>'s. Deriving here would therefore produce a schema
    /// that validates nothing. Declaring the fields outright makes <see cref="SeedLab.Data.StrictJson"/>
    /// demand each of them, which is the point: a dump that stopped writing <c>offeringBowls</c> must
    /// fail loudly, not report a world with no bosses.</para>
    ///
    /// <para><b>The dump satisfies this schema.</b> Measured against
    /// <c>data\1.0.15-59f53fb5\locationchildren.json</c> on 2026-09-24: 186 entries, every one carrying
    /// all four occupant arrays plus <c>prefabName</c> and <c>occupantsCaptured</c>; 2 characters,
    /// 3 traders, 9 offering bowls and 27 runestones in total, and every element of those arrays
    /// carries every field <see cref="CharacterDef"/>, <see cref="TraderDef"/>,
    /// <see cref="OfferingBowlDef"/> and <see cref="RuneStoneDef"/> declare. A schema ahead of the
    /// shipped dump fails closed, so that was checked before this type existed.</para>
    /// </summary>
    public sealed class LocationOccupantsFile
    {
        public string? stamp;
        public int schema;

        /// <summary>Entries in <see cref="locations"/>; 186 for this build. Checked against the array
        /// length, so a truncated file is an error rather than a short answer.</summary>
        public int count;

        public LocationOccupantsDef[]? locations;
    }

    /// <summary>
    /// One location prefab's occupants. The four arrays are the same objects the full walk records -
    /// see <see cref="InteriorDef.characters"/> and its neighbours for what each one is and is not good
    /// for.
    /// </summary>
    public sealed class LocationOccupantsDef
    {
        /// <summary>The prefab's SoftReference name - the identity <c>ZoneLocation.m_prefab.Name</c>
        /// carries and the key every other table in this dump joins on.</summary>
        public string? prefabName;

        /// <summary>False on a file written before 2026-09-23, where the occupant arrays do not exist
        /// and deserialize to null. <b>An empty array and "not captured" are different facts</b>, and
        /// reading the first as the second would say "this world has no bosses" - so a reader refuses
        /// on false rather than reporting nothing found. True on all 186 entries of the shipped
        /// dump.</summary>
        public bool occupantsCaptured;

        /// <summary>Every <c>Character</c> standing in the prefab. TWO in all 186 (2026-09-24):
        /// <c>DN_Bossroom</c>'s <c>FrozenKing</c> and <c>BogWitch_Camp</c>'s <c>BogWitchKvastur</c>.
        /// See <see cref="CharacterDef"/>: a classic altar SUMMONS its boss and contains no Character,
        /// so this is not how a boss altar is identified.</summary>
        public CharacterDef[]? characters;

        /// <summary>Every <c>Trader</c> - 3 in all 186: Haldor, Hildir, the Bog Witch.</summary>
        public TraderDef[]? traders;

        /// <summary>Every <c>OfferingBowl</c> - 9 bowls over 8 location prefabs. This is what names a
        /// boss altar. It does not find them all on its own: The Queen's bowl is in a ROOM prefab, see
        /// <see cref="RoomOccupantsFile"/>.</summary>
        public OfferingBowlDef[]? offeringBowls;

        /// <summary>Every <c>RuneStone</c> - 27 stones over 23 host prefabs. <b>Not a source of place
        /// names</b>: a stone names the location it makes the game DISCOVER, not the one it stands in,
        /// and its own name token is "Runestone". See <see cref="RuneStoneDef"/>.</summary>
        public RuneStoneDef[]? runeStones;
    }

    /// <summary>
    /// <c>roomchildren.json</c> read as OCCUPANTS ONLY - the same trim as
    /// <see cref="LocationOccupantsFile"/>, applied to the 74.5 MB room file for one reason.
    ///
    /// <para><b>Why the room file is in the naming path at all.</b> The Mistlands boss has no offering
    /// bowl in any location prefab. Its bowl is in the ROOM prefab
    /// <c>dvergr_new_bossroom_ENTRANCE02</c> - <c>roomTheme</c> 256, <c>_RoomList_Mistlands</c>,
    /// <c>entrance</c> true, bowl <c>offeraltar_queen</c> summoning <c>SeekerQueen</c>
    /// (<c>$enemy_seekerqueen</c>, "The Queen", <c>bossOrder</c> 6) - because
    /// <c>Mistlands_DvergrBossEntrance1</c> generates its interior instead of authoring it. It is the
    /// ONLY bowl among all 358 rooms, and theme 256 is used by exactly one location generator in all
    /// 186 location prefabs, so the join <c>locationprefabs generators[].themes &amp; rooms[].roomTheme</c>
    /// is unambiguous. Reading <c>locationchildren.json</c> alone reports seven bosses and misses her;
    /// with this file the derived boss set is the game's own contiguous <c>m_bossOrder</c> 0-7
    /// (verified 2026-09-24).</para>
    ///
    /// <para><b>Cost, and who may pay it.</b> 74.5 MB is read, SHA-256'd and validated on first touch.
    /// Nothing on the per-seed path may reach for it; it is loaded lazily, once per process, by the
    /// naming layer only.</para>
    /// </summary>
    public sealed class RoomOccupantsFile
    {
        public string? stamp;
        public int schema;

        /// <summary>Entries in <see cref="rooms"/>; 358 for this build.</summary>
        public int count;

        /// <summary>Non-null when the room walk could not run at all, in which case
        /// <see cref="rooms"/> is empty. That is NOT "there are no bosses in rooms" - it is "this dump
        /// cannot say", and a reader must refuse rather than answer. Null in the shipped dump.</summary>
        public string? skipped;

        public RoomOccupantsDef[]? rooms;
    }

    /// <summary>One room prefab's occupants, plus the two fields that say which dungeon or camp can
    /// contain it.</summary>
    public sealed class RoomOccupantsDef
    {
        /// <summary>The room prefab's SoftReference name, e.g. <c>dvergr_new_bossroom_ENTRANCE02</c>.</summary>
        public string? prefabName;

        /// <summary>See <see cref="LocationOccupantsDef.occupantsCaptured"/>. True on all 358 entries
        /// of the shipped dump.</summary>
        public bool occupantsCaptured;

        /// <summary><c>Room.m_theme</c> on the component, the raw <c>Room.Theme</c> bitmask. This is the
        /// side of the join the bowl room is identified by (256).</summary>
        public int roomTheme;

        /// <summary><c>RoomData.m_theme</c> - <b>the field <c>SetupAvailableRooms</c> actually filters
        /// on</b>. Normally equal to <see cref="roomTheme"/>; it is not on 3 of the 358 rooms
        /// (measured 2026-09-24), so both are carried and a consumer that joins on one can assert the
        /// other rather than assume they agree.</summary>
        public int roomDataTheme;

        /// <summary>The normalised name of the <c>RoomList</c> this room came from, e.g.
        /// <c>_RoomList_Mistlands</c>. Non-null on all 358 entries of the shipped dump; it is carried
        /// as evidence in the note that explains where a room-derived name came from.</summary>
        public string? roomListName;

        /// <summary>Every <c>OfferingBowl</c> in the room - exactly ONE in all 358 rooms, the Queen's.</summary>
        public OfferingBowlDef[]? offeringBowls;

        /// <summary>Every <c>Character</c> in the room. One in all 358. Carried beside the bowls
        /// because the location-side pair of these two arrays is what the naming rules read, and a room
        /// that gains a boss Character in a later build must be visible to the same code.</summary>
        public CharacterDef[]? characters;
    }
}
