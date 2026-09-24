using System;
using System.Collections.Generic;
using System.Text;
using SeedLab.WorldGen;
using SeedLab.WorldGen.Unity;

namespace SeedLabAcceptanceTests
{
    /// <summary>
    /// Localises a height disagreement to a single river point.
    ///
    /// WorldGenerator.GetWeight (decomp 666-693) sets `weight` to the MAXIMUM of
    /// (1 - distance / point.w) over the points of one 64 m river-grid cell, and AddRivers
    /// (decomp 937-957) then lerps the normalised height toward the river bed by that weight. So a
    /// height that is off by dH implies a weight that is off by dH / (200 * |bed - h|), which implies
    /// the single dominating point is off in position by about that times its radius. Reporting which
    /// river or stream that point belongs to tells a reader whether the residual is spread evenly over
    /// the whole river field (per-point noise) or concentrated on a few watercourses (an endpoint that
    /// differs in its last bit, which would shift every point of that river together).
    /// </summary>
    public static class RiverDiagnostic
    {
        public sealed class Hit
        {
            public float X, Z;
            public float Weight;
            public float PointRadius;
            public Vec2 Point;
            public int SegmentId = -1;      // index into rivers, or 100000 + index into streams
            public float SegmentDistance = float.NaN;
            public bool IsStreamRadius;
        }

        public static void Describe(WorldGeneratorPort gen, List<(float x, float z)> points, string label)
        {
            IReadOnlyList<WorldGeneratorPort.River> rivers = gen.GetRivers();
            IReadOnlyList<WorldGeneratorPort.River> streams = gen.GetStreams();
            IReadOnlyDictionary<Vec2i, WorldGeneratorPort.RiverPoint[]> field = gen.GetRiverPoints();

            List<Hit> hits = new List<Hit>();
            foreach ((float x, float z) in points)
            {
                Vec2i cell = gen.GetRiverGrid(x, z);
                if (!field.TryGetValue(cell, out WorldGeneratorPort.RiverPoint[]? arr)) continue;
                Hit best = new Hit { X = x, Z = z, Weight = 0f };
                Vec2 q = new Vec2(x, z);
                bool any = false;
                foreach (WorldGeneratorPort.RiverPoint rp in arr)
                {
                    float d2 = Vec2.SqrMagnitude(rp.p - q);
                    if (d2 >= rp.w2) continue;
                    float d = (float)Math.Sqrt((double)d2);
                    float w = (float)(1.0 - (double)d / (double)rp.w);
                    if (w > best.Weight || !any)
                    {
                        any = true;
                        best.Weight = w;
                        best.Point = rp.p;
                        best.PointRadius = rp.w;
                    }
                }
                if (!any) continue;
                best.IsStreamRadius = best.PointRadius < 40f;
                Classify(best, rivers, streams);
                hits.Add(best);
            }

            if (hits.Count == 0)
            {
                Console.WriteLine("    " + label + ": none of the differing samples sits in a river cell.");
                return;
            }

            Dictionary<int, int> bySegment = new Dictionary<int, int>();
            int unmatched = 0, streamRadius = 0;
            foreach (Hit h in hits)
            {
                if (h.SegmentId < 0) unmatched++;
                else bySegment[h.SegmentId] = bySegment.TryGetValue(h.SegmentId, out int c) ? c + 1 : 1;
                if (h.IsStreamRadius) streamRadius++;
            }

            List<KeyValuePair<int, int>> ordered = new List<KeyValuePair<int, int>>(bySegment);
            ordered.Sort((a, b) => b.Value != a.Value ? b.Value.CompareTo(a.Value) : a.Key.CompareTo(b.Key));

            Console.WriteLine("    " + label + ": " + hits.Count + " of the differing samples have a dominating river point ("
                              + streamRadius + " of them with a stream radius of ~20 m)");
            Console.WriteLine("      they belong to " + bySegment.Count + " distinct watercourses out of "
                              + rivers.Count + " rivers + " + streams.Count + " surface streams"
                              + (unmatched > 0 ? "; " + unmatched + " could not be matched (DeepNorth streams are not retained by the game's m_streams list)" : ""));
            StringBuilder sb = new StringBuilder("      busiest: ");
            for (int i = 0; i < Math.Min(8, ordered.Count); i++)
            {
                int id = ordered[i].Key;
                sb.Append(id >= 100000 ? "stream#" + (id - 100000) : "river#" + id)
                  .Append('x').Append(ordered[i].Value).Append("  ");
            }
            Console.WriteLine(sb.ToString());
        }

        private static void Classify(Hit h, IReadOnlyList<WorldGeneratorPort.River> rivers,
                                     IReadOnlyList<WorldGeneratorPort.River> streams)
        {
            float best = float.MaxValue;
            for (int i = 0; i < rivers.Count; i++)
            {
                float d = SegmentDistance(h.Point, rivers[i].p0, rivers[i].p1);
                if (d < best) { best = d; h.SegmentId = i; }
            }
            for (int i = 0; i < streams.Count; i++)
            {
                float d = SegmentDistance(h.Point, streams[i].p0, streams[i].p1);
                if (d < best) { best = d; h.SegmentId = 100000 + i; }
            }
            h.SegmentDistance = best;
            // A rendered point is displaced from its segment by at most curveWidth (= length/15), so a
            // match further than that is not a match at all.
            if (best > 400f) h.SegmentId = -1;
        }

        private static float SegmentDistance(Vec2 p, Vec2 a, Vec2 b)
        {
            float vx = b.x - a.x, vy = b.y - a.y;
            float wx = p.x - a.x, wy = p.y - a.y;
            float vv = vx * vx + vy * vy;
            float t = vv <= 0f ? 0f : (wx * vx + wy * vy) / vv;
            if (t < 0f) t = 0f; else if (t > 1f) t = 1f;
            float dx = wx - t * vx, dy = wy - t * vy;
            return (float)Math.Sqrt((double)(dx * dx + dy * dy));
        }
    }
}
