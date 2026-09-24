namespace SeedLab.Contracts.Dump
{
    /// <summary>
    /// Everything the prefab-child walk records about ONE prefab's interior, shared by the two kinds of
    /// prefab that spend a seeded RNG stream on their own children:
    /// <list type="bullet">
    /// <item>a LOCATION prefab, spawned by <c>ZoneSystem.SpawnLocation</c> - see
    /// <see cref="LocationChildrenDef"/>;</item>
    /// <item>a dungeon/camp ROOM prefab, placed by <c>DungeonGenerator.PlaceRoom</c> - see
    /// <see cref="RoomChildrenDef"/>.</item>
    /// </list>
    ///
    /// The two call sites are near-identical in the shipped 1.0.15 IL: both build the three arrays with
    /// <c>Utils.GetEnabledComponentsInChildren&lt;T&gt;</c> in the order ZNetView, RandomObject,
    /// RandomSpawn, both call <c>Prepare()</c> on every RandomSpawn, both then reseed and consume the
    /// stream RandomSpawn-first, RandomObject-second, one draw each. They differ in three ways, and all
    /// three are recorded on the derived type rather than smoothed over here:
    /// <list type="number">
    /// <item><b>The anchor.</b> <c>SpawnLocation</c> zeroes the ASSET ROOT's position and rotation and
    /// reads <c>child.transform.position</c> back; <c>PlaceRoom</c> leaves the asset alone and computes
    /// <c>Inverse(room.transform.rotation) * (child.position - room.transform.position)</c>, anchored on
    /// the <c>Room</c> COMPONENT's transform. <see cref="RoomChildrenDef.roomComponentOnRoot"/> says
    /// when the two coincide. Either way <see cref="RandomSpawnDef.prefabPosition"/> holds the value the
    /// game reads.</item>
    /// <item><b>The seed.</b> A location's stream is
    /// <c>worldSeed + zone.x*4271 + zone.y*9187</c>; a room's is
    /// <c>(int)v.x*4271 + (int)v.y*9187 + (int)v.z*2134</c> over the room's placement position, plus the
    /// generator's own <c>GetSeed()</c> when <c>DungeonGenerator.m_addBaseSeedToRandomSpawn</c> is set.
    /// Note the <c>+2134</c>: <c>DungeonGenerator.GetSeed</c> uses <c>-2134</c> for its z term and the
    /// two are different constants.</item>
    /// <item><b>The gates.</b> <c>SpawnLocation</c> calls <c>Randomize(pos, locationComponent)</c> with
    /// the DungeonGenerator argument defaulted to null; <c>PlaceRoom</c> calls
    /// <c>Randomize(pos, null, this)</c>. In <c>RandomSpawn.Randomize</c> the biome gate is
    /// <c>loc != null &amp;&amp; m_requireBiome != None</c> and the theme gate is
    /// <c>dg != null &amp;&amp; m_dungeonRequireTheme != None</c>, so a location's RandomSpawns are never
    /// theme-gated and a room's are never biome-gated (verified from IL, 2026-09-23).</item>
    /// </list>
    /// </summary>
    public class InteriorDef
    {
        /// <summary>The prefab's SoftReference name - the identity the game and the save files use.</summary>
        public string? prefabName;

        public AssetIdDef? assetId;

        /// <summary><b>Did the ASSET load?</b> True means <c>SoftReference.Load()</c> produced a
        /// GameObject. It does NOT mean the walk succeeded - read <see cref="error"/> for that. The two
        /// were conflated until 2026-09-23, which let the same prefab appear as loaded in
        /// <c>locationprefabs.json</c> and as not-loaded here.</summary>
        public bool loaded;

        /// <summary>Null when the walk completed. Non-null means the arrays below are empty or partial
        /// because something failed - <b>not</b> because the prefab has no children. When
        /// <see cref="loaded"/> is true and this is non-null, the asset loaded and the WALK failed.</summary>
        public string? error;

        // ---- the anchor transform, so a reader can audit the position arithmetic ------------------

        // ---- who is in here, and what they are called ---------------------------------------------

        /// <summary>
        /// Every <c>Character</c> in this prefab. It is what makes "boss altar" a DERIVED fact:
        /// <c>Character.m_boss</c> is a serialized field on the creature, so a location that contains a
        /// boss no longer has to be on a hand-written list. Empty for the overwhelming majority of
        /// prefabs, which contain no creature at all.
        /// </summary>
        public CharacterDef[]? characters;

        /// <summary>Every <c>Trader</c> in this prefab - Haldor, Hildir, the Bog Witch. Same reasoning
        /// as <see cref="characters"/>: it replaces a curated list with a read.</summary>
        public TraderDef[]? traders;

        /// <summary>
        /// Every <c>OfferingBowl</c>. This, and NOT <see cref="characters"/>, is what identifies a boss
        /// altar and names its boss - a classic altar contains no <c>Character</c> at all, it summons
        /// one. Eight location prefabs have one.
        /// </summary>
        public OfferingBowlDef[]? offeringBowls;

        /// <summary>Every <c>RuneStone</c> - 23 location prefabs carry one, 27 stones in all, against
        /// 4 prefabs with a discoverLabel (counted 2026-09-24; this said 24). It is NOT a source of
        /// place names: see <see cref="SeedLab.Contracts.Dump.RuneStoneDef"/> for why a stone names
        /// the location it DISCOVERS, not the one it stands in.</summary>
        public RuneStoneDef[]? runeStones;

        /// <summary>False on a file written before 2026-09-23, where the two arrays above do not exist
        /// and deserialise to null. An empty array and "not captured" are different facts, and reading
        /// the first as the second would say "this world has no bosses".</summary>
        public bool occupantsCaptured;

        /// <summary>Every <c>Teleport</c> - a dungeon's doors, and the only place the game keeps a
        /// dungeon's player-facing name (<c>m_enterText</c>). See <see cref="TeleportDef"/>.</summary>
        public TeleportDef[]? teleports;

        /// <summary>Every <c>Vegvisir</c>. Like a runestone it names the places it REVEALS, not its
        /// host. See <see cref="VegvisirDef"/>.</summary>
        public VegvisirDef[]? vegvisirs;

        /// <summary>
        /// True when <see cref="teleports"/> and <see cref="vegvisirs"/> were read. False on a file
        /// written before 2026-09-24, where both are absent, and false when their walk threw.
        ///
        /// <para>A flag of their own rather than a widening of <see cref="occupantsCaptured"/>: the
        /// two were added after the occupant arrays had been verified against the game, and a failure
        /// in the new walk must not take the boss and trader captures down with it.</para>
        /// </summary>
        public bool waymarksCaptured;

        /// <summary>The anchor's <c>transform.position</c> at dump time (the asset root for a location,
        /// the <c>Room</c> component's transform for a room).</summary>
        public Vec3Def? rootPosition;

        /// <summary>The anchor's <c>transform.rotation</c> at dump time.</summary>
        public QuatDef? rootRotation;

        /// <summary>The asset ROOT's <c>transform.localScale</c>. <c>SpawnLocation</c> zeroes the root's
        /// position and rotation before the Randomize loop but leaves the SCALE alone, which is why
        /// <see cref="RandomSpawnDef.prefabPosition"/> is computed without dividing by it.</summary>
        public Vec3Def? rootLocalScale;

        /// <summary>True when <see cref="rootPosition"/> is exactly (0,0,0) and
        /// <see cref="rootRotation"/> exactly the identity. When true,
        /// <see cref="RandomSpawnDef.prefabPosition"/> is bit-identical to the child's raw
        /// <c>transform.position</c>, because the subtraction and the identity quaternion rotation are
        /// both exact. When false it is a computed value and one ulp may differ.</summary>
        public bool rootAtIdentity;

        // ---- the seeded stream -------------------------------------------------------------------

        /// <summary>Length of <see cref="randomSpawns"/>: the number of draws the RandomSpawn phase
        /// spends, and the index of the first RandomObject draw.</summary>
        public int randomSpawnCount;

        /// <summary>How many RandomSpawns the prefab CONTAINS, inactive included, <b>excluding one
        /// mounted on the root transform</b> - which is what <c>Utils.GetEnabledComponentsInChildren</c>
        /// excludes too (<c>componentsInChildren[i].transform == root.transform</c>). Comparing like
        /// with like matters: before 2026-09-23 this counted the root one and every clean prefab with a
        /// root RandomSpawn was reported as session-drifted. Larger than
        /// <see cref="randomSpawnCount"/> now means some RandomSpawns really were inactive at dump
        /// time. See <see cref="warnings"/>.</summary>
        public int randomSpawnCountAll;

        public int randomObjectCount;
        public int randomObjectCountAll;

        /// <summary><c>randomSpawnCount + randomObjectCount</c>: the total number of draws the spawn
        /// routine takes from the seeded stream before it instantiates anything.</summary>
        public int seededDrawCount;

        /// <summary>Length of <c>Utils.GetEnabledComponentsInChildren&lt;ZNetView&gt;(asset)</c>. The
        /// objects that actually get instantiated. It consumes no draws, but a port that replays the
        /// stream needs to know the phase boundary is after the RandomObjects, not after these.</summary>
        public int netViewCount;

        /// <summary>In <c>Utils.GetEnabledComponentsInChildren&lt;RandomSpawn&gt;(asset)</c> order.
        /// Entry N consumes draw N.</summary>
        public RandomSpawnDef[]? randomSpawns;

        /// <summary>In <c>Utils.GetEnabledComponentsInChildren&lt;RandomObject&gt;(asset)</c> order.
        /// Entry N consumes draw <c>randomSpawnCount + N</c>.</summary>
        public RandomObjectDef[]? randomObjects;

        // ---- containers ---------------------------------------------------------------------------

        public int containerCount;

        /// <summary>Every <c>Container</c> in the prefab's children, INCLUDING inactive ones, in
        /// <c>GetComponentsInChildren&lt;Container&gt;(includeInactive: true)</c> order. Inactive ones
        /// are included on purpose: a chest behind a <c>RandomSpawn</c> that happens to be switched off
        /// on the shared asset at dump time is still a chest that CAN appear.</summary>
        public ContainerDef[]? containers;

        // ---- the flat name index --------------------------------------------------------------------

        /// <summary>
        /// The asset ROOT GameObject's own name, VERBATIM. It is deliberately NOT in
        /// <see cref="childNames"/>, which is documented as excluding the root - but it still has to be
        /// searchable, because a sought name can BE a room or location prefab rather than something
        /// inside one. Before this field existed the child walk skipped the root
        /// (<c>if (t == root) continue;</c>) and a search for a prefab's own name was a guaranteed
        /// false negative. Location names were reachable through the location table; ROOM names were
        /// reachable nowhere at all.
        /// </summary>
        public string? rootName;

        /// <summary><c>Utils.GetPrefabName(rootName)</c> - the identity the search matches on.</summary>
        public string? rootNormalizedName;

        /// <summary><c>rootNormalizedName.GetStableHashCode()</c>, or 0.</summary>
        public int rootNormalizedHash;

        /// <summary>Total number of <c>Transform</c>s under the root, inactive included, the root
        /// itself excluded. A size and cost figure, and a tripwire for a prefab that failed to load
        /// its children.</summary>
        public int childTransformCount;

        /// <summary>Every DISTINCT child GameObject name in the prefab, VERBATIM, inactive included,
        /// sorted ordinal. Raw <c>GameObject.name</c>, so a child authored as <c>piece_maypole (1)</c>
        /// appears here under that exact string. <b>For identity questions use
        /// <see cref="prefabNames"/> instead</b> - the game's own identity for an object is
        /// <c>Utils.GetPrefabName(name)</c>, and an exact-match query against this array is a false
        /// negative for every duplicated child.</summary>
        public ChildNameDef[]? childNames;

        /// <summary>
        /// The same index keyed by <c>Utils.GetPrefabName(name)</c>: the name truncated at the first
        /// <c>'('</c> or <c>' '</c> (<c>Utils.extraCharacters = { '(', ' ' }</c>, verified from
        /// assembly_utils, 2026-09-23). This is the game's own identity for a GameObject - what
        /// <c>ZNetScene</c>, the ZDO prefab hash and every prefab lookup key on - so <b>this is the
        /// array a "does this prefab contain X" question must be asked of</b>. Each entry keeps the raw
        /// spellings it merged in <see cref="PrefabNameDef.rawNames"/>, so nothing is hidden.
        /// </summary>
        public PrefabNameDef[]? prefabNames;

        /// <summary>Anything the walk noticed about THIS prefab that a reader must not miss - an
        /// enabled-vs-present component count mismatch, a null <c>m_OffObject</c> reference, a drop
        /// table entry with no item, a position the game will not actually read. Empty array, never
        /// null, when the prefab was walked cleanly.</summary>
        public string[]? warnings;
    }

    /// <summary>
    /// <c>locationchildren.json</c> - what lives INSIDE each location prefab, captured by the same
    /// prefab walk that fills <see cref="LocationPrefabsFile"/>.
    ///
    /// <b>Why this file exists.</b> <c>ZoneSystem.SpawnLocation</c> (assembly_valheim, 1.0.15) opens a
    /// seeded RNG stream per location instance and spends it on the prefab's own children:
    /// <code>
    /// ZNetView[]     netViews   = Utils.GetEnabledComponentsInChildren&lt;ZNetView&gt;(location.m_prefab.Asset);
    /// RandomObject[] randObjs   = Utils.GetEnabledComponentsInChildren&lt;RandomObject&gt;(location.m_prefab.Asset);
    /// RandomSpawn[]  randSpawns = Utils.GetEnabledComponentsInChildren&lt;RandomSpawn&gt;(location.m_prefab.Asset);
    /// foreach (RandomSpawn s in randSpawns) s.Prepare();
    /// ...
    /// Random.InitState(seed);                       // seed = worldSeed + zone.x*4271 + zone.y*9187
    /// foreach (RandomSpawn o in randSpawns)  o.Randomize(pos + rot * o.transform.position, location);
    /// foreach (RandomObject o in randObjs)   o.Randomize(pos + rot * o.transform.position, location);
    /// </code>
    /// Each <c>RandomSpawn.Randomize</c> spends exactly one <c>Random.Range(0f, 100f)</c>, and each
    /// <c>RandomObject.Randomize</c> spends exactly one <c>Random.Range(0f, totalWeight)</c> - the
    /// <c>GetWeightedObject()</c> call is the FIRST statement of <c>RandomObject.Randomize</c> and runs
    /// before every gate, so a gated-off entry still consumes its draw. <b>Array order is therefore the
    /// data</b>: entry <c>index</c> N consumes draw N. Both arrays are written here in exactly the
    /// order <c>Utils.GetEnabledComponentsInChildren</c> returned them, from the same call the game
    /// makes, so the order is identical by construction rather than by assumption.
    ///
    /// <b>This file covers LOCATION prefabs only.</b> Dungeon and camp interiors are built from ROOM
    /// prefabs, which live in <c>DungeonDB</c> and never appear inside a location prefab - see
    /// <see cref="RoomChildrenFile"/>. A name absent from both, and from the location and vegetation
    /// tables, is reported as NOT FOUND in <see cref="SearchFile"/> rather than being silently missing.
    ///
    /// <b>A drop table is a probability, never an inventory.</b> Everything in
    /// <see cref="DropTableDef"/> answers "what CAN this container contain", and nothing in this dump
    /// answers "what DOES a given chest contain" - the contents are rolled at spawn time from the
    /// ambient, unseeded stream (<c>Container.Awake</c> -&gt; <c>AddDefaultItems</c>), not from the
    /// location's seeded stream. Any tool reading this file must phrase its answers that way.
    /// </summary>
    public sealed class LocationChildrenFile
    {
        public string? stamp;
        public int schema;

        /// <summary>Number of entries in <see cref="locations"/>. Same prefab set, same order, as
        /// <see cref="LocationPrefabsFile.prefabs"/> - both are filled by one pass of the walk.</summary>
        public int count;

        public int loadedCount;
        public int failedCount;

        /// <summary>Sum of <see cref="InteriorDef.randomSpawnCount"/> over every entry.</summary>
        public int totalRandomSpawns;

        /// <summary>Sum of <see cref="InteriorDef.randomObjectCount"/> over every entry.</summary>
        public int totalRandomObjects;

        /// <summary>Sum of <see cref="InteriorDef.containerCount"/> over every entry.</summary>
        public int totalContainers;

        /// <summary>
        /// The cap the writer applied to the per-entry name lists
        /// (<see cref="RandomSpawnDef.subtreeChildNames"/>,
        /// <see cref="RandomSpawnDef.offObjectChildNames"/>,
        /// <see cref="RandomObjectDef.subtreeChildNames"/>). Each of those has a sibling
        /// <c>...Count</c> holding the true number, so truncation is always visible rather than
        /// silent, and a reader can tell "this entry has 12 distinct child names" from "this entry has
        /// at least 64".
        ///
        /// It exists because those lists are the one part of this file with no natural bound: a
        /// RandomSpawn gating a whole building repeats that building's names, once per RandomSpawn,
        /// across 186 prefabs. Nothing load-bearing is capped - the whole-prefab indices
        /// <see cref="InteriorDef.childNames"/> and <see cref="InteriorDef.prefabNames"/> and
        /// <see cref="RandomSpawnDef.activatedNetViewNames"/> are all complete.
        /// </summary>
        public int maxNamesPerEntry;

        public LocationChildrenDef[]? locations;
    }

    /// <summary>One location prefab's interior, as the game's own component queries return it.</summary>
    public sealed class LocationChildrenDef : InteriorDef
    {
        // ---- the Location component's biome cache ------------------------------------------------

        public bool hasLocationComponent;

        /// <summary>
        /// <c>Location.m_biome</c> as read off the SHARED PREFAB ASSET, which is the object
        /// <c>SpawnLocation</c> hands to every <c>Randomize</c> call.
        ///
        /// <b>This field is a session-wide cache, not only authored data.</b>
        /// <c>RandomSpawn.Randomize</c> and <c>RandomObject.Randomize</c> are the only writers
        /// (verified by IL, 2026-09-23): when a gated entry finds <c>m_biome == None</c> they set it to
        /// <c>WorldGenerator.instance.GetBiome(pos)</c> and nothing ever clears it. So the FIRST
        /// biome-gated entry of the FIRST instance of this prefab to spawn in a session fixes the value
        /// for every later instance anywhere in the world.
        ///
        /// On the 2026-09-22 dump 181 of 186 prefabs read 0 (<c>None</c>) - including all of
        /// WoodHouse1..13 - and the five non-zero ones are <c>AncientUpgradeStation</c> (Mountain) and
        /// three <c>DN_*</c> plus <c>NorthMemorialPlace</c> (DeepNorth), none of which could have
        /// spawned near the dump's Meadows spawn point. That is the evidence that non-zero here means
        /// AUTHORED and zero means "not yet sampled". A dump taken after a long session in an unusual
        /// place could still catch a cached value; compare against an older dump if it matters.
        /// </summary>
        public int locationBiome;

        // ---- the custom interior transform ---------------------------------------------------------

        /// <summary>
        /// <c>SpawnLocation</c>'s local <c>flag</c>, reproduced exactly:
        /// <c>m_useCustomInteriorTransform &amp;&amp; m_interiorTransform != null &amp;&amp;
        /// m_generator != null</c> on the ROOT <c>Location</c> component. Nine prefabs in 1.0.15 set
        /// <c>m_useCustomInteriorTransform</c>.
        ///
        /// <b>When this is true, some <see cref="RandomSpawnDef.prefabPosition"/> values in this entry
        /// are not what the game reads.</b> Between <c>Random.InitState(seed)</c> and the Randomize
        /// loop, <c>SpawnLocation</c> mutates the shared asset:
        /// <code>
        /// component.m_generator.transform.localPosition = Vector3.zero;
        /// component.m_interiorTransform.localPosition   = &lt;derived from the zone centre, the
        ///                                                  instance position and its rotation&gt;;
        /// component.m_interiorTransform.localRotation   = Quaternion.Inverse(rot);
        /// </code>
        /// Anything under either transform is therefore evaluated at a moved position - and the
        /// interior one is INSTANCE-DEPENDENT, so no dump can state it. The affected entries carry
        /// <see cref="RandomSpawnDef.underGeneratorTransform"/> /
        /// <see cref="RandomSpawnDef.underInteriorTransform"/> and
        /// <see cref="RandomSpawnDef.prefabPositionIsInstanceDependent"/>, and this entry's
        /// <see cref="InteriorDef.warnings"/> names the count. Draw ORDER and draw COUNT are unaffected:
        /// the mutation moves positions, not the stream.
        /// </summary>
        public bool customInteriorTransformActive;

        /// <summary><c>Location.m_useCustomInteriorTransform</c> on its own, before the null checks that
        /// make <see cref="customInteriorTransformActive"/>. A true here with a false there means the
        /// prefab is authored for the custom transform but is missing <c>m_interiorTransform</c> or
        /// <c>m_generator</c>, which the game would not act on.</summary>
        public bool useCustomInteriorTransform;

        /// <summary>Path of <c>Location.m_interiorTransform</c> from the root, or null.</summary>
        public string? interiorTransformPath;

        /// <summary>Path of <c>Location.m_generator</c>'s transform from the root, or null.</summary>
        public string? generatorTransformPath;

        /// <summary>How many entries in <see cref="InteriorDef.randomSpawns"/> +
        /// <see cref="InteriorDef.randomObjects"/> carry
        /// <see cref="RandomSpawnDef.prefabPositionIsInstanceDependent"/>. 0 on every prefab where
        /// <see cref="customInteriorTransformActive"/> is false.</summary>
        public int instanceDependentPositionCount;
    }

    /// <summary>
    /// One <c>RandomSpawn</c>, in the game's own array order. It consumes exactly one
    /// <c>Random.Range(0f, 100f)</c> draw and spawns when
    /// <c>draw &lt;= chanceToSpawn</c> AND every gate below passes.
    /// </summary>
    public sealed class RandomSpawnDef
    {
        /// <summary>Position in <c>Utils.GetEnabledComponentsInChildren&lt;RandomSpawn&gt;(asset)</c>,
        /// and therefore which draw of the seeded stream this entry consumes (0-based).</summary>
        public int index;

        /// <summary>Hierarchy path from the prefab root, '/' separated, root excluded. Raw names.</summary>
        public string? path;

        /// <summary>The GameObject's own name, VERBATIM. For a RandomSpawn that directly gates a single
        /// piece this is usually the piece's prefab name - but it can also be <c>piece_maypole (1)</c>,
        /// which is why <see cref="normalizedName"/> exists.</summary>
        public string? name;

        /// <summary><c>Utils.GetPrefabName(name)</c>: <see cref="name"/> truncated at the first
        /// <c>'('</c> or <c>' '</c>. The game's own identity for this object, and the form every index
        /// and every search in this dump matches on.</summary>
        public string? normalizedName;

        /// <summary><c>normalizedName.GetStableHashCode()</c>, 0 when the name is null.</summary>
        public int normalizedHash;

        /// <summary><c>transform.localPosition</c> - relative to the IMMEDIATE parent, which is not in
        /// general the prefab root.</summary>
        public Vec3Def? localPosition;

        /// <summary>
        /// What the spawn routine reads as this object's position.
        ///
        /// For a LOCATION: <c>SpawnLocation</c> gets it by temporarily setting the asset root to
        /// <c>position = Vector3.zero, rotation = Quaternion.identity</c> and leaving the scale alone,
        /// then reading <c>obj.gameObject.transform.position</c>; the instance position is
        /// <c>pos + rot * prefabPosition</c>.
        ///
        /// For a ROOM: <c>PlaceRoom</c> computes
        /// <c>Inverse(room.transform.rotation) * (child.position - room.transform.position)</c> and the
        /// instance position is likewise <c>pos + rot * prefabPosition</c>.
        ///
        /// The dumper must not mutate a shared asset, so it computes the identical quantity as
        /// <c>Inverse(anchorRotation) * (transform.position - anchorPosition)</c> - NOT
        /// <c>InverseTransformPoint</c>, which would also divide by the root scale the game keeps.
        /// See <see cref="InteriorDef.rootAtIdentity"/> for when this is bit-exact, and
        /// <see cref="prefabPositionIsInstanceDependent"/> for when it is not what the game reads at
        /// all.
        /// </summary>
        public Vec3Def? prefabPosition;

        /// <summary>Raw <c>transform.position</c> at dump time, before the anchor-relative correction.
        /// Equal to <see cref="prefabPosition"/> whenever the anchor is at the identity.</summary>
        public Vec3Def? dumpWorldPosition;

        /// <summary>True when this object sits under <c>Location.m_generator</c>'s transform on a prefab
        /// whose <see cref="LocationChildrenDef.customInteriorTransformActive"/> is true.
        /// <c>SpawnLocation</c> sets that transform's <c>localPosition</c> to <c>Vector3.zero</c> before
        /// the Randomize loop, so the position the game reads is this one shifted by the generator's own
        /// authored offset. Always false for a room.</summary>
        public bool underGeneratorTransform;

        /// <summary>True when this object sits under <c>Location.m_interiorTransform</c> on a prefab
        /// whose <see cref="LocationChildrenDef.customInteriorTransformActive"/> is true. That transform
        /// is moved AND rotated per instance before the Randomize loop, from the zone centre, the
        /// instance position and the instance rotation - so the position the game reads cannot be stated
        /// by any dump. Always false for a room.</summary>
        public bool underInteriorTransform;

        /// <summary><c>underGeneratorTransform || underInteriorTransform</c>: this entry's
        /// <see cref="prefabPosition"/> is <b>not</b> the value the game reads. Its draw index, chance
        /// and gates are unaffected; only the position is. An offline replay must treat the elevation
        /// and lava gates of such an entry as unknown rather than as passing.</summary>
        public bool prefabPositionIsInstanceDependent;

        /// <summary><c>m_chanceToSpawn</c>, 0..100. Compared with <c>&lt;=</c> against the draw, so its
        /// last bit can decide the outcome - use the sibling "bits" value, not this decimal.</summary>
        public float chanceToSpawn;

        /// <summary><c>m_requireBiome</c> as the raw <c>Heightmap.Biome</c> bitmask. 0 (<c>None</c>)
        /// means no biome gate. Non-zero means the gate consults the SHARED
        /// <see cref="LocationChildrenDef.locationBiome"/> cache - read that field's remarks before
        /// treating this as a per-instance test. <b>Inert inside a room</b>: <c>PlaceRoom</c> passes a
        /// null <c>Location</c>, and the gate is <c>loc != null &amp;&amp; m_requireBiome != None</c>.</summary>
        public int requireBiome;

        /// <summary><c>m_minElevation</c>. The gate is <c>pos2.y &lt; (float)m_minElevation</c>, where
        /// <c>pos2.y</c> is the ground height <c>ZoneSystem.GetGroundData</c> raycast at the location's
        /// position, plus the child's own y - NOT the stored <c>LocationInstance.m_position.y</c>.
        /// Defaults are -10000 / 10000, i.e. no gate.</summary>
        public int minElevation;

        public int maxElevation;

        /// <summary><c>m_notInLava</c>: gated by <c>ZoneSystem.IsLavaPreHeightmap(pos2)</c>.</summary>
        public bool notInLava;

        /// <summary><c>m_dungeonRequireTheme</c> as the raw <c>Room.Theme</c> bitmask.
        /// <b>Inert inside a location</b>: <c>SpawnLocation</c> calls <c>Randomize(pos2, component)</c>
        /// with the dungeon argument defaulted to null and the gate is
        /// <c>dg != null &amp;&amp; m_dungeonRequireTheme != None</c>. <b>Live inside a room</b>:
        /// <c>PlaceRoom</c> calls <c>Randomize(pos, null, this)</c>, so the gate tests
        /// <c>dungeonGenerator.m_themes.HasFlag(m_dungeonRequireTheme)</c>.</summary>
        public int dungeonRequireTheme;

        /// <summary><c>gameObject.activeSelf</c> at dump time.</summary>
        public bool activeSelf;

        /// <summary><c>Utils.IsEnabledInheirarcy(gameObject, asset)</c> - the game's OWN test, the one
        /// <c>Utils.GetEnabledComponentsInChildren</c> filters with: every GameObject from here up to
        /// the prefab root has <c>activeSelf</c> true. Unity's <c>activeInHierarchy</c> is not used
        /// because a prefab asset is not in a scene, where it is false for everything.</summary>
        public bool enabledInHierarchy;

        /// <summary>True when this GameObject has its own <c>ZNetView</c>.
        /// <c>RandomSpawn.SetSpawned(true)</c> only re-activates the object when <c>m_nview == null</c>,
        /// so this changes what "spawned" does to the hierarchy.</summary>
        public bool hasNetView;

        // ---- what it toggles ------------------------------------------------------------------------

        /// <summary><c>m_OffObject.name</c>, or null when the reference is null. The off-object is set
        /// ACTIVE when the RandomSpawn does NOT spawn, and inactive when it does - it is the
        /// "instead of" variant.</summary>
        public string? offObjectName;

        /// <summary>Hierarchy path of <c>m_OffObject</c> from the prefab root, or null.</summary>
        public string? offObjectPath;

        /// <summary><c>m_OffObject.activeSelf</c> at dump time. <b>Expect false on a prefab that has
        /// already been spawned this session</b>: both <c>SpawnLocation</c> and <c>PlaceRoom</c> end by
        /// calling <c>Reset()</c> on every RandomSpawn, which is <c>SetSpawned(true)</c>, which sets the
        /// off-object INACTIVE and never puts it back. That is a property of the shared asset, not of
        /// the authored prefab.</summary>
        public bool offObjectActiveSelf;

        /// <summary>Number of distinct names under <c>m_OffObject</c> before
        /// <see cref="LocationChildrenFile.maxNamesPerEntry"/> truncation. 0 when there is no
        /// off-object.</summary>
        public int offObjectChildNameCount;

        /// <summary>Distinct NORMALISED names of every GameObject under <c>m_OffObject</c> (itself
        /// included, inactive included), sorted ordinal, truncated to
        /// <see cref="LocationChildrenFile.maxNamesPerEntry"/>. Empty when there is no
        /// off-object.</summary>
        public string[]? offObjectChildNames;

        /// <summary>How many <c>ZNetView</c>s <c>Prepare()</c> would put in <c>m_childNetViews</c> -
        /// the true length, before the names below are de-duplicated. Never truncated.</summary>
        public int activatedNetViewCount;

        /// <summary>
        /// The <c>ZNetView</c>s this entry switches on when it spawns, by NORMALISED GameObject name
        /// (<c>Utils.GetPrefabName</c>):
        /// <c>GetComponentsInChildren&lt;ZNetView&gt;(true)</c> over this object's subtree, filtered by
        /// <c>Utils.IsEnabledInheirarcy(child, this)</c> - exactly <c>RandomSpawn.Prepare()</c>'s query.
        /// <b>This is the list that names the piece.</b> The dumper reproduces the query by reading
        /// only; it never calls <c>Prepare()</c>, which would write the component's private state on a
        /// shared asset.
        ///
        /// <b>Distinct and sorted ordinal, not in Prepare's order.</b> Prepare's order carries no
        /// information - it builds the list only to <c>SetActive</c> every entry together, and nothing
        /// in it touches the RNG - while a building's 300-piece list repeated verbatim for every
        /// RandomSpawn is most of this file's size. <see cref="activatedNetViewCount"/> keeps the true
        /// length. Never truncated.
        /// </summary>
        public string[]? activatedNetViewNames;

        /// <summary>Number of distinct normalised names in this entry's subtree before
        /// <see cref="LocationChildrenFile.maxNamesPerEntry"/> truncation.</summary>
        public int subtreeChildNameCount;

        /// <summary>Distinct NORMALISED names of every GameObject in this entry's subtree, itself
        /// included, inactive included, sorted ordinal, truncated to
        /// <see cref="LocationChildrenFile.maxNamesPerEntry"/>. A safety net broader than
        /// <see cref="activatedNetViewNames"/> - it also catches a piece whose ZNetView is nested or
        /// switched off on the shared asset. Because it can be truncated, the authoritative
        /// whole-prefab answer to "does this prefab contain X" is
        /// <see cref="InteriorDef.prefabNames"/>, which never is.</summary>
        public string[]? subtreeChildNames;

        /// <summary>Index in <see cref="InteriorDef.randomSpawns"/> of the nearest ANCESTOR
        /// that is itself a RandomSpawn in the same array, or -1. A nested RandomSpawn only matters
        /// when its ancestor spawned, so its own draw is spent regardless but its effect is
        /// conditional.</summary>
        public int parentRandomSpawnIndex;

        /// <summary>True when this entry sits inside some other entry's <c>m_OffObject</c> subtree: it
        /// is part of the "not spawned" variant of that other entry.</summary>
        public bool underAnOffObject;
    }

    /// <summary>
    /// One <c>RandomObject</c>. It consumes exactly one <c>Random.Range(0f, totalWeight)</c> draw, and
    /// it does so BEFORE any gate is evaluated (<c>GetWeightedObject()</c> is the first statement of
    /// <c>RandomObject.Randomize</c>), so the draw is spent even when the entry is gated off.
    /// </summary>
    public sealed class RandomObjectDef
    {
        /// <summary>Position in <c>Utils.GetEnabledComponentsInChildren&lt;RandomObject&gt;(asset)</c>.
        /// It consumes draw <c>randomSpawnCount + index</c> of the seeded stream.</summary>
        public int index;

        public string? path;

        /// <summary>The GameObject's own name, VERBATIM.</summary>
        public string? name;

        /// <summary><c>Utils.GetPrefabName(name)</c>. See <see cref="RandomSpawnDef.normalizedName"/>.</summary>
        public string? normalizedName;

        public int normalizedHash;

        public Vec3Def? localPosition;

        /// <summary>See <see cref="RandomSpawnDef.prefabPosition"/>.</summary>
        public Vec3Def? prefabPosition;

        public Vec3Def? dumpWorldPosition;

        /// <summary>See <see cref="RandomSpawnDef.underGeneratorTransform"/>.</summary>
        public bool underGeneratorTransform;

        /// <summary>See <see cref="RandomSpawnDef.underInteriorTransform"/>.</summary>
        public bool underInteriorTransform;

        /// <summary>See <see cref="RandomSpawnDef.prefabPositionIsInstanceDependent"/>.</summary>
        public bool prefabPositionIsInstanceDependent;

        /// <summary>Sum of every entry's <c>m_weight</c>, including entries whose <c>m_object</c> is
        /// null. This is the exclusive upper bound of the draw: <c>Random.Range(0f, totalWeight)</c>.
        /// Read its bits, not this decimal - the winner is picked with <c>draw &lt;= runningSum</c>.</summary>
        public float totalWeight;

        /// <summary>In <c>m_objects</c> list order, which is the order the running sum walks.</summary>
        public RandomObjectEntryDef[]? objects;

        public int requireBiome;
        public int minElevation;
        public int maxElevation;
        public bool notInLava;
        public int dungeonRequireTheme;

        public bool activeSelf;

        /// <summary>See <see cref="RandomSpawnDef.enabledInHierarchy"/>.</summary>
        public bool enabledInHierarchy;

        /// <summary><c>RandomObject.SetSpawned</c> re-activates the object only when
        /// <c>GetComponent&lt;ZNetView&gt;() == null</c>.</summary>
        public bool hasNetView;

        /// <summary>See <see cref="RandomSpawnDef.subtreeChildNameCount"/>.</summary>
        public int subtreeChildNameCount;

        /// <summary>See <see cref="RandomSpawnDef.subtreeChildNames"/>.</summary>
        public string[]? subtreeChildNames;

        public int parentRandomSpawnIndex;
        public bool underAnOffObject;
    }

    /// <summary>One <c>RandomObject.ObjectEntry</c>.</summary>
    public sealed class RandomObjectEntryDef
    {
        /// <summary>Index in <c>m_objects</c>; the running sum is walked in this order.</summary>
        public int index;

        /// <summary><c>m_object.name</c> VERBATIM, or null when the reference is null. A null entry
        /// still adds its weight to the total and can still win the draw - the game then spawns
        /// nothing.</summary>
        public string? name;

        /// <summary><c>name.GetStableHashCode()</c>, or 0 when <see cref="name"/> is null. The RAW
        /// name's hash, kept for continuity with earlier dumps.</summary>
        public int hash;

        /// <summary><c>Utils.GetPrefabName(name)</c>, or null. What a search matches on.</summary>
        public string? normalizedName;

        /// <summary><c>normalizedName.GetStableHashCode()</c>, or 0.</summary>
        public int normalizedHash;

        public float weight;

        /// <summary><c>m_object.activeSelf</c> at dump time, or false when the reference is null.</summary>
        public bool activeSelf;

        // ---- what is INSIDE the option ---------------------------------------------------------------

        /// <summary>
        /// Every <c>Container</c> in the option prefab's own subtree, inactive included, with its full
        /// <see cref="ContainerDef.defaultItems"/> drop table.
        ///
        /// <b>Why this is here and not in the host's <see cref="InteriorDef.containers"/>.</b> A
        /// RandomObject option is a prefab REFERENCE, not a child, so it never appears in the host's
        /// transform walk and its chests were invisible to every earlier dump: a question like "which
        /// chest can roll this item" had no answer at all whenever the chest was a weighted option
        /// rather than a direct child. The reference is a plain <c>GameObject</c> field
        /// (<c>RandomObject/ObjectEntry.m_object</c>, verified against the shipped assembly_valheim on
        /// 2026-09-23), i.e. an ordinary Unity serialised reference that is already resident whenever
        /// the host prefab is - so reading it is a pure component query with no <c>Load()</c>, no
        /// <c>Release()</c> and no asset-loader bookkeeping.
        ///
        /// <b>The gate.</b> These containers exist only when this option WINS the RandomObject's single
        /// weighted draw, on top of whatever <see cref="ContainerDef.gatedByRandomSpawnIndex"/> says.
        /// </summary>
        public ContainerDef[]? containers;

        /// <summary>Length of <see cref="containers"/>.</summary>
        public int containerCount;

        /// <summary>Non-null when reading the option's subtree threw, with the reason. Part of the
        /// option was then NOT read and a "not found" is inconclusive because of it - the count of
        /// these is carried into <c>search.json</c>'s coverage.</summary>
        public string? scanError;

        /// <summary>
        /// How many <c>RandomObject</c>s <b>that have at least one non-null option of their own</b> are
        /// inside this option's subtree, inactive included. (An option-less RandomObject hides nothing
        /// and is not counted.)
        ///
        /// <b>A known, deliberate boundary</b>: options are resolved exactly ONE level deep, so nothing
        /// that exists only behind a RandomObject inside a RandomObject option is in this dump.
        /// Recorded rather than recursed because the walk builds the whole JSON document in memory
        /// inside one frame of a live game. The total is reported in <c>search.json</c>'s
        /// <c>standingLimitations</c> - not <c>notSearched</c>, because it is non-zero in essentially
        /// every run and would otherwise mark every verdict in every dump inconclusive.
        /// </summary>
        public int nestedRandomObjectCount;
    }

    /// <summary>
    /// One <c>Container</c> found anywhere in the prefab's children.
    ///
    /// <b>A drop table says CAN, never DOES.</b> <see cref="defaultItems"/> is the pool the game rolls
    /// from when the container is first created; it is not the chest's contents. Two different items in
    /// the same table means either one can appear, not that both will.
    /// </summary>
    public sealed class ContainerDef
    {
        /// <summary>Hierarchy path from the prefab root.</summary>
        public string? path;

        /// <summary>The GameObject's name VERBATIM - usually the chest's own prefab name, such as
        /// <c>piece_chest_wood</c>.</summary>
        public string? name;

        /// <summary><c>name.GetStableHashCode()</c> of the RAW name.</summary>
        public int hash;

        /// <summary><c>Utils.GetPrefabName(name)</c>: the game's identity for the chest.</summary>
        public string? normalizedName;

        /// <summary><c>normalizedName.GetStableHashCode()</c>.</summary>
        public int normalizedHash;

        /// <summary><c>Container.m_name</c>: the localization token shown on the container, e.g.
        /// <c>$piece_chest</c>. Not the prefab name.</summary>
        public string? containerName;

        public int width;
        public int height;
        public bool autoDestroyEmpty;

        /// <summary><c>Container.m_privacy</c> as the raw <c>Container.PrivacySetting</c> integer.</summary>
        public int privacy;

        public bool checkGuardStone;

        public bool activeSelf;

        /// <summary>See <see cref="RandomSpawnDef.enabledInHierarchy"/>.</summary>
        public bool enabledInHierarchy;

        /// <summary>Index in <see cref="InteriorDef.randomSpawns"/> of the nearest
        /// ancestor-or-self that is a RandomSpawn in that array, or -1 when the container is
        /// unconditional. This is the link that turns "WoodHouse7 has a chest" into "WoodHouse7 has a
        /// chest when draw N passes".</summary>
        public int gatedByRandomSpawnIndex;

        /// <summary>Path of that RandomSpawn, or null.</summary>
        public string? gatedByRandomSpawnPath;

        /// <summary>True when this container sits inside some RandomSpawn's <c>m_OffObject</c>, i.e. it
        /// appears only when that RandomSpawn does NOT spawn.</summary>
        public bool underAnOffObject;

        /// <summary><c>Container.m_defaultItems</c>. Never null for a loaded container - the field is
        /// initialised to an empty <c>DropTable</c> in the class - but a table with no
        /// <see cref="DropTableDef.drops"/> rolls nothing.</summary>
        public DropTableDef? defaultItems;
    }

    /// <summary>
    /// A <c>DropTable</c>. The roll (<c>DropTable.GetDropListItems</c>) is:
    /// <c>if (Random.value &gt; m_dropChance) return nothing;</c> then
    /// <c>Random.Range(m_dropMin, m_dropMax + 1)</c> picks how many times to spin, and each spin takes
    /// <c>Random.Range(0f, totalWeight)</c> and walks the running sum with <c>draw &lt;= runningSum</c>.
    /// With <c>m_oneOfEach</c> the winner is removed from the pool and the total reduced.
    ///
    /// <b>None of those draws come from the location's seeded stream</b>, so this file cannot predict a
    /// given chest's contents. It states what the chest CAN contain.
    /// </summary>
    public sealed class DropTableDef
    {
        public int dropMin;
        public int dropMax;

        /// <summary><c>m_dropChance</c>, 0..1, tested as <c>Random.value &gt; m_dropChance</c>.</summary>
        public float dropChance;

        public bool oneOfEach;

        /// <summary>Length of <see cref="drops"/>.</summary>
        public int dropCount;

        /// <summary>Sum of every <see cref="DropDataDef.weight"/>, the exclusive upper bound of a
        /// spin.</summary>
        public float totalWeight;

        /// <summary>In <c>m_drops</c> list order, which is the order the running sum walks.</summary>
        public DropDataDef[]? drops;
    }

    /// <summary>One <c>DropTable.DropData</c> (a struct, so an entry always exists even when its
    /// <c>m_item</c> reference is null).</summary>
    public sealed class DropDataDef
    {
        /// <summary>Index in <c>m_drops</c>.</summary>
        public int index;

        /// <summary><c>m_item.name</c> VERBATIM - the item PREFAB name, e.g. <c>AxeIron</c>, never a
        /// display string. Null when the reference is null, which is a data bug in the prefab and is
        /// reported in <see cref="InteriorDef.warnings"/>.</summary>
        public string? itemPrefabName;

        /// <summary><c>itemPrefabName.GetStableHashCode()</c>, or 0 when the name is null. The same
        /// identity <c>ZDO</c>s and the location table use.</summary>
        public int itemPrefabHash;

        /// <summary><c>Utils.GetPrefabName(itemPrefabName)</c>, or null.</summary>
        public string? normalizedName;

        /// <summary><c>normalizedName.GetStableHashCode()</c>, or 0.</summary>
        public int normalizedHash;

        /// <summary>
        /// <c>ItemDrop.m_itemData.m_shared.m_name</c> - the item's localization token, e.g.
        /// <c>$item_axehead1</c>. Null when the drop's prefab carries no <c>ItemDrop</c>, which is a
        /// data bug worth seeing rather than hiding.
        ///
        /// <para>This is what lets a search say "a house whose chest can hold a Curious Axe Head"
        /// instead of "WoodHouse6". <c>AxeHead1</c> and <c>AxeHead2</c> are indistinguishable as prefab
        /// names and are two different named items to a player.</para>
        /// </summary>
        public string? itemNameToken;

        /// <summary>That token resolved through <c>localization.json</c>, or null when it has no entry.</summary>
        public string? itemLocalizedName;

        public int stackMin;
        public int stackMax;

        public float weight;

        /// <summary><c>m_dontScale</c>: when false the stack size is put through
        /// <c>Game.ScaleDrops</c> / the resource rate, so the stack range here is the unscaled one.</summary>
        public bool dontScale;
    }

    /// <summary>One distinct RAW child GameObject name inside a prefab.</summary>
    public sealed class ChildNameDef
    {
        /// <summary>The verbatim <c>GameObject.name</c>.</summary>
        public string? name;

        /// <summary><c>name.GetStableHashCode()</c> of the raw name.</summary>
        public int hash;

        /// <summary><c>Utils.GetPrefabName(name)</c> - the key this entry is grouped under in
        /// <see cref="InteriorDef.prefabNames"/>.</summary>
        public string? normalizedName;

        /// <summary>How many GameObjects in the prefab carry this raw name, inactive included.</summary>
        public int count;

        /// <summary>How many of those carry a <c>ZNetView</c> - i.e. would be instantiated as real
        /// world objects rather than being pure scenery or a grouping node.</summary>
        public int netViewCount;

        /// <summary>How many of those were enabled all the way up to the root at dump time
        /// (<c>Utils.IsEnabledInheirarcy</c>). Fewer than <see cref="count"/> means some copies sit
        /// behind a switched-off RandomSpawn or off-object.</summary>
        public int enabledCount;
    }

    /// <summary>
    /// One distinct NORMALISED child name inside a prefab: <c>Utils.GetPrefabName(GameObject.name)</c>,
    /// i.e. the name truncated at the first <c>'('</c> or <c>' '</c>. This is the game's own identity
    /// for an object, so this - not <see cref="ChildNameDef"/> - is the index an
    /// "is X in here" question must be asked of.
    /// </summary>
    public sealed class PrefabNameDef
    {
        /// <summary>The normalised name.</summary>
        public string? name;

        /// <summary><c>name.GetStableHashCode()</c>.</summary>
        public int hash;

        /// <summary>How many GameObjects normalise to this name, inactive included.</summary>
        public int count;

        /// <summary>How many of those carry a <c>ZNetView</c>.</summary>
        public int netViewCount;

        /// <summary>How many of those were enabled to the root at dump time.</summary>
        public int enabledCount;

        /// <summary>How many DISTINCT raw spellings were merged into this entry.</summary>
        public int rawNameCount;

        /// <summary>The distinct raw spellings, sorted ordinal - so the normalisation never hides what
        /// was actually in the prefab. Truncated to
        /// <see cref="LocationChildrenFile.maxNamesPerEntry"/>; <see cref="rawNameCount"/> keeps the
        /// true number.</summary>
        public string[]? rawNames;
    }

    /// <summary>
    /// A <c>UnityEngine.Quaternion</c>. Lives here rather than in <c>Primitives.cs</c> because this is
    /// the only dump file that needs one, and the file it belongs to is the one that explains why.
    /// </summary>
    public sealed class QuatDef
    {
        public float x;
        public float y;
        public float z;
        public float w;
    }
}
