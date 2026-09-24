using System;
using System.Collections.Generic;
using System.Threading;

namespace SeedLab.Web.Search
{
    /// <summary>
    /// The seam the search panel is built against.
    ///
    /// <para>It is deliberately small - describe yourself, start a query, watch events, cancel. Behind
    /// it today is <see cref="EngineSearchEngine"/>, which drives <c>SeedLab.Search</c>: the same
    /// tiered evaluator, the same criteria language and the same <c>CompiledQuery</c> that
    /// <c>vseed search</c> runs, so a query typed into the page and the same query run in the terminal
    /// return the same seeds in the same order.</para>
    /// </summary>
    public interface ISeedSearchEngine
    {
        SearchEngineInfo Describe();

        /// <summary>
        /// Validates the query and starts scanning.
        ///
        /// <para>Throws <see cref="ArgumentException"/> for a query this build cannot answer - a bad
        /// metric, a goal no seed can satisfy, or a goal that needs the dumped location table. It never
        /// starts a run whose results would be silently wrong: a location goal is refused with the
        /// reason, not scored zero.</para>
        /// </summary>
        ISearchRun Start(SearchQuery query);
    }

    /// <summary>One running search.</summary>
    public interface ISearchRun
    {
        string Id { get; }

        SearchProgress Progress { get; }

        /// <summary>
        /// Every event from the start of the run, then live ones until the run ends. A client that
        /// reconnects gets the whole history back, so a dropped connection does not lose results.
        /// </summary>
        IAsyncEnumerable<SearchEvent> ReadEvents(CancellationToken ct);

        void Cancel();
    }
}
