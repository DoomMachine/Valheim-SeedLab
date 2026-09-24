using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Threading;

namespace SeedLab.Runtime.SelfTest
{
    /// <summary>
    /// <c>libm-dense</c>: the C runtime's math functions over 65,536 arguments per call site, in the
    /// exact shapes the generator calls them, reduced to two SHA-256 digests per site - the raw double
    /// results, and the values as the generator consumes them (after its own narrowing to float, where
    /// it narrows).
    ///
    /// <para><b>Why a dense suite as well as the numeric goldens.</b> The goldens hold 93 libm values the
    /// game itself recorded. They catch a C runtime that differs at those points - the non-FMA3 path of
    /// <c>ucrtbase.dll</c> is caught by <c>Cos(1.0)</c> and <c>Cos(20.0)</c> - but a library that differs
    /// in the last bit somewhere else in SeedLab's argument ranges (another Windows build's ucrtbase,
    /// another C library on another OS) would pass 93 points and still move a biome boundary. Here every
    /// site is swept densely, so such a library is caught wherever the generator would feel it.</para>
    ///
    /// <para><b>The sites</b> (the expressions are copied from the port, including where it narrows):</para>
    /// <list type="bullet">
    /// <item><c>world-angle</c> - <c>WorldAngle</c>: <c>(float)Math.Sin((double)(float)((double)(float)Math.Atan2(x, z) * 20.0))</c>
    /// over points of the 10.5 km world disc;</item>
    /// <item><c>river-curve</c> - <c>RenderRivers</c>: three double sines of <c>t</c>, <c>0.634t</c>,
    /// <c>0.334t</c>, times the curve width, one narrowing;</item>
    /// <item><c>sin-cos-float</c> - <c>Mathf.Sin/Cos(float)</c> and <c>insideUnitCircle</c>:
    /// <c>(float)Math.Sin((double)a)</c> and <c>Cos</c> over angles in [0, 2 pi) and a wider band;</item>
    /// <item><c>pow-1.5</c> - the Mistlands' <c>(float)Math.Pow(m, 1.5)</c> over (0, 1.5];</item>
    /// <item><c>ashlands-pow</c> - <c>Math.Pow(x, 4.0)</c>, <c>Pow(x, 1.4f)</c> and <c>Pow(x, 2.0)</c> over
    /// [0, 1], consumed in double (so raw and consumed are the same values).</item>
    /// </list>
    ///
    /// <para><b>Fails closed on either digest.</b> A raw difference that happens to vanish after
    /// narrowing at these 65,536 points is not a proof that it vanishes everywhere, so it is a failure
    /// too; <c>vseed selftest --report</c> prints both digests, so which one moved is visible. The
    /// arguments come from a fixed xorshift sequence - no data file, no randomness. The reference digests
    /// below were recorded on the machine whose output the gates compare against the game itself (the
    /// runtime tests' <c>--emit-libm-dense</c> prints them); recording anywhere else would bake that
    /// machine's library in as the reference.</para>
    /// </summary>
    public sealed class LibmDense : ISelfTestSuite
    {
        /// <summary>Arguments per site.</summary>
        public const int ArgumentsPerSite = 65536;

        /// <summary>
        /// (site, raw digest, consumed digest), recorded 2026-09-24 on win-x64, .NET 10.0.12, ucrtbase
        /// 10.0.19041.3636 (its FMA3 path) - the machine whose libm matches the game's 93 recorded values.
        /// </summary>
        private static readonly (string Site, string Raw, string Consumed)[] Reference =
        {
            ("world-angle", "c6bcc4077b18085125208a8eb93b6d790cb0469740621e7e84a43ac4f5933900", "97a61d289eeffe65d51506273c1556cf930b11edc93ec2c2c530fc23d3fa2fc1"),
            ("river-curve", "4443995e9ec9c1ab14fe7faf14b711299fd6ec01e110db4023b7dd6e413dbfe9", "ed512b7abbe0ec3f911a0f91a548d69c8cb88e88bf97141aa768a017245002fb"),
            ("sin-cos-float", "b948d56e191d0b2e7f14f3d4cb834325dd273a0f265ad6bac317394bb52337cd", "fdb7b48ba7866c05a3264648dcfdf61287ebe4589a14206ccd61c71f7d208466"),
            ("pow-1.5", "92e0b6c69f0c670149549094dbd3f160d2b302e7ad9541f666cc1a4621a8f8c2", "395479766d763a3269e60208dd173ab785e101c06d587d961833b7fa884d9180"),
            ("ashlands-pow", "8c1b1c6efc430052af06e99b0c70b420ec19456e06ad2fa71ac05e4447651489", "8c1b1c6efc430052af06e99b0c70b420ec19456e06ad2fa71ac05e4447651489"),
        };

        public string Name => "libm-dense";

