using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SeedLab.Search.Criteria;
using SeedLab.Search.Evaluation;

namespace SeedLab.Search.Output
{
    public enum ResultFormat
    {
        /// <summary>One JSON object per line. The primary format: streamable, rotatable, easy to pipe.</summary>
        Jsonl,

        /// <summary>A JSON array. Valid at every flush, so it can be opened while the run is going.</summary>
        Json,

        /// <summary>A fixed, versioned column set for spreadsheets.</summary>
        Csv,
    }

    /// <summary>
    /// Streams passing seeds to disk <b>as they are found</b>, never only at the end: a multi-hour scan
    /// that is interrupted must still leave every hit it had already made.
    ///
    /// <para>Records are handed to this writer in block order by a single collector thread, so the file
    /// is byte-identical for a given <c>{query, key, range, block size}</c> however many threads ran and
    /// in whatever order the blocks finished. <see cref="Length"/> is what the checkpoint stores: on
    /// resume the file is truncated back to it, which is what makes a resumed run's result set exactly
    /// an uninterrupted run's.</para>
    ///
    /// <para><b>This is the UNBOUNDED sink.</b> It writes every record it is given, so on its own it is
    /// only safe for a run whose match count is known to be small, or one that rotates
    /// (<see cref="SegmentedResultSink"/>). The default sink is <see cref="BoundedResultSink"/>, whose
    /// file size is O(keep) whatever the scan finds - see <see cref="ResultSinks"/>.</para>
    /// </summary>
    public sealed class ResultWriter : IResultSink
    {
        private readonly FileStream _fs;
        private readonly ResultFormat _format;
        private readonly RecordFormatter _fmt;
        private long _written;
        private bool _first;

        public ResultWriter(string path, ResultFormat format, Query query, CompiledQuery compiled,
                            string engineVersion, long resumeLength = -1)
            : this(path, format, new RecordFormatter(query, compiled, engineVersion), resumeLength)
        {
        }

        public ResultWriter(string path, ResultFormat format, RecordFormatter formatter, long resumeLength = -1)
        {
            _format = format;
            _fmt = formatter;

            string? dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            // Resuming truncates the file back to the byte count the checkpoint recorded, so the run
            // continues from exactly the record boundary it stopped at - no duplicate and no half line.
            //
            // A hard kill leaves the file LONGER than the checkpoint: the last records written after
            // the checkpoint are complete records the run will produce again, and the very last one
            // may be torn in half. Truncating back to resumeLength is therefore the normal case, not
            // the exception - it is how a torn tail is repaired (measured: a kill left 94 % of the
            // file beyond results_length).
            //
            // If the file is gone, or SHORTER than the checkpoint says, the records below the resume
            // point no longer exist and this run cannot recreate them: it starts at the checkpoint's
            // block. Falling through to FileMode.Create here would wipe what is left, write only the
            // tail, and still report the full match count - a results file quietly missing thousands
            // of hits. Refuse instead, and say which of the two files to throw away.
            if (resumeLength > 0)
            {
                long have = File.Exists(path) ? new FileInfo(path).Length : -1;
                if (have < resumeLength)
                {
                    throw new InvalidOperationException(
                        "cannot resume into '" + path + "': the checkpoint was written when it held "
                        + resumeLength + " bytes of results, and it now holds "
                        + (have < 0 ? "no file at all" : have + " bytes")
                        + ". The hits below the resume point are not in it and this run starts after them. "
                        + "Delete the checkpoint to run the query from the beginning, or restore the results file.");
                }
            }

            bool resuming = resumeLength >= 0 && File.Exists(path) && new FileInfo(path).Length >= resumeLength;
            _fs = new FileStream(path, resuming ? FileMode.Open : FileMode.Create, FileAccess.Write, FileShare.Read);
            if (resuming)
            {
                _fs.SetLength(resumeLength);
                _fs.Position = resumeLength;
                _written = resumeLength;
                // A JSON array file holds "[" and nothing else when no record has been written yet.
                _first = format == ResultFormat.Json && resumeLength <= 1;
                if (format == ResultFormat.Csv && resumeLength == 0) Raw(_fmt.CsvHeader());
                if (format == ResultFormat.Json && resumeLength == 0) Raw("[");
            }
            else
            {
                _written = 0;
                _first = true;
                if (format == ResultFormat.Csv) Raw(_fmt.CsvHeader());
                else if (format == ResultFormat.Json) Raw("[");
            }
        }

