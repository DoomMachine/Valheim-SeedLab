using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using SeedLab.Runtime.Storage;

namespace SeedLab.Web.Tiles
{
    /// <summary>
    /// A bounded least-recently-used cache of encoded tiles, keyed by
    /// (seed, world-gen version, style, z, x, y), with a per-key gate so that two requests for the
    /// same tile render it once.
    ///
    /// <para>Bounded by BYTES, not by entry count: tiles differ in size by more than 10x between an
    /// empty ocean tile and a Mistlands coast, so a count bound would not bound the memory. The
    /// default 128 MiB holds roughly 2,500 typical tiles - about ten screenfuls of panning history at
    /// four zoom levels.</para>
    ///
    /// <para><b>Two tiers.</b> In front, the bounded in-memory table above. Behind it, when the server
    /// was given one, a directory under the runtime cache root (<c>&lt;cache&gt;/tiles</c>) where the
    /// same PNGs survive a restart - a tile is a pure function of (seed, gen version, style, z, x, y),
    /// so a cached one can never be stale, only thrown away. That root is the one wipeable place
    /// SeedLab is allowed to leave things, which is exactly what a tile cache is; the audit's defect 6
    /// was renders accumulating in the user's WORKING directory instead, and nothing here does that.</para>
    /// </summary>
    public sealed class TileCache
    {
        private readonly object _lock = new object();
        private readonly Dictionary<string, LinkedListNode<Entry>> _map
            = new Dictionary<string, LinkedListNode<Entry>>(StringComparer.Ordinal);
        private readonly LinkedList<Entry> _lru = new LinkedList<Entry>();
        private readonly Dictionary<string, SemaphoreSlim> _gates
            = new Dictionary<string, SemaphoreSlim>(StringComparer.Ordinal);
        private readonly long _capacityBytes;
        private readonly string? _diskDir;
        private readonly long _diskCapacityBytes;

        private long _bytes;
        private long _hits;
        private long _misses;
        private long _evictions;
        private long _diskHits;
        private long _diskWrites;
        private long _diskEvictions;
        private long _diskCheckedTicks;

        public TileCache(long capacityBytes = 128L * 1024 * 1024, string? diskDirectory = null,
                         long diskCapacityBytes = 512L * 1024 * 1024)
        {
            if (capacityBytes < 64 * 1024) throw new ArgumentOutOfRangeException(nameof(capacityBytes));
            _capacityBytes = capacityBytes;
            _diskCapacityBytes = diskCapacityBytes;
            if (!string.IsNullOrWhiteSpace(diskDirectory))
            {
                try
                {
                    Directory.CreateDirectory(diskDirectory!);
                    _diskDir = diskDirectory;
                }
                catch (Exception)
                {
                    // A cache directory that cannot be created is not an error: the memory tier alone
                    // is correct, only colder. Failing the server over it would be the wrong trade.
                    _diskDir = null;
                }

                if (_diskDir != null) SweepOldTemps(_diskDir);
            }
        }

