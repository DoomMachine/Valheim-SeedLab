using System.Collections.Generic;
using System.Globalization;

namespace SeedLab.Saves
{
    /// <summary>
    /// One row of <c>ZoneSystem.m_locationInstances</c> as the world save stores it: the location
    /// prefab's stable hash, its position at <b>full float32 precision</b>, and whether the zone has
    /// actually been visited and built.
    /// <para>
    /// <b>Why the position matters so much.</b> In
    /// <c>ZoneSystem.GenerateLocationsTimeSliced</c> the candidate point gets
    /// <c>randomPointInZone.y = WorldGenerator.instance.GetHeight(x, z, out var mask);</c> (line 2073)
    /// and <c>RegisterLocation</c> stores the <c>Vector3</c> unchanged; <c>PlaceLocations</c> later
    /// copies it into a local and only sets <c>m_placed</c> - it never writes a new position back
    /// (lines 2198-2229). So every stored <c>(x, y, z)</c> satisfies
    /// <c>y == WorldGenerator.GetHeight(x, z)</c> exactly, as float32, and needs no reproduction of
    /// the placement RNG. That is acceptance test T5: 12 314 full-precision height samples spread
    /// from radius 70.6 m to 10 292.7 m (05-validation.md section 3.2).
    /// </para>
    /// </summary>
    public sealed class LocationInstance
    {
        public LocationInstance(int prefabHash, float x, float y, float z, bool placed)
        {
            PrefabHash = prefabHash;
            X = x;
            Y = y;
            Z = z;
            Placed = placed;
        }

        /// <summary>
        /// <c>item.m_location.m_prefabName.GetStableHashCode()</c> - the same string hash as the seed.
        /// The names are not in the save; recover them from the dumper's <c>ZoneSystem.m_locations</c>
        /// table, or offline from the asset manifests (05-validation.md section 3.4).
        /// </summary>
        public int PrefabHash { get; }

        public float X { get; }

        /// <summary>The terrain height the generator produced at (X, Z). See the class remarks.</summary>
        public float Y { get; }

        public float Z { get; }

        /// <summary>
        /// <c>m_placed</c> - whether the location has actually been instantiated. Only 44 of 12 314 in
        /// <c>asdasdasd</c>: it tells you where players have been, not where things are.
        /// </summary>
        public bool Placed { get; }

        public Vec3f Position => new Vec3f(X, Y, Z);

        /// <summary>
        /// The 64 m zone this instance sits in.
        /// <code>
        /// // ZoneSystem.GetZone (decompiled, line 2973)
        /// public static Vector2s GetZone(Vector3 p) {
        ///     int x = Utils.FloorToInt((float)(((double)p.x + 32.0) / 64.0));
        ///     int y = Utils.FloorToInt((float)(((double)p.z + 32.0) / 64.0));
        ///     return new Vector2s(x, y);            // Vector2s(int,int) narrows with (short)
        /// }
        /// </code>
        /// <b>Not <c>MathF.Floor((X + 32f) / 64f)</c>, twice over:</b> the divide is done in double and
        /// narrowed to float once, and the floor is <see cref="ValheimRounding.FloorToInt"/>, whose
        /// float bias quantises the fraction to 1/512 at this magnitude. For <c>x in (31.875, 32)</c>
        /// the exact <c>(x+32)/64</c> is in <c>(0.998046875, 1)</c>, <c>+64000f</c> rounds up to
        /// exactly <c>64001f</c>, and the game returns <b>1</b> where <c>MathF.Floor</c> returns
        /// <b>0</b> - a 0.125 m band at every zone boundary. No instance in the ground-truth saves
        /// falls in such a band (max <c>|pos - 64*zone|</c> = 28.85), which is exactly why a wrong
        /// implementation would pass the fixtures and fail silently on arbitrary query points
        /// (05-validation.md section 5.0).
        /// </summary>
        public (short X, short Y) Zone => (
            (short)ValheimRounding.FloorToInt((float)(((double)X + 32.0) / 64.0)),
            (short)ValheimRounding.FloorToInt((float)(((double)Z + 32.0) / 64.0)));

        public override string ToString() => string.Format(CultureInfo.InvariantCulture,
            "{0} at ({1}, {2}, {3}){4}", PrefabHash, X, Y, Z, Placed ? " placed" : "");
    }

    /// <summary>
    /// The <c>ZoneSystem.Save</c> block of a <c>.db2</c> (lines 987-1025, decompiled): one
    /// <c>int length</c> plus a gzip blob holding, in order, the generated zones, the location
    /// version, the global keys, the locations-generated flag and the location instances.
    /// </summary>
    public sealed class ZoneSystemData
    {
        public ZoneSystemData(IReadOnlyList<(short X, short Y)> generatedZones, int locationVersion,
                              IReadOnlyList<string> globalKeys, bool locationsGenerated,
                              IReadOnlyList<LocationInstance> locations)
        {
            GeneratedZones = generatedZones;
            LocationVersion = locationVersion;
            GlobalKeys = globalKeys;
            LocationsGenerated = locationsGenerated;
            Locations = locations;
        }

