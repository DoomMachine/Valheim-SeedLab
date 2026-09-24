using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SeedLab.Search.Evaluation;

namespace SeedLab.Search.Output
{
    /// <summary>One closed segment, as the manifest records it.</summary>
    public sealed class SegmentInfo
    {
        public int Index;
        public string File = "";
        public long Records;

        /// <summary>Bytes of records before compression - the number the rotation threshold counts.</summary>
        public long RawBytes;

        /// <summary>Bytes the file actually occupies.</summary>
        public long FileBytes;

        public string Sha256 = "";
        public int FirstSeed;
        public int LastSeed;

        /// <summary>Set when the segment was reduced and its raw records deleted.</summary>
        public string? ReducedBy;

        public long ReducedRecords;
    }

    /// <summary>
    /// <c>keep: all</c> done safely: the stream is cut into segments of a fixed size, each gzipped,
    /// listed in a manifest, and optionally reduced-then-deleted when it closes.
    ///
    /// <para><b>Why segments.</b> A whole-space unbounded run is 0.56-7.4 TB. One file that size is
    /// unopenable, unmovable and unresumable; ~7,000 1 GB segments with a manifest are all three. The
    /// size is the decided 1 GB of <i>uncompressed</i> records, so a segment holds a predictable
    /// 585,000 JSONL records whatever the compression achieves.</para>
    ///
    /// <para><b>Why multi-member gzip.</b> Every checkpoint closes the current gzip member and starts
    /// a new one in the same file. A gzip file is a concatenation of members, so the file is complete
    /// and decompressible at <i>every</i> checkpoint, and the checkpoint's <c>results_length</c> is
    /// always a member boundary that a resume can truncate back to. Without that, a hard kill would
    /// leave a deflate stream cut mid-block, which no reader can finish and no resume can extend.</para>
    ///
    /// <para><b>Why the reduction is restricted.</b> <c>--reduce</c> deletes the raw segment, so the
    /// whole run's answer has to be rebuildable from the reduced pieces. <see cref="ReduceSpec"/>
    /// allows only reductions for which that is exactly true and refuses the rest by name.</para>
    /// </summary>
    public sealed class SegmentedResultSink : IResultSink
    {
        private readonly string _basePath;
        private readonly string _stem;
        private readonly string _ext;
        private readonly ResultFormat _format;
        private readonly RecordFormatter _fmt;
        private readonly OutputPolicy _policy;
        private readonly List<SegmentInfo> _segments = new List<SegmentInfo>();

        private FileStream? _file;
        private Stream? _sink;          // the gzip member, or the file itself when not compressing
        private int _index;
        private long _segmentRaw;
        private long _segmentRecords;
        private int _firstSeed, _lastSeed;
        private bool _segmentOpen;

        // The reduction's running answer for the OPEN segment. Held in memory because the raw records
        // are about to be deleted: by the time the segment closes, this is all that survives of it.
        private BoundedResultSet? _reduce;