        public string Describes =>
            "Math.Atan2/Sin/Cos/Pow over 65,536 arguments at each of the generator's call sites, raw and as consumed: "
            + "a C runtime that differs anywhere a world would feel it";

        /// <summary>One site's two digests, computed now.</summary>
        public sealed class SiteDigest
        {
            public SiteDigest(string site, string raw, string consumed)
            {
                Site = site;
                Raw = raw;
                Consumed = consumed;
            }

            public string Site { get; }
            public string Raw { get; }
            public string Consumed { get; }
        }

        public SelfTestSuiteResult Run(CancellationToken cancel)
        {
            Stopwatch sw = Stopwatch.StartNew();
            SelfTestSuiteResult r = Check(Compute(cancel));
            sw.Stop();
            return new SelfTestSuiteResult(r.Name, r.Checks, r.Failures, r.FirstFailure, sw.Elapsed);
        }

        /// <summary>
        /// Compares digests with the recorded reference: two checks per site, raw and consumed. Public so a
        /// test can hand it a digest that differs and see it fail.
        /// </summary>
        public static SelfTestSuiteResult Check(IReadOnlyList<SiteDigest> now)
        {
            int checks = 0, failures = 0;
            string first = "";
            foreach ((string site, string raw, string consumed) in Reference)
            {
                SiteDigest? d = null;
                foreach (SiteDigest x in now) if (x.Site == site) d = x;
                checks += 2;
                if (d == null || raw.Length == 0 || consumed.Length == 0)
                {
                    failures += 2;
                    if (first.Length == 0)
                    {
                        first = "site " + site + " has no recorded digest - re-record with "
                                + "'dotnet run --project tests/SeedLab.Runtime.Tests -- --emit-libm-dense'";
                    }

                    continue;
                }

                if (!string.Equals(d.Raw, raw, StringComparison.Ordinal))
                {
                    failures++;
                    if (first.Length == 0) first = site + ": the raw results differ from the reference (" + Short(d.Raw) + " vs " + Short(raw) + ")";
                }

                if (!string.Equals(d.Consumed, consumed, StringComparison.Ordinal))
                {
                    failures++;
                    if (first.Length == 0) first = site + ": the values the generator consumes differ from the reference (" + Short(d.Consumed) + " vs " + Short(consumed) + ")";
                }
            }

            return new SelfTestSuiteResult("libm-dense", checks, failures, first, TimeSpan.Zero);
        }

        private static string Short(string hex) => hex.Length > 16 ? hex.Substring(0, 16) + "..." : hex;

        /// <summary>The digests this machine computes, in site order. Pure: no I/O, no state.</summary>
        public static IReadOnlyList<SiteDigest> Compute(CancellationToken cancel = default)
        {
            List<SiteDigest> list = new List<SiteDigest>();
            list.Add(WorldAngle(cancel));
            list.Add(RiverCurve(cancel));
            list.Add(SinCosFloat(cancel));
            list.Add(Pow15(cancel));
            list.Add(AshlandsPow(cancel));
            return list;
        }

        /// <summary>The recorded reference, for reports: (site, raw, consumed).</summary>
        public static IReadOnlyList<(string Site, string Raw, string Consumed)> Recorded => Reference;

        // ---- the sites ----------------------------------------------------------------------------------

        private static SiteDigest WorldAngle(CancellationToken cancel)
        {
            using Sink raw = new Sink();
            using Sink used = new Sink();
            Xorshift r = new Xorshift(0x5EED0001u);
            for (int i = 0; i < ArgumentsPerSite; i++)
            {
                if ((i & 4095) == 0) cancel.ThrowIfCancellationRequested();
                // Points of the world disc and a little beyond, as floats: the lattice the grids walk
                // (multiples of 12 m, half-offset) for a quarter of them, uniform floats for the rest.
                float wx, wy;
                if ((i & 3) == 0)
                {
                    wx = (float)(((int)(r.Next() % 1750u) - 875) * 12 + 6);
                    wy = (float)(((int)(r.Next() % 1750u) - 875) * 12 + 6);
                }
                else
                {
                    wx = (float)((r.Unit() - 0.5) * 21600.0);
                    wy = (float)((r.Unit() - 0.5) * 21600.0);
                }

                double atan = Math.Atan2((double)wx, (double)wy);
                double sin = Math.Sin((double)(float)((double)(float)atan * 20.0));
                raw.F64(atan);
                raw.F64(sin);
                used.F32((float)sin);
            }

            return new SiteDigest("world-angle", raw.Hex(), used.Hex());
        }