        /// <summary>
        /// The "&lt;hash&gt;.png.tmp" files an earlier build left when a write or its rename failed -
        /// nothing ever removed them (2026-09-24). Writes now go through <see cref="DurableWrite"/>,
        /// whose temp names the cache root's reaper knows; these are the old kind. A tile is written in
        /// milliseconds, so one older than a minute belongs to no write that is still going on.
        /// </summary>
        private static void SweepOldTemps(string dir)
        {
            try
            {
                foreach (string f in Directory.EnumerateFiles(dir, "*.png.tmp"))
                {
                    try
                    {
                        if (DateTime.UtcNow - File.GetLastWriteTimeUtc(f) > TimeSpan.FromMinutes(1)) File.Delete(f);
                    }
                    catch (Exception)
                    {
                        // In use or not ours to delete; the next start tries again.
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>A tile, not a write in progress: <see cref="DurableWrite"/>'s temp names also end in ".png".</summary>
        private static bool IsTile(string path) =>
            !Path.GetFileName(path).StartsWith(DurableWrite.TempPrefix, StringComparison.Ordinal);

        /// <summary>The directory the second tier uses, or null when there is no disk tier.</summary>
        public string? DiskDirectory => _diskDir;

        public long DiskHits => Interlocked.Read(ref _diskHits);

        public long DiskWrites => Interlocked.Read(ref _diskWrites);

        public long DiskEvictions => Interlocked.Read(ref _diskEvictions);

        public long DiskCapacityBytes => _diskCapacityBytes;

        /// <summary>Bytes the disk tier holds. Walks the directory, so it is for the stats page only.</summary>
        public long DiskBytes()
        {
            if (_diskDir == null) return 0;
            long total = 0;
            try
            {
                foreach (string f in Directory.EnumerateFiles(_diskDir, "*.png"))
                {
                    if (IsTile(f)) total += new FileInfo(f).Length;
                }
            }
            catch (Exception)
            {
                return -1;
            }

            return total;
        }

        private sealed class Entry
        {
            public Entry(string key, byte[] png) { Key = key; Png = png; }

            public string Key { get; }

            public byte[] Png { get; }
        }

        public long Bytes { get { lock (_lock) { return _bytes; } } }

        public int Count { get { lock (_lock) { return _map.Count; } } }

        public long Hits { get { lock (_lock) { return _hits; } } }

        public long Misses { get { lock (_lock) { return _misses; } } }

        public long Evictions { get { lock (_lock) { return _evictions; } } }

        public long CapacityBytes => _capacityBytes;

        public static string Key(int seed, int version, TileStyle style, int z, int x, int y)
            => seed + "/" + version + "/" + style.CacheKey + "/" + z + "/" + x + "/" + y;

        public void Clear()
        {
            lock (_lock)
            {
                _map.Clear();
                _lru.Clear();
                _bytes = 0;
            }
        }

        /// <summary>
        /// The cached tile, or <paramref name="render"/>'s result, stored before it is returned.
        ///
        /// <para>Concurrent callers for the same key are serialised on a per-key semaphore, so a
        /// reload that asks for twenty tiles twice renders twenty, not forty. If the first caller is
        /// cancelled the second simply renders it itself: cancellation belongs to one request and is
        /// never inherited by another.</para>
        /// </summary>
        public byte[] GetOrRender(string key, Func<byte[]> render, CancellationToken ct)
        {
            byte[]? hit = TryGet(key);
            if (hit != null) return hit;

            SemaphoreSlim gate = Gate(key);
            bool acquired = false;
            try
            {
                gate.Wait(ct);
                acquired = true;

                hit = TryGet(key);
                if (hit != null) return hit;

                hit = TryReadDisk(key);
                if (hit != null)
                {
                    Add(key, hit);
                    Interlocked.Increment(ref _diskHits);
                    return hit;
                }

                byte[] png = render();
                Add(key, png);
                WriteDisk(key, png);
                return png;
            }
            finally
            {
                // Both are needed even when the wait itself was cancelled: Gate() has already
                // registered this caller, and an unbalanced registration would pin the semaphore in
                // the table for the life of the process.
                if (acquired) gate.Release();
                ReleaseGate(key);
            }
        }

        private byte[]? TryGet(string key)
        {
            lock (_lock)
            {
                if (_map.TryGetValue(key, out LinkedListNode<Entry>? node))
                {
                    _lru.Remove(node);
                    _lru.AddFirst(node);
                    _hits++;
                    return node.Value.Png;
                }

                return null;
            }
        }

        private void Add(string key, byte[] png)
        {
            lock (_lock)
            {
                if (_map.ContainsKey(key)) return;
                _misses++;
                LinkedListNode<Entry> node = _lru.AddFirst(new Entry(key, png));
                _map[key] = node;
                _bytes += png.Length;
                while (_bytes > _capacityBytes && _lru.Count > 1)
                {
                    LinkedListNode<Entry>? last = _lru.Last;
                    if (last == null) break;
                    _lru.RemoveLast();
                    _map.Remove(last.Value.Key);
                    _bytes -= last.Value.Png.Length;
                    _evictions++;
                }
            }
        }

        /// <summary>
        /// The disk name for a cache key: the first 16 bytes of the key's SHA-256, in hex. A key holds
        /// a style string and a signed seed, neither of which is a safe file name, and a hash is
        /// fixed-width - so a cache entry can never become a path.
        /// </summary>
        private string? PathFor(string key)
        {
            if (_diskDir == null) return null;
            byte[] h = SHA256.HashData(Encoding.UTF8.GetBytes(key));
            StringBuilder sb = new StringBuilder(40);
            for (int i = 0; i < 16; i++) sb.Append(h[i].ToString("x2", CultureInfo.InvariantCulture));
            sb.Append(".png");
            return Path.Combine(_diskDir, sb.ToString());
        }

        private byte[]? TryReadDisk(string key)
        {
            string? path = PathFor(key);
            if (path == null) return null;
            try
            {
                if (!File.Exists(path)) return null;
                byte[] b = File.ReadAllBytes(path);
                return b.Length > 0 ? b : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private void WriteDisk(string key, byte[] png)
        {
            string? path = PathFor(key);
            if (path == null || png.Length == 0) return;
            try
            {
                // Temp + rename, so a killed process can never leave a half-written PNG that a later
                // run would hand back as a tile. Through DurableWrite since 2026-09-24: a write that fails
                // removes its temp file, and one a kill leaves is named so that the cache root's reaper
                // removes it - the old "<hash>.png.tmp" was left for good. Tried ONCE: this is a cache on
                // a tile request's path, and waiting out a scanner here would only slow the map down.
                DurableWrite.Bytes(path, png, RetrySchedule.Once);
                Interlocked.Increment(ref _diskWrites);
            }
            catch (Exception)
            {
                return;
            }

            TrimDisk();
        }

        /// <summary>
        /// Keeps the disk tier under its bound, oldest write first. Checked at most once a minute: the
        /// directory walk is the expensive part, and a tile cache does not need a tight bound.
        /// </summary>
        private void TrimDisk()
        {
            if (_diskDir == null) return;
            long now = Environment.TickCount64;
            long last = Interlocked.Read(ref _diskCheckedTicks);
            if (now - last < 60_000) return;
            if (Interlocked.CompareExchange(ref _diskCheckedTicks, now, last) != last) return;

            try
            {
                List<FileInfo> files = new List<FileInfo>();
                long total = 0;
                foreach (string f in Directory.EnumerateFiles(_diskDir, "*.png"))
                {
                    if (!IsTile(f)) continue;
                    FileInfo fi = new FileInfo(f);
                    files.Add(fi);
                    total += fi.Length;
                }

                if (total <= _diskCapacityBytes) return;
                files.Sort((a, b) => a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc));
                foreach (FileInfo fi in files)
                {
                    if (total <= _diskCapacityBytes) break;
                    long len = fi.Length;
                    try
                    {
                        fi.Delete();
                        total -= len;
                        Interlocked.Increment(ref _diskEvictions);
                    }
                    catch (Exception)
                    {
                    }
                }
            }
            catch (Exception)
            {
                // A cache that cannot be trimmed grows to whatever its directory allows; that is not a
                // reason to fail a tile request.
            }
        }

        private SemaphoreSlim Gate(string key)
        {
            lock (_lock)
            {
                if (!_gates.TryGetValue(key, out SemaphoreSlim? g))
                {
                    g = new SemaphoreSlim(1, 1);
                    _gates[key] = g;
                }

                // Counted by the waiters themselves: the dictionary entry is removed once the last
                // one has left, so the gate table cannot grow without bound over a long session.
                _gateUsers.TryGetValue(key, out int n);
                _gateUsers[key] = n + 1;
                return g;
            }
        }

        private readonly Dictionary<string, int> _gateUsers = new Dictionary<string, int>(StringComparer.Ordinal);

        private void ReleaseGate(string key)
        {
            lock (_lock)
            {
                if (!_gateUsers.TryGetValue(key, out int n)) return;
                if (n <= 1)
                {
                    _gateUsers.Remove(key);
                    _gates.Remove(key);
                }
                else
                {
                    _gateUsers[key] = n - 1;
                }
            }
        }
    }
}
