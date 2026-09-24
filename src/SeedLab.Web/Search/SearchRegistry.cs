using System;
using System.Collections.Generic;

namespace SeedLab.Web.Search
{
    /// <summary>
    /// The runs this server knows about, so a page reload can rejoin a search instead of losing it.
    /// Bounded: starting a new run past the limit cancels and drops the oldest, because a forgotten
    /// run is still burning every core.
    /// </summary>
    public sealed class SearchRegistry
    {
        private readonly object _lock = new object();
        private readonly Dictionary<string, ISearchRun> _runs = new Dictionary<string, ISearchRun>(StringComparer.Ordinal);
        private readonly LinkedList<string> _order = new LinkedList<string>();
        private readonly int _capacity;

        public SearchRegistry(int capacity = 4)
        {
            _capacity = Math.Max(1, capacity);
        }

        public void Add(ISearchRun run)
        {
            List<ISearchRun> evicted = new List<ISearchRun>();
            lock (_lock)
            {
                _runs[run.Id] = run;
                _order.AddFirst(run.Id);
                while (_order.Count > _capacity)
                {
                    string id = _order.Last!.Value;
                    _order.RemoveLast();
                    if (_runs.Remove(id, out ISearchRun? old)) evicted.Add(old);
                }
            }

            foreach (ISearchRun r in evicted) r.Cancel();
        }

        public bool TryGet(string id, out ISearchRun run)
        {
            lock (_lock) { return _runs.TryGetValue(id, out run!); }
        }

        /// <summary>Cancels everything still running. Called when the server shuts down.</summary>
        public void CancelAll()
        {
            List<ISearchRun> all;
            lock (_lock) { all = new List<ISearchRun>(_runs.Values); }
            foreach (ISearchRun r in all) r.Cancel();
        }
    }
}