        public string Path => _fs.Name;

        public ResultFormat Format => _format;

        public RecordFormatter Formatter => _fmt;

        /// <summary>Bytes of complete records on disk. The checkpoint stores this; a resume truncates to it.</summary>
        public long Length => _written;

        public long FileBytes => _written;

        public long Count { get; private set; }

        /// <summary>Every record offered is written by this sink, so the two counts are the same.</summary>
        public long TotalMatches => Count;

        public long Kept => Count;

        public long Dropped => 0;

        public bool IsBounded => false;

        private void Raw(string s)
        {
            byte[] b = Encoding.UTF8.GetBytes(s);
            _fs.Write(b, 0, b.Length);
            _written += b.Length;
        }

        public void Write(SeedResult r) => Add(r);

        public void Add(SeedResult r)
        {
            Count++;
            if (_format == ResultFormat.Json)
            {
                Raw((_first ? "\n  " : ",\n  ") + _fmt.Record(r));
                _first = false;
                return;
            }

            Raw(_fmt.Text(_format, r));
        }

        /// <summary>One result as a single-line JSON object. The same shape in JSONL and in the array form.</summary>
        public string Record(SeedResult r) => _fmt.Record(r);

        /// <summary>
        /// Replaces the whole file with these records, in this order. This is how a bounded run
        /// writes: its file is the kept set, so it is rewritten rather than appended to, and the
        /// result is a file of exactly <c>keep</c> records however many seeds were scanned.
        ///
        /// <para>A kill during the rewrite leaves a partial file, which is why a bounded run's
        /// checkpoint carries the kept set itself (<see cref="BoundedResultSet.SaveSnapshot"/>): the
        /// next flush rewrites the file from the snapshot, so the damage is always repaired rather
        /// than resumed into.</para>
        /// </summary>
        public void Rewrite(IReadOnlyList<string> texts)
        {
            _fs.Position = 0;
            _fs.SetLength(0);
            _written = 0;
            _first = true;
            Count = 0;

            if (_format == ResultFormat.Csv) Raw(_fmt.CsvHeader());
            else if (_format == ResultFormat.Json) Raw("[");

            foreach (string t in texts)
            {
                if (_format == ResultFormat.Json)
                {
                    Raw((_first ? "\n  " : ",\n  ") + t);
                    _first = false;
                }
                else
                {
                    Raw(t);
                }

                Count++;
            }
        }

        public static string UnitName(Unit u) => RecordFormatter.UnitName(u);

        public void Flush()
        {
            if (_format == ResultFormat.Json)
            {
                long at = _fs.Position;
                byte[] tail = Encoding.UTF8.GetBytes(_first ? "]" : "\n]");
                _fs.Write(tail, 0, tail.Length);
                _fs.Flush(true);
                _fs.Position = at;      // the next record overwrites the temporary closing bracket
                _fs.SetLength(at + tail.Length);
                return;
            }

            _fs.Flush(true);
        }

        public void Finish()
        {
            if (_format == ResultFormat.Json)
            {
                _fs.Position = _written;
                byte[] tail = Encoding.UTF8.GetBytes(_first ? "]\n" : "\n]\n");
                _fs.Write(tail, 0, tail.Length);
                _fs.SetLength(_written + tail.Length);
            }

            _fs.Flush(true);
        }

        public void Dispose() => _fs.Dispose();

        public static ResultFormat FormatFor(string path)
        {
            string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
            return ext switch
            {
                ".csv" => ResultFormat.Csv,
                ".json" => ResultFormat.Json,
                _ => ResultFormat.Jsonl,
            };
        }
    }
}