        public SegmentedResultSink(string basePath, ResultFormat format, RecordFormatter formatter,
                                   OutputPolicy policy)
        {
            if (format == ResultFormat.Json)
            {
                throw new SeedLab.Search.Criteria.QueryException("rotate",
                    "a rotated JSON array is not valid JSON", "write .jsonl or .csv");
            }

            _basePath = System.IO.Path.GetFullPath(basePath);
            _format = format;
            _fmt = formatter;
            _policy = policy;

            string? dir = System.IO.Path.GetDirectoryName(_basePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            _ext = System.IO.Path.GetExtension(_basePath);
            _stem = _basePath.Substring(0, _basePath.Length - _ext.Length);
            ManifestPath = _stem + ".manifest.json";
        }

        public string Path => _basePath;

        public string ManifestPath { get; }

        public IReadOnlyList<SegmentInfo> Segments => _segments;

        /// <summary>The open segment's file length at the last flush: a gzip member boundary.</summary>
        public long Length { get; private set; } = -1;

        /// <summary>Every closed segment plus the open one, as they stand on disk.</summary>
        public long FileBytes
        {
            get
            {
                long total = 0;
                foreach (SegmentInfo s in _segments) total += s.FileBytes;
                if (_file != null)
                {
                    try
                    {
                        total += _file.Length;
                    }
                    catch (ObjectDisposedException)
                    {
                    }
                }

                return total;
            }
        }

        public long Count { get; private set; }

        public long TotalMatches => Count;

        /// <summary>
        /// Records that will survive: every closed segment's contribution plus what the OPEN segment
        /// is currently holding. Counting only the closed ones under-reported every run, because the
        /// last segment is closed by <see cref="Finish"/> - which runs after the caller has already
        /// read this number off the outcome.
        /// </summary>
        public long Kept => _keptAfterReduction
                            + (_segmentOpen ? (_reduce != null ? _reduce.Count : _segmentRecords) : 0);

        public long Dropped => Count - Kept;

        public bool IsBounded => false;

        private long _keptAfterReduction;

        public void Add(SeedResult r)
        {
            string text = _fmt.Text(_format, r);
            byte[] bytes = Encoding.UTF8.GetBytes(text);

            if (!_segmentOpen) OpenSegment();
            if (_segmentRecords == 0) _firstSeed = r.Seed;
            _lastSeed = r.Seed;

            _sink!.Write(bytes, 0, bytes.Length);
            _segmentRaw += bytes.Length;
            _segmentRecords++;
            Count++;
            _reduce?.Offer(r.Seed, r.Score, text);

            if (_policy.RotateBytes > 0 && _segmentRaw >= _policy.RotateBytes) CloseSegment();
        }

        /// <summary>
        /// Ends the current gzip member and fsyncs, so the file on disk is a complete, readable gzip
        /// and its length is a boundary a resume can truncate to. Costs a few hundred bytes of
        /// compression efficiency per checkpoint and buys crash safety.
        /// </summary>
        public void Flush()
        {
            if (!_segmentOpen || _file == null) return;

            if (_policy.Compress)
            {
                _sink!.Dispose();               // writes this member's CRC and length
                _file.Flush(true);
                Length = _file.Length;
                _sink = new GZipStream(_file, CompressionLevel.Fastest, leaveOpen: true);
            }
            else
            {
                _sink!.Flush();
                _file.Flush(true);
                Length = _file.Length;
            }

            WriteManifest(complete: false);
        }

        public void Finish()
        {
            if (_segmentOpen) CloseSegment();
            WriteManifest(complete: true);
        }

        public void Dispose()
        {
            try
            {
                if (_segmentOpen) CloseSegment();
            }
            finally
            {
                _sink?.Dispose();
                _file?.Dispose();
                _sink = null;
                _file = null;
                _segmentOpen = false;
            }
        }

        private void OpenSegment()
        {
            _index++;
            string name = _stem + "." + _index.ToString("0000", CultureInfo.InvariantCulture) + _ext
                          + (_policy.Compress ? ".gz" : "");
            _file = new FileStream(name, FileMode.Create, FileAccess.Write, FileShare.Read);
            _sink = _policy.Compress
                ? new GZipStream(_file, CompressionLevel.Fastest, leaveOpen: true)
                : (Stream)_file;
            _segmentRaw = 0;
            _segmentRecords = 0;
            _segmentOpen = true;
            Length = 0;
            _reduce = _policy.Reduce.Kind == "top" ? new BoundedResultSet(_policy.Reduce.N) : null;

            // Every segment is a file someone will open on its own, so a CSV segment carries the
            // header. Without it, segment 0002 onwards is a headerless CSV whose columns are only
            // discoverable by reading a different file.
            if (_format == ResultFormat.Csv)
            {
                byte[] h = Encoding.UTF8.GetBytes(_fmt.CsvHeader());
                _sink.Write(h, 0, h.Length);
                _segmentRaw += h.Length;
            }
        }

        private void CloseSegment()
        {
            if (!_segmentOpen || _file == null) return;
            string name = _file.Name;

            if (_policy.Compress) _sink!.Dispose();
            else _sink!.Flush();
            _file.Flush(true);
            _file.Dispose();
            _sink = null;
            _file = null;
            _segmentOpen = false;

            SegmentInfo info = new SegmentInfo
            {
                Index = _index,
                File = System.IO.Path.GetFileName(name),
                Records = _segmentRecords,
                RawBytes = _segmentRaw,
                FileBytes = new FileInfo(name).Length,
                Sha256 = Sha256Of(name),
                FirstSeed = _firstSeed,
                LastSeed = _lastSeed,
            };

            if (_reduce != null)
            {
                // The user's "analyse then discard": reduce the closed segment, keep the reduction,
                // delete the raw segment. Top-N merges exactly, so the run's overall top-N is still
                // the true one - which is the only reason this is allowed to delete anything.
                string reducedName = _stem + "." + _index.ToString("0000", CultureInfo.InvariantCulture)
                                     + ".top" + _policy.Reduce.N.ToString(CultureInfo.InvariantCulture) + _ext;
                using (FileStream rf = new FileStream(reducedName, FileMode.Create, FileAccess.Write, FileShare.Read))
                {
                    if (_format == ResultFormat.Csv)
                    {
                        byte[] h = Encoding.UTF8.GetBytes(_fmt.CsvHeader());
                        rf.Write(h, 0, h.Length);
                    }

                    foreach (BoundedResultSet.Entry e in _reduce.Ordered())
                    {
                        byte[] b = Encoding.UTF8.GetBytes(e.Text);
                        rf.Write(b, 0, b.Length);
                    }

                    rf.Flush(true);
                }

                File.Delete(name);
                info.ReducedBy = _policy.Reduce.Text;
                info.ReducedRecords = _reduce.Count;
                info.File = System.IO.Path.GetFileName(reducedName);
                info.FileBytes = new FileInfo(reducedName).Length;
                info.Sha256 = Sha256Of(reducedName);
                _keptAfterReduction += _reduce.Count;
                _reduce = null;
            }
            else
            {
                _keptAfterReduction += _segmentRecords;
            }

            _segments.Add(info);
            Length = -1;
            WriteManifest(complete: false);
        }

        /// <summary>
        /// The map of ~7,000 segments. Rewritten atomically at every rotation and every flush, so a
        /// killed run still leaves a manifest that describes everything that closed.
        /// </summary>
        private void WriteManifest(bool complete)
        {
            StringBuilder sb = new StringBuilder(4096);
            sb.Append("{\n");
            sb.Append("  \"version\": 1,\n");
            sb.Append("  \"base\": ").Append(JsonSerializer.Serialize(System.IO.Path.GetFileName(_basePath))).Append(",\n");
            sb.Append("  \"format\": \"").Append(_format.ToString().ToLowerInvariant()).Append("\",\n");
            sb.Append("  \"compressed\": ").Append(_policy.Compress ? "true" : "false").Append(",\n");
            sb.Append("  \"rotate_bytes\": ").Append(_policy.RotateBytes.ToString(CultureInfo.InvariantCulture)).Append(",\n");
            sb.Append("  \"reduce\": ").Append(JsonSerializer.Serialize(_policy.Reduce.Text)).Append(",\n");
            sb.Append("  \"complete\": ").Append(complete ? "true" : "false").Append(",\n");
            sb.Append("  \"records\": ").Append(Count.ToString(CultureInfo.InvariantCulture)).Append(",\n");
            sb.Append("  \"segments\": [\n");
            for (int i = 0; i < _segments.Count; i++)
            {
                SegmentInfo s = _segments[i];
                sb.Append("    {\"index\": ").Append(s.Index);
                sb.Append(", \"file\": ").Append(JsonSerializer.Serialize(s.File));
                sb.Append(", \"records\": ").Append(s.Records.ToString(CultureInfo.InvariantCulture));
                sb.Append(", \"raw_bytes\": ").Append(s.RawBytes.ToString(CultureInfo.InvariantCulture));
                sb.Append(", \"file_bytes\": ").Append(s.FileBytes.ToString(CultureInfo.InvariantCulture));
                sb.Append(", \"first_seed\": ").Append(s.FirstSeed.ToString(CultureInfo.InvariantCulture));
                sb.Append(", \"last_seed\": ").Append(s.LastSeed.ToString(CultureInfo.InvariantCulture));
                if (s.ReducedBy != null)
                {
                    sb.Append(", \"reduced_by\": ").Append(JsonSerializer.Serialize(s.ReducedBy));
                    sb.Append(", \"reduced_records\": ").Append(s.ReducedRecords.ToString(CultureInfo.InvariantCulture));
                    sb.Append(", \"raw_deleted\": true");
                }

                sb.Append(", \"sha256\": \"").Append(s.Sha256).Append("\"}");
                if (i < _segments.Count - 1) sb.Append(',');
                sb.Append('\n');
            }

            sb.Append("  ]\n}\n");

            string tmp = ManifestPath + ".tmp";
            using (FileStream fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                byte[] b = Encoding.UTF8.GetBytes(sb.ToString());
                fs.Write(b, 0, b.Length);
                fs.Flush(true);
            }

            File.Move(tmp, ManifestPath, overwrite: true);
        }

        private static string Sha256Of(string path)
        {
            using FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using SHA256 sha = SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();
        }
    }
}
