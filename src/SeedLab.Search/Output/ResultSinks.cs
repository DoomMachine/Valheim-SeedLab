using System;
using SeedLab.Search.Criteria;
using SeedLab.Search.Evaluation;

namespace SeedLab.Search.Output
{
    /// <summary>
    /// Builds the sink a run's <see cref="OutputPolicy"/> asks for. One place, so "what does
    /// <c>--keep</c> mean" has one answer shared by the CLI, the web UI and the tests.
    /// </summary>
    public static class ResultSinks
    {
        /// <summary>
        /// Creates the sink. <paramref name="resumeLength"/> is the checkpoint's <c>results_length</c>
        /// (-1 for a fresh run); a bounded sink ignores it and rebuilds its file from the kept-set
        /// snapshot instead.
        /// </summary>
        public static IResultSink Create(OutputPolicy policy, Query query, CompiledQuery compiled,
                                         string engineVersion, long resumeLength = -1)
        {
            if (policy.Path == null) throw new ArgumentException("the policy has no output path", nameof(policy));
            policy.Validate();

            RecordFormatter fmt = new RecordFormatter(query, compiled, engineVersion);

            if (policy.IsRotating)
            {
                // A rotated run CANNOT be resumed in this build, and the failure would be silent and
                // total: SegmentedResultSink starts at segment 0001 and opens it with FileMode.Create,
                // so a resumed leg would overwrite the first segment and then rewrite the manifest
                // with only its own segments in it - every record of the earlier legs gone, and a
                // manifest that says so confidently. Refuse, and name both ways out. (The real fix is
                // to read the manifest, continue at segments.Count + 1, and truncate the open segment
                // back to the gzip-member boundary the checkpoint recorded; it is not written yet.)
                if (resumeLength >= 0)
                {
                    throw new InvalidOperationException(
                        "cannot resume a rotated run in this build: the segments are numbered from 0001 and "
                        + "this leg would overwrite the first one and rewrite the manifest without the others. "
                        + "Delete the checkpoint and the existing segments to run the query again from the "
                        + "beginning, or run without rotation (keep: N is bounded and resumes exactly; "
                        + "keep: all without --rotate resumes from the results file's own byte count).");
                }

                return new SegmentedResultSink(policy.Path, policy.Format, fmt, policy);
            }

            if (policy.KeepAll)
            {
                return new ResultWriter(policy.Path, policy.Format, fmt, resumeLength);
            }

            // Bounded: the file is the kept set, so it is always written from scratch (resumeLength is
            // deliberately not passed through - see BoundedResultSink).
            return new BoundedResultSink(new ResultWriter(policy.Path, policy.Format, fmt, -1), policy.Keep);
        }

        /// <summary>
        /// The one sentence a report must print about a bounded run, so "1000 matches" can never be
        /// mistaken for the whole answer.
        /// </summary>
        public static string Describe(IResultSink sink)
        {
            if (!sink.IsBounded)
            {
                return sink.Count.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) + " matches written";
            }

            string n = sink.Kept.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);
            string total = sink.TotalMatches.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);
            return sink.TotalMatches > sink.Kept
                ? "top " + n + " of " + total + " matches"
                : n + " matches (all of them; the cap was not reached)";
        }
    }
}
