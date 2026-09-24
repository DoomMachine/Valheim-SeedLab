using System;
using System.Collections.Generic;
using SeedLab.WorldGen;

namespace SeedLab.Web
{
    /// <summary>
    /// Keeps a constructed <see cref="WorldGeneratorPort"/> per seed alive between requests.
    ///
    /// <para>This is the single most important cache in the server. Constructing a generator runs the
    /// whole river / stream / lake pregeneration: measured by <c>vseed bench</c> on this machine at
    /// <b>322 ms per world on one thread</b>, against about 30 ms to sample a whole 258 x 258 tile.
    /// Without this, every tile would pay 322 ms and panning would be unusable.</para>
    ///
    /// <para>Bounded to a handful of seeds because each one holds its pregenerated river-point grid.
    /// Eviction is least-recently-used, and a generator that is evicted while a render is still using
    /// it stays alive for that render - the caller holds a reference, nothing is disposed.</para>
    /// </summary>
    public sealed class WorldCache
    {
        private readonly object _lock = new object();
        private readonly Dictionary<(int Seed, int Version), Lazy<WorldGeneratorPort>> _map
            = new Dictionary<(int, int), Lazy<WorldGeneratorPort>>();
        private readonly LinkedList<(int Seed, int Version)> _lru = new LinkedList<(int, int)>();
        private readonly int _capacity;

        public WorldCache(int capacity = 4)
        {
            if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
            _capacity = capacity;
        }

        public int Constructed { get; private set; }

        /// <summary>
        /// The shared generator for this seed. Never sample with the returned handle directly from
        /// more than one thread: take <c>.Fork()</c> per worker, as <c>WorldField.Sample</c> does.
        /// </summary>
        public WorldGeneratorPort Get(int seed, int worldGenVersion)
        {
            (int, int) key = (seed, worldGenVersion);
            Lazy<WorldGeneratorPort> lazy;
            lock (_lock)
            {
                if (_map.TryGetValue(key, out Lazy<WorldGeneratorPort>? found))
                {
                    lazy = found;
                    _lru.Remove(key);
                    _lru.AddFirst(key);
                }
                else
                {
                    lazy = new Lazy<WorldGeneratorPort>(
                        () => new WorldGeneratorPort(seed, worldGenVersion, menu: false),
                        System.Threading.LazyThreadSafetyMode.ExecutionAndPublication);
                    _map[key] = lazy;
                    _lru.AddFirst(key);
                    while (_lru.Count > _capacity)
                    {
                        LinkedListNode<(int, int)>? last = _lru.Last;
                        if (last == null) break;
                        _lru.RemoveLast();
                        _map.Remove(last.Value);
                    }
                }
            }

            // Constructed outside the lock: 322 ms is far too long to hold a global lock for, and Lazy
            // already guarantees exactly one construction for concurrent callers on the same seed.
            bool wasCreated = lazy.IsValueCreated;
            WorldGeneratorPort gen = lazy.Value;
            if (!wasCreated)
            {
                lock (_lock) { Constructed++; }
            }

            return gen;
        }
    }
}
