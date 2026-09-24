using System;

namespace SeedLab.Dumper
{
    /// <summary>
    /// The hard refusals. Re-checked at the moment of action, never cached from an earlier frame -
    /// a peer can connect between the keypress and the dump.
    ///
    /// The user plays multiplayer and tests builds live. A dumper that runs while someone else is in
    /// the world is unacceptable even when it would be harmless, so the rule is "solo, hosting, no
    /// peers" and a refusal is loud.
    /// </summary>
    internal static class Safety
    {
        /// <summary>
        /// True when running anything would be a BREACH of the multiplayer rule: this session is a
        /// client of someone else's server, a dedicated server, or a host with connected peers.
        ///
        /// This is the distinction that decides the response. A breach is loud and permanently
        /// disables the plugin for the session (<see cref="Plugin.Refuse"/>). "You are at the main
        /// menu" or "the world has not finished loading" is not a breach - it is a mistimed keypress,
        /// and it only earns a message, because disabling the plugin for the rest of the session over
        /// a mistimed keypress would be its own kind of failure.
        /// </summary>
        public static bool MultiplayerBreach(out string reason)
        {
            if (ZNet.instance == null) { reason = null; return false; }   // main menu: nobody is here

            if (!ZNet.instance.IsServer())
            {
                reason = "this session is a client of someone else's server.";
                return true;
            }
            if (ZNet.instance.IsDedicated())
            {
                reason = "this is a dedicated server process.";
                return true;
            }
            int peers = PeerCount();
            if (peers != 0)
            {
                reason = "there are " + peers + " connected peer(s). The dumper only runs solo.";
                return true;
            }
            reason = null;
            return false;
        }

        /// <summary>
        /// Mode A (asset tables). Requires a loaded world that we host alone:
        /// <c>ZoneSystem</c> exists only in the <c>main</c> scene, and <c>m_locationInstances</c> /
        /// <c>World.m_biomeData</c> exist only on the host (spec 04 section 2.3).
        /// </summary>
        public static bool SoloHostWithWorld(out string reason)
        {
            if (ZNet.instance == null)
            {
                reason = "no world is loaded (ZNet.instance is null). Start a single-player world first.";
                return false;
            }
            if (!ZNet.instance.IsServer())
            {
                reason = "this session is a client of someone else's server; the location table is there " +
                         "but the placement results are not, and nothing may be dumped from a multiplayer session.";
                return false;
            }
            if (ZNet.instance.IsDedicated())
            {
                reason = "this is a dedicated server process.";
                return false;
            }
            int peers = PeerCount();
            if (peers != 0)
            {
                reason = "there are " + peers + " connected peer(s). The dumper only runs solo.";
                return false;
            }
            if (ZNet.World == null)
            {
                reason = "ZNet.World is null - no world data to identify the dump with.";
                return false;
            }
            reason = null;
            return true;
        }

        /// <summary>
        /// Native-function evidence. It reads nothing from the world, so the main menu is fine, but it
        /// does drive <c>UnityEngine.Random</c> (inside a <see cref="RandomGuard"/>) and must never do
        /// that with company present.
        /// </summary>
        public static bool NoPeers(out string reason)
        {
            if (ZNet.instance == null) { reason = null; return true; }   // main menu

            if (!ZNet.instance.IsServer())
            {
                reason = "this session is a client of someone else's server.";
                return false;
            }
            if (ZNet.instance.IsDedicated())
            {
                reason = "this is a dedicated server process.";
                return false;
            }
            int peers = PeerCount();
            if (peers != 0)
            {
                reason = "there are " + peers + " connected peer(s).";
                return false;
            }
            reason = null;
            return true;
        }

        /// <summary>
        /// The multi-seed world-generator dump. It replaces the static
        /// <c>WorldGenerator.instance</c>, which the <c>HeightmapBuilder</c> thread reads concurrently
        /// (<c>Heightmap.Regenerate</c> re-requests when <c>m_buildData.m_worldGen != WorldGenerator.instance</c>,
        /// Heightmap.cs:430) and which owns the static <c>s_cachedBiomeAreas</c> / <c>s_cachedBiomes</c>
        /// caches that the constructor clears. Doing that with a world loaded corrupts terrain.
        ///
        /// Spec 04 section 3.7 Invariant 3 mitigation 1: run it from the MAIN MENU only, where
        /// <c>ZNet.instance</c> is null, there is no <c>ZoneSystem</c>, and the only heightmap is the
        /// menu backdrop.
        /// </summary>
        public static bool MainMenuOnly(out string reason)
        {
            if (ZNet.instance != null)
            {
                reason = "a world is loaded. This dump replaces WorldGenerator.instance, which the " +
                         "HeightmapBuilder thread reads - it must run from the main menu. " +
                         "Quit to the main menu and run it again.";
                return false;
            }
            if (WorldGenerator.instance == null)
            {
                reason = "WorldGenerator.instance is null even at the menu; FejdStartup has not " +
                         "initialised the menu world yet. Wait for the main menu to finish loading.";
                return false;
            }
            reason = null;
            return true;
        }

        /// <summary>
        /// How often a long job re-asks the questions above, in seconds of wall clock.
        /// <see cref="Watch"/>.
        /// </summary>
        public const float RecheckSeconds = 1f;

        public static int PeerCount()
        {
            try
            {
                if (ZNet.instance == null) return 0;
                var peers = ZNet.instance.GetPeers();
                int byList = peers == null ? 0 : peers.Count;
                int byConn = ZNet.instance.GetPeerConnections();
                return Math.Max(byList, byConn);
            }
            catch (Exception e)
            {
                // Unreadable peer state is treated as "someone might be here".
                Plugin.Log.LogWarning("Peer count unreadable, assuming peers present: " + e.Message);
                return int.MaxValue;
            }
        }
    }

    /// <summary>
    /// The throttle for a job that must keep re-asking the safety questions while it runs.
    ///
    /// A dump is not one moment, it is minutes of frames: the asset dump's prefab walk alone is about
    /// a minute, and the world-generator dump is seconds per seed. Checking only at the start answers
    /// a question about a session that no longer exists by the time the dump ends - a peer can connect
    /// mid-walk, and the BepInEx manager object is <c>DontDestroyOnLoad</c>, so the coroutine also
    /// survives a menu -> world scene change. Every long loop therefore holds one of these and
    /// re-checks whenever <see cref="Due"/> says the interval has passed.
    ///
    /// It is a throttle and not a per-frame check because <see cref="Safety.PeerCount"/> walks
    /// <c>ZNet.GetPeers()</c>, and a second of extra dumping in a session that just stopped being solo
    /// is an acceptable price for not doing that 60 times a second. The first call is always due.
    /// </summary>
    internal struct Watch
    {
        private float _next;
        private bool _started;

        /// <summary>True at most once per <see cref="Safety.RecheckSeconds"/>, and always the first
        /// time. Uses <c>Time.realtimeSinceStartup</c>, which is unaffected by <c>timeScale</c> - a
        /// paused game must not switch the watchdog off.</summary>
        public bool Due()
        {
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (_started && now < _next) return false;
            _started = true;
            _next = now + Safety.RecheckSeconds;
            return true;
        }
    }
}