        private static SiteDigest RiverCurve(CancellationToken cancel)
        {
            using Sink raw = new Sink();
            using Sink used = new Sink();
            Xorshift r = new Xorshift(0x5EED0002u);
            for (int i = 0; i < ArgumentsPerSite; i++)
            {
                if ((i & 4095) == 0) cancel.ThrowIfCancellationRequested();
                // t = s / curveWavelength: a float from 0 up to a river's length over its wavelength.
                float t = (float)(r.Unit() * 320.0);
                float width = (float)(r.Unit() * 120.0);
                double a = Math.Sin(t);
                double b = Math.Sin((double)t * 0.634119987487793);
                double c = Math.Sin((double)t * 0.3341200053691864);
                raw.F64(a);
                raw.F64(b);
                raw.F64(c);
                used.F32((float)(a * b * c * (double)width));
            }

            return new SiteDigest("river-curve", raw.Hex(), used.Hex());
        }

        private static SiteDigest SinCosFloat(CancellationToken cancel)
        {
            using Sink raw = new Sink();
            using Sink used = new Sink();
            Xorshift r = new Xorshift(0x5EED0003u);
            for (int i = 0; i < ArgumentsPerSite; i++)
            {
                if ((i & 4095) == 0) cancel.ThrowIfCancellationRequested();
                // Random.Range(0f, 2 pi) for stream angles and insideUnitCircle, and a wider band for any
                // Mathf.Sin/Cos of a float that is not an angle in one turn.
                float a = (i & 7) == 0 ? (float)((r.Unit() - 0.5) * 200.0) : (float)(r.Unit() * 6.2831854820251465);
                double s = Math.Sin((double)a);
                double c = Math.Cos((double)a);
                raw.F64(s);
                raw.F64(c);
                used.F32((float)s);
                used.F32((float)c);
            }

            return new SiteDigest("sin-cos-float", raw.Hex(), used.Hex());
        }

        private static SiteDigest Pow15(CancellationToken cancel)
        {
            using Sink raw = new Sink();
            using Sink used = new Sink();
            Xorshift r = new Xorshift(0x5EED0004u);
            for (int i = 0; i < ArgumentsPerSite; i++)
            {
                if ((i & 4095) == 0) cancel.ThrowIfCancellationRequested();
                // m is a product and sum of Perlin samples, positive when Pow is reached.
                float m = (float)(r.Unit() * 1.5);
                if (!(m > 0f)) m = float.Epsilon;
                double p = Math.Pow(m, 1.5);
                raw.F64(p);
                used.F32((float)p);
            }

            return new SiteDigest("pow-1.5", raw.Hex(), used.Hex());
        }

        private static SiteDigest AshlandsPow(CancellationToken cancel)
        {
            using Sink raw = new Sink();
            Xorshift r = new Xorshift(0x5EED0005u);
            for (int i = 0; i < ArgumentsPerSite; i++)
            {
                if ((i & 4095) == 0) cancel.ThrowIfCancellationRequested();
                // Remapped noise in [0, 1], consumed as double all the way to the final narrowing.
                double x = r.Unit();
                raw.F64(Math.Pow(x, 4.0));
                raw.F64(Math.Pow(x, 1.399999976158142));
                raw.F64(Math.Pow(x, 2.0));
            }

            string h = raw.Hex();
            return new SiteDigest("ashlands-pow", h, h);
        }

        // ---- helpers ------------------------------------------------------------------------------------

        /// <summary>xorshift32: a fixed, portable argument sequence.</summary>
        private struct Xorshift
        {
            private uint _s;

            public Xorshift(uint seed) => _s = seed;

            public uint Next()
            {
                uint x = _s;
                x ^= x << 13;
                x ^= x >> 17;
                x ^= x << 5;
                _s = x;
                return x;
            }

            /// <summary>[0, 1) with 32 bits of the sequence, exact in double.</summary>
            public double Unit() => Next() / 4294967296.0;
        }

        /// <summary>SHA-256 over little-endian bit patterns through a buffer.</summary>
        private sealed class Sink : IDisposable
        {
            private readonly IncrementalHash _h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            private readonly byte[] _buf = new byte[1 << 15];
            private int _n;

            public void F64(double v)
            {
                if (_n + 8 > _buf.Length) Flush();
                BinaryPrimitives.WriteInt64LittleEndian(_buf.AsSpan(_n), BitConverter.DoubleToInt64Bits(v));
                _n += 8;
            }

            public void F32(float v)
            {
                if (_n + 4 > _buf.Length) Flush();
                BinaryPrimitives.WriteInt32LittleEndian(_buf.AsSpan(_n), BitConverter.SingleToInt32Bits(v));
                _n += 4;
            }

            private void Flush()
            {
                _h.AppendData(_buf, 0, _n);
                _n = 0;
            }

            public string Hex()
            {
                Flush();
                return Convert.ToHexString(_h.GetHashAndReset()).ToLowerInvariant();
            }

            public void Dispose() => _h.Dispose();
        }
    }
}