        /// <summary>
        /// Zones the players have already visited and which are therefore excluded from location
        /// candidacy. 112 in <c>asdasdasd</c>, 101 in <c>testworldclaude</c>.
        /// </summary>
        public IReadOnlyList<(short X, short Y)> GeneratedZones { get; }

        /// <summary>
        /// <c>m_locationVersion</c> - 32 in both ground-truth worlds. The code default is 1; the
        /// ZoneSystem prefab overrides it. A bump makes the game discard and regenerate every
        /// <i>unplaced</i> instance while already-placed ones survive (<c>ZoneSystem.Load</c> /
        /// <c>GenerateLocationsTimeSliced</c>), so it changes what this file can be compared against.
        /// </summary>
        public int LocationVersion { get; }

        /// <summary>
        /// The live global keys, with server-option keys removed by <c>ZoneSystem.Save</c> before
        /// writing. <b>Not</b> the <c>.fwl2</c>'s <see cref="WorldMeta.StartingGlobalKeys"/>. Both
        /// ground-truth worlds store 0 here, so the filter is untested against real data
        /// (05-validation.md section 7 item 4).
        /// </summary>
        public IReadOnlyList<string> GlobalKeys { get; }

        /// <summary><c>m_locationsGenerated</c>. True in both ground-truth worlds.</summary>
        public bool LocationsGenerated { get; }

        /// <summary>
        /// Every registered location instance: 12 314 in <c>asdasdasd</c>, 12 287 in
        /// <c>testworldclaude</c>, one per zone.
        /// <para>
        /// <b>Not the complete candidate set.</b> When a <c>m_unique</c> location is placed,
        /// <c>RemoveUnplacedLocations</c> deletes every remaining unplaced instance of that type; and
        /// instances whose hash the running game does not know are dropped on load
        /// (05-validation.md section 3.3).
        /// </para>
        /// </summary>
        public IReadOnlyList<LocationInstance> Locations { get; }
    }

    /// <summary>
    /// <c>RandEventSystem.Save</c>: a float, a string and four more floats written individually - not
    /// a <c>Vector3</c> helper (05-validation.md section 5.2).
    /// </summary>
    public sealed class RandomEventData
    {
        public RandomEventData(float eventTimer, string eventName, float eventTime, Vec3f position)
        {
            EventTimer = eventTimer;
            EventName = eventName;
            EventTime = eventTime;
            Position = position;
        }

        public float EventTimer { get; }
        public string EventName { get; }
        public float EventTime { get; }
        public Vec3f Position { get; }
    }

    /// <summary>A parsed <c>_main.&lt;N&gt;.db2</c>.</summary>
    public sealed class WorldDb
    {
        public WorldDb(int fileVersion, double netTime, ZoneSystemData zoneSystem,
                       RandomEventData? randomEvent, byte[]? persistentEventBlob,
                       byte[] undecodedTail, string? sourcePath = null)
        {
            FileVersion = fileVersion;
            NetTime = netTime;
            ZoneSystem = zoneSystem;
            RandomEvent = randomEvent;
            PersistentEventBlob = persistentEventBlob;
            UndecodedTail = undecodedTail;
            SourcePath = sourcePath;
        }

        /// <summary><c>ZNet.SaveWorldThread</c> writes <c>int 41</c> first.</summary>
        public int FileVersion { get; }

        /// <summary><c>m_netTime</c>. 2222.679928 for <c>asdasdasd</c>, 2086.899981 for the hold-out.</summary>
        public double NetTime { get; }

        public ZoneSystemData ZoneSystem { get; }

        /// <summary>Null when the trailing blocks could not be decoded; see <see cref="UndecodedTail"/>.</summary>
        public RandomEventData? RandomEvent { get; }

        /// <summary>
        /// <c>PersistentEventSystem.Save</c>'s payload, recorded verbatim. 15 bytes in both worlds,
        /// containing the ASCII <c>{"list":[]}</c>. <b>Unverified:</b> its compression framing - it is
        /// neither gzip nor a ZPackage. Nothing in world generation needs it (05-validation.md
        /// section 5.2 and section 6 item 5).
        /// </summary>
        public byte[]? PersistentEventBlob { get; }

        /// <summary>
        /// Anything after the ZoneSystem block that this reader did not decode. Empty for both
        /// ground-truth worlds, where the 40-byte tail parses completely with nothing left over.
        /// </summary>
        public byte[] UndecodedTail { get; }

        public string? SourcePath { get; }
    }
}
