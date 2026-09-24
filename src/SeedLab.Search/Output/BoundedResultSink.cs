using System;
using System.Collections.Generic;
using System.IO;
using SeedLab.Search.Evaluation;

namespace SeedLab.Search.Output
{
    /// <summary>
    /// The DEFAULT sink: a real cap on the file, not only on an in-memory table.
    ///
    /// <para>The audit's headline defect was that <c>--keep N</c> capped a display list while every
    /// passing seed was still streamed to disk, so a whole-space run of a query with no must-have goal
    /// would write 7.36 TB. Here the file <b>is</b> the kept set: records go into a
    /// <see cref="BoundedResultSet"/>, and the file is rewritten from it at every flush. Disk is
    /// <c>keep x record size</c> - 1.7 MB at keep = 1000 in JSONL - whatever the scan finds.</para>
    ///
    /// <para>The count the report must print is <see cref="TotalMatches"/>, not <see cref="Kept"/>:
    /// "top 1000 of 131,076", never "1000 matches".</para>
    /// </summary>
    public sealed class BoundedResultSink : IResultSink
    {
        private readonly ResultWriter _writer;
        private readonly ResultFormat _format;
        private readonly RecordFormatter _fmt;
        private BoundedResultSet _set;
        private string? _snapshotPath;
        private bool _dirty = true;

        public BoundedResultSink(ResultWriter writer, int keep)
        {
            _writer = writer;
            _format = writer.Format;
            _fmt = writer.Formatter;
            _set = new BoundedResultSet(keep);
        }

        /// <summary>The kept set, for the report (cut line, dropped count) and for the snapshot.</summary>
        public BoundedResultSet Set => _set;

        /// <summary>
        /// Where the kept set was last snapshotted, so a resumed run continues the same best-N rather
        /// than starting a new one: the generation the last checkpoint written names. Set by the run;
        /// null means "this run is not resumable".
        ///
        /// <para><b>Only the run writes the snapshot</b> (2026-09-24). <see cref="Flush"/> used to write
        /// it too, under the same name, before the run wrote it again and then the checkpoint - so a
        /// checkpoint save that failed after the flush left a newer snapshot beside an older
        /// checkpoint, and the resume that followed duplicated records. The snapshot is now part of
        /// the checkpoint save alone (<see cref="Execution.CheckpointStore.NextSnapshotPath"/>).</para>
        /// </summary>
        public string? SnapshotPath
        {
            get => _snapshotPath;
            set => _snapshotPath = value;
        }

        /// <summary>Restores a kept set saved by an earlier leg of this run.</summary>
        public void Restore(BoundedResultSet set)
        {
            _set = set;
            _dirty = true;
        }

        public string Path => _writer.Path;

        /// <summary>
        /// -1: a bounded file is rewritten wholesale, so there is no "resume at this byte" offset and
        /// the checkpoint must not try to truncate to one.
        /// </summary>
        public long Length => -1;

        public long FileBytes => _writer.FileBytes;

        public long Count => _set.Count;

        public long TotalMatches => _set.TotalMatches;

        public long Kept => _set.Count;

        public long Dropped => _set.Dropped;

        public bool IsBounded => true;

        public void Add(SeedResult r)
        {
            if (_set.Offer(r.Seed, r.Score, _fmt.Text(_format, r))) _dirty = true;
        }

        public void Flush()
        {
            if (_dirty)
            {
                List<string> texts = new List<string>(_set.Count);
                foreach (BoundedResultSet.Entry e in _set.Ordered()) texts.Add(e.Text);
                _writer.Rewrite(texts);
                _dirty = false;
            }

            _writer.Flush();
        }

        public void Finish()
        {
            Flush();
            _writer.Finish();
        }

        public void Dispose() => _writer.Dispose();

        /// <summary>
        /// Removes a completed run's snapshot - both generations and their temp files. A checkpoint that
        /// outlives its run is litter.
        /// </summary>
        public void DeleteSnapshot()
        {
            if (_snapshotPath == null) return;
            string ckpt = _snapshotPath.EndsWith(".top2", StringComparison.Ordinal)
                ? _snapshotPath.Substring(0, _snapshotPath.Length - 5)
                : _snapshotPath.EndsWith(".top", StringComparison.Ordinal)
                    ? _snapshotPath.Substring(0, _snapshotPath.Length - 4)
                    : _snapshotPath;
            List<string> files = new List<string> { _snapshotPath, _snapshotPath + ".tmp" };
            if (!ReferenceEquals(ckpt, _snapshotPath))
            {
                foreach (string g in Execution.CheckpointStore.SnapshotGenerations(ckpt))
                {
                    files.Add(g);
                    files.Add(g + ".tmp");
                }
            }

            foreach (string p in files)
            {
                try
                {
                    if (File.Exists(p)) File.Delete(p);
                }
                catch (IOException)
                {
                    // Litter, not data loss: a locked snapshot is not worth failing a finished run for.
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
    }
}
