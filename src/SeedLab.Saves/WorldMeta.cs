using System.Collections.Generic;

namespace SeedLab.Saves
{
    /// <summary>
    /// One entry of <c>World.m_playerHistory</c>, written at world version 41 and above.
    /// <c>ZNet.CrossNetworkUserInfo.Read(ZPackage)</c> - four strings, in this order.
    /// </summary>
    public sealed class CrossNetworkUser
    {
        public CrossNetworkUser(string platformUserId, string displayName, string serverAssignedName, string playFabId)
        {
            PlatformUserId = platformUserId;
            DisplayName = displayName;
            ServerAssignedName = serverAssignedName;
            PlayFabId = playFabId;
        }

        /// <summary>e.g. <c>Steam_7656...</c>.</summary>
        public string PlatformUserId { get; }
        public string DisplayName { get; }
        public string ServerAssignedName { get; }
        public string PlayFabId { get; }

        public override string ToString() => DisplayName + " (" + PlatformUserId + ")";
    }

    /// <summary>
    /// A parsed <c>_main.&lt;N&gt;.fwl2</c>.
    /// <para>
    /// This is the file that tells you a world's seed. Everything else about generation follows from
    /// <see cref="Seed"/> and <see cref="WorldGenVersion"/>.
    /// </para>
    /// </summary>
    public sealed class WorldMeta
    {
        public WorldMeta(int fileVersion, string name, string seedName, int seed, long uid,
                         int worldGenVersion, bool needsDb,
                         IReadOnlyList<string> startingGlobalKeys,
                         IReadOnlyList<CrossNetworkUser> playerHistory,
                         string? sourcePath = null)
        {
            FileVersion = fileVersion;
            Name = name;
            SeedName = seedName;
            Seed = seed;
            Uid = uid;
            WorldGenVersion = worldGenVersion;
            NeedsDb = needsDb;
            StartingGlobalKeys = startingGlobalKeys;
            PlayerHistory = playerHistory;
            SourcePath = sourcePath;
        }

        /// <summary><c>Version.World</c>; 41 = <c>DeepNorth</c> on 1.0.15.</summary>
        public int FileVersion { get; }

        /// <summary><c>m_name</c> - the world's display name.</summary>
        public string Name { get; }

        /// <summary><c>m_seedName</c> - the seed <i>text</i> the player typed or the game generated.</summary>
        public string SeedName { get; }

        /// <summary><c>m_seed</c> - the int32 that actually drives generation.</summary>
        public int Seed { get; }

        /// <summary>
        /// <c>m_uid</c> - <c>name.GetStableHashCode() + Utils.GenerateUID()</c>, so it is random per
        /// world creation. The character save keys its per-world map data by <b>this</b>, not by the
        /// name or the seed (05-validation.md section 5.4).
        /// </summary>
        public long Uid { get; }

        /// <summary>
        /// <c>m_worldGenVersion</c>; <c>Version.c_WorldGenVersion</c> is 2 on this build. Both
        /// ground-truth worlds are at 2. <c>WorldGenerator.VersionSetup</c> changes
        /// <c>maxMarshDistance</c> for versions &lt;= 1, so this is not cosmetic.
        /// </summary>
        public int WorldGenVersion { get; }

        /// <summary><c>m_needsDB</c>.</summary>
        public bool NeedsDb { get; }

        /// <summary>
        /// <c>m_startingGlobalKeys</c> - the world's modifiers and starting keys, e.g.
        /// <c>"resourcerate 300"</c> and a <c>"preset ..."</c> line.
        /// <para>
        /// <b>This is not the same list as the <c>.db2</c>'s global keys</b>
        /// (<see cref="ZoneSystemData.GlobalKeys"/>), which is the live key set with server-option
        /// keys filtered out. A report that says "world keys" must say which list it read
        /// (05-validation.md section 7 item 4). World generation ignores both.
        /// </para>
        /// </summary>
        public IReadOnlyList<string> StartingGlobalKeys { get; }

        /// <summary>Present from world version 41. Everyone who has joined this world.</summary>
        public IReadOnlyList<CrossNetworkUser> PlayerHistory { get; }

        /// <summary>The file this came from, for diagnostics.</summary>
        public string? SourcePath { get; }

        /// <summary>
        /// What <c>World..ctor</c> would compute from <see cref="SeedName"/>:
        /// <c>m_seed = ((!(m_seedName == "")) ? m_seedName.GetStableHashCode() : 0)</c> (line 73).
        /// </summary>
        public int SeedFromSeedName => SaveStableHash.SeedFromSeedText(SeedName);

        /// <summary>
        /// True when the stored <see cref="Seed"/> really is the hash of the stored
        /// <see cref="SeedName"/>. A false here means the world was not created from its seed text in
        /// the normal way (or the hash port is wrong), and the tool must trust <see cref="Seed"/>,
        /// which is what the game uses.
        /// </summary>
        public bool SeedMatchesSeedName => SeedFromSeedName == Seed;

        public override string ToString() =>
            Name + " (seed \"" + SeedName + "\" = " + Seed + ", uid " + Uid +
            ", world v" + FileVersion + ", worldGen v" + WorldGenVersion + ")";
    }
}
