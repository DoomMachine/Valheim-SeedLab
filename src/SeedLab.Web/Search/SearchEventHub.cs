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
    /// dropped EventSource must not lose the hits already found. Every event is kept for replay, so a
    /// client that joins late is handed the whole history and then the live stream - except the two
    /// that are states rather than history: <c>progress</c> and the best-of table <c>top</c>, of which
    /// a client wants the latest, not thousands superseded, so the most recent one of each alone is
    /// replayed.</para>
    ///
    /// <para>Bounded: <see cref="MaxHistory"/> <c>result</c> lines are kept. Past that the hub stops
    /// replaying individual results and counts how many it dropped, rather than growing without limit on
    /// a run that matches millions of seeds. The live stream is unaffected.</para>
    ///
    /// <para><b>Only results are capped</b> (review of 2026-09-24). The cap used to apply to every
    /// event, so a run that had streamed 4,000 results and tables - measured, under three minutes of a
    /// cheap query - dropped its later <c>warning</c> events and its <c>done</c> from replay: a tab that
    /// rejoined never saw the warnings or the failed last save, and, with no <c>done</c> to end on,
    /// reconnected for as long as it was open. <c>done</c> is kept apart and replayed last, after the
    /// latest table and progress, because the page closes its stream on it.</para>
    /// </summary>
    public sealed class SearchEventHub
    {
        /// <summary>How many <c>result</c> lines one run keeps for replay. A result is ~400 bytes of JSON.</summary>
        public const int MaxHistory = 4000;

        private readonly object _lock = new object();
        private readonly List<SearchEvent> _history = new List<SearchEvent>();
        private readonly List<Channel<SearchEvent>> _subs = new List<Channel<SearchEvent>>();
        private SearchEvent? _lastProgress;
        private SearchEvent? _lastTop;
        private SearchEvent? _done;
        private int _results;
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
                else if (e.Type == "top")
                {
                    _lastTop = e;
                }
                else if (e.Type == "done")
                {
                    _done = e;
                }
                else if (e.Type != "result")
                {
                    _history.Add(e);
                }
                else if (_results < MaxHistory)
                {
                    _history.Add(e);
                    _results++;
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
                if (_lastTop != null) ch.Writer.TryWrite(_lastTop);
                if (_lastProgress != null) ch.Writer.TryWrite(_lastProgress);
                if (_done != null) ch.Writer.TryWrite(_done);
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
