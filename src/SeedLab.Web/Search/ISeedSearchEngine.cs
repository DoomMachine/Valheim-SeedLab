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

        /// <summary>
        /// Tries again the last checkpoint save of a run that has ended, when that save failed - the
        /// page's "Retry saving". Safe to call at any time: a run still going, or one with nothing to
        /// save, says so and changes nothing.
        /// </summary>
        SearchRetryResult RetrySave();

        /// <summary>
        /// The last save as it stands now - <c>{checkpointError, checkpointPath, resumeCommand}</c> - or
        /// null while the run is going. Unlike the frozen <c>done</c> event, it follows a Retry saving.
        /// </summary>
        object? SaveState { get; }
    }
}
