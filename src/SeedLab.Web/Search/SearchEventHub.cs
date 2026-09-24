using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace SeedLab.Web.Search
{
    /// <summary>
    /// Fan-out of one run's events to however many browser tabs are watching it, with replay.
    ///
    /// <para><b>Replay is the point.</b> A scan can run for hours; a reload, a sleeping laptop or a
    /// dropped EventSource must not lose the hits already found. Every event except <c>progress</c> is
    /// kept, so a client that joins late is handed the whole history and then the live stream. Progress
    /// is not history - a client wants the latest state, not ten thousand superseded ticks - so the
    /// most recent one alone is replayed.</para>
    ///
    /// <para>Bounded: <see cref="MaxHistory"/> results are kept. Past that the hub stops replaying
    /// individual results and says how many it dropped, rather than growing without limit on a run that
    /// matches millions of seeds. The live stream is unaffected.</para>
    /// </summary>
    public sealed class SearchEventHub
    {
        /// <summary>How many replayable events one run keeps. A result is ~400 bytes of JSON.</summary>
        public const int MaxHistory = 4000;

        private readonly object _lock = new object();
        private readonly List<SearchEvent> _history = new List<SearchEvent>();
        private readonly List<Channel<SearchEvent>> _subs = new List<Channel<SearchEvent>>();
        private SearchEvent? _lastProgress;
        private long _dropped;
        private bool _closed;

        public long DroppedFromHistory
        {
            get { lock (_lock) { return _dropped; } }
        }

        public void Publish(SearchEvent e)
        {
            lock (_lock)
            {
                if (e.Type == "progress")
                {
                    _lastProgress = e;
                }
                else if (_history.Count < MaxHistory)
                {
                    _history.Add(e);
                }
                else
                {
                    _dropped++;
                }

                foreach (Channel<SearchEvent> c in _subs) c.Writer.TryWrite(e);
            }
        }

        /// <summary>No more events will ever be published; every reader finishes after its replay.</summary>
        public void Close()
        {
            lock (_lock)
            {
                _closed = true;
                foreach (Channel<SearchEvent> c in _subs) c.Writer.TryComplete();
                _subs.Clear();
            }
        }

        public async IAsyncEnumerable<SearchEvent> Read([EnumeratorCancellation] CancellationToken ct)
        {
            Channel<SearchEvent> ch = Channel.CreateUnbounded<SearchEvent>();
            lock (_lock)
            {
                foreach (SearchEvent e in _history) ch.Writer.TryWrite(e);
                if (_lastProgress != null) ch.Writer.TryWrite(_lastProgress);
                if (_closed) ch.Writer.TryComplete();
                else _subs.Add(ch);
            }

            try
            {
                await foreach (SearchEvent e in ch.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                {
                    yield return e;
                }
            }
            finally
            {
                lock (_lock) { _subs.Remove(ch); }
            }
        }
    }
}
