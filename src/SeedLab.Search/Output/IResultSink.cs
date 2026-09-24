using System;
using SeedLab.Search.Evaluation;

namespace SeedLab.Search.Output
{
    /// <summary>
    /// Where a run's passing seeds go. Three implementations, and the choice between them is the
    /// difference between a 1.7 MB file and a 7.36 TB one:
    ///
    /// <list type="bullet">
    /// <item><see cref="BoundedResultSink"/> - the DEFAULT. A bounded best-N heap; only those N are
    /// ever written, so the file is O(keep) whatever the scan finds, and the true match count is
    /// reported beside it ("top 1000 of 131,076").</item>
    /// <item><see cref="ResultWriter"/> - <c>keep: all</c> without rotation. Every match is streamed.
    /// Safe only when the match count is known to be small.</item>
    /// <item><see cref="SegmentedResultSink"/> - <c>keep: all</c> with rotation into gzipped 1 GB
    /// JSONL segments plus a manifest, and an optional reduce-and-delete step per segment.</item>
    /// </list>
    ///
    /// <para>Every sink is written by the single collector thread, in block order, so a sink never has
    /// to be thread-safe and the file is reproducible whatever the thread count.</para>
    /// </summary>
    public interface IResultSink : IDisposable
    {
        /// <summary>The primary file this sink writes (the base path for a segmented sink).</summary>
        string Path { get; }

        /// <summary>Offer one passing seed. A bounded sink may count it and not write it.</summary>
        void Add(SeedResult r);

        /// <summary>Make everything written so far durable. Called on the run's checkpoint cadence.</summary>
        void Flush();

        /// <summary>Close the output: the JSON array's bracket, the last segment, the manifest.</summary>
        void Finish();

        /// <summary>
        /// Bytes of complete records in the results file, for the checkpoint to truncate back to on
        /// resume, or <b>-1</b> when this sink does not append (a bounded sink rewrites its file
        /// wholesale from the heap, so there is no byte offset to resume at).
        /// </summary>
        long Length { get; }

        /// <summary>
        /// Bytes this sink's output actually occupies on disk right now - unlike <see cref="Length"/>,
        /// which is a resume offset and is -1 for a sink that rewrites its file. This is the number the
        /// live estimate divides by <see cref="Count"/> to get a MEASURED bytes-per-record, which beats
        /// the fitted law as soon as there is one record to measure.
        /// </summary>
        long FileBytes { get; }

        /// <summary>Records currently in the file.</summary>
        long Count { get; }

        /// <summary>Every match this run found, whether or not it was written. The honest headline number.</summary>
        long TotalMatches { get; }

        /// <summary>Records the sink is keeping.</summary>
        long Kept { get; }

        /// <summary>Matches that were found and deliberately not kept.</summary>
        long Dropped { get; }

        bool IsBounded { get; }
    }
}
