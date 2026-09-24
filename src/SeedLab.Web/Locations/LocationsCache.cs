using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using SeedLab.Web.Api;

namespace SeedLab.Web.Locations
{
    /// <summary>What a request for a seed's locations can find.</summary>
    public enum LocationsState
    {
        /// <summary>Nothing has been asked for this seed and set yet.</summary>
        Absent = 0,

        Computing = 1,

        Ready = 2,

        Failed = 3,
    }

    /// <summary>One seed's locations: the answer, or the run that is producing it.</summary>
    public sealed class LocationsEntry
    {
        /// <summary>
        /// <b>Volatile, and written last.</b> The computing thread fills <see cref="Report"/> and then
        /// publishes by setting this; a request thread reads this first and only then touches the
        /// report. Without the volatile the two writes may be reordered and a reader can see
        /// <c>Ready</c> with a null report - a once-in-a-blue-moon 500 that is impossible to reproduce.
        /// </summary>
        public volatile LocationsState State;

        public LocationsReport? Report;

        public string? Error;

        /// <summary>A hint the user can act on, when the failure is actionable (a stale data stamp).</summary>
        public string? Hint;

        /// <summary>world | placement | biomes - what the run is doing right now.</summary>
        public string Phase = "";

        public double Fraction;

        public double ElapsedS;
    }

    /// <summary>
    /// Computes a seed's locations at most once, in the background, and keeps the answer.
    ///
    /// <para><b>Why it cannot be done inside the request.</b> A full placement run is about seven
    /// seconds a seed on this machine, nearly all of it the 2048x2048 biome-point grid the game itself
    /// builds. Seven seconds inside a GET is a hung page and a browser that retries; worse, a second
    /// tab asking for the same seed would run it twice and take every core from the map. So a request
    /// either gets the answer or gets <c>computing</c> with a phase and an elapsed time, and the page
    /// shows that rather than freezing.</para>
    ///
    /// <para>Bounded LRU, because a full world is about 12,300 instances and there is no reason to hold
    /// more than a few seeds. A computation already running is never evicted from under its own
    /// thread - eviction drops the cache entry, the thread finishes into an orphan and is collected.</para>
    /// </summary>
    public sealed class LocationsCache
    {
        private readonly LocationsProvider? _provider;
        private readonly int _capacity;
        private readonly int _threads;
        private readonly object _lock = new object();
        private readonly Dictionary<string, LocationsEntry> _map = new Dictionary<string, LocationsEntry>(StringComparer.Ordinal);
        private readonly LinkedList<string> _lru = new LinkedList<string>();

        /// <summary>
        /// One placement run at a time across the whole server. Two at once would double the memory of
        /// the biome grid and halve the cores available to each, for no gain: they are not independent
        /// work, they are the same kind of work queued.
        /// </summary>
        private readonly SemaphoreSlim _slot = new SemaphoreSlim(1, 1);

        public LocationsCache(LocationsProvider? provider, int capacity = 4, int threads = 0)
        {
            _provider = provider;
            _capacity = Math.Max(1, capacity);
            _threads = threads;
        }

        /// <summary>False when this build has no way to place locations at all.</summary>
        public bool Available => _provider != null;

        public static string Key(int seed, int version, LocationSet set)
            => seed + "/" + version + "/" + (set == LocationSet.All ? "all" : "core");

        /// <summary>
        /// The entry for this seed and set, starting the computation if <paramref name="start"/> is set
        /// and nothing is running or finished yet.
        /// </summary>
        public LocationsEntry Get(int seed, int version, LocationSet set, bool start)
        {
            string key = Key(seed, version, set);
            LocationsEntry entry;
            bool launch = false;

            lock (_lock)
            {
                if (_map.TryGetValue(key, out LocationsEntry? found))
                {
                    _lru.Remove(key);
                    _lru.AddFirst(key);

                    // A failure is not cached forever: asking again after fixing the data should work.
                    if (found.State != LocationsState.Failed || !start) return found;
                    _map.Remove(key);
                    _lru.Remove(key);
                }

                if (!start) return new LocationsEntry { State = LocationsState.Absent };

                entry = new LocationsEntry { State = LocationsState.Computing, Phase = "queued" };
                _map[key] = entry;
                _lru.AddFirst(key);
                launch = true;
                while (_lru.Count > _capacity)
                {
                    string last = _lru.Last!.Value;
                    _lru.RemoveLast();
                    _map.Remove(last);
                }
            }

            if (launch) Task.Run(() => Compute(seed, version, set, entry));
            return entry;
        }

        private void Compute(int seed, int version, LocationSet set, LocationsEntry entry)
        {
            LocationsProvider? provider = _provider;
            if (provider == null)
            {
                entry.Error = "this build has no location placement wired in.";
                entry.State = LocationsState.Failed;
                return;
            }

            Stopwatch sw = Stopwatch.StartNew();
            try
            {
                _slot.Wait();
            }
            catch (ObjectDisposedException)
            {
                entry.State = LocationsState.Failed;
                entry.Error = "the server is shutting down.";
                return;
            }

            try
            {
                entry.Phase = "world";
                LocationsRequest req = new LocationsRequest
                {
                    Seed = seed,
                    WorldGenVersion = version,
                    Set = set,
                    Threads = _threads,
                    OnPhase = (phase, fraction) =>
                    {
                        entry.Phase = phase;
                        entry.Fraction = fraction;
                        entry.ElapsedS = sw.Elapsed.TotalSeconds;
                    },
                };

                LocationsReport report = provider(req, CancellationToken.None);
                entry.Report = report;
                entry.ElapsedS = sw.Elapsed.TotalSeconds;
                entry.Fraction = 1.0;
                entry.Phase = "done";
                entry.State = LocationsState.Ready;
            }
            catch (Exception ex)
            {
                entry.Error = ex.Message;
                entry.Hint = HintFor(ex);
                entry.ElapsedS = sw.Elapsed.TotalSeconds;
                entry.State = LocationsState.Failed;
            }
            finally
            {
                _slot.Release();
            }
        }

        /// <summary>
        /// The one failure a user can actually do something about: the dumped table describes a
        /// different Valheim build than the one installed, so the answer would be wrong and the data
        /// policy fails it closed rather than guessing.
        /// </summary>
        private static string? HintFor(Exception ex)
        {
            string name = ex.GetType().Name;
            if (name == "StaleGameDataException")
            {
                return "run tools\\SeedLab.Dumper once against the installed game to capture its "
                       + "location table; terrain, biomes and heights are unaffected and stay exact.";
            }

            return null;
        }
    }
}
