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

        /// <summary>The runs that have not ended yet, oldest first.</summary>
        public List<ISearchRun> Running()
        {
            List<ISearchRun> all = new List<ISearchRun>();
            lock (_lock)
            {
                for (LinkedListNode<string>? n = _order.Last; n != null; n = n.Previous)
                {
                    if (_runs.TryGetValue(n.Value, out ISearchRun? r) && r.IsRunning) all.Add(r);
                }
            }

            return all;
        }

        /// <summary>True while any run has not ended - which the idle reminder counts as someone using SeedLab.</summary>
        public bool AnyRunning()
        {
            lock (_lock)
            {
                foreach (ISearchRun r in _runs.Values)
                {
                    if (r.IsRunning) return true;
                }
            }

            return false;
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
