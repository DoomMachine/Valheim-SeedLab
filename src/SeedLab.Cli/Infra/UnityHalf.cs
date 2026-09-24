using System;
using System.Runtime.CompilerServices;

namespace SeedLab.Cli.Infra
{
    /// <summary>
    /// The binary16 encoder that <c>cacheMinimapHeight</c> was written with.
    ///
    /// <para><c>Utils.FloatsToCompressedHalfBuffer</c> stores every minimap height as
    /// <c>UnityEngine.Mathf.FloatToHalf(h)</c>, which is a native extern - its rounding rule cannot be
    /// read out of managed code. It was <b>measured</b> instead: of the height pixels where .NET's
    /// ties-to-even <c>(Half)f</c> disagreed with the game, all but a handful sat exactly on a half
    /// midpoint, and every one of those matched once the tie was broken <b>away from zero</b>
    /// (2026-09-22, on both ground-truth worlds' minimap caches; the acceptance suite prints the tie
    /// census that shows it, and <c>tests\SeedLab.Tests</c> checks the rule against the dumped
    /// <c>natives-half.json</c> golden). So this is .NET's conversion with the midpoints redirected.</para>
    ///
    /// <para>The decode direction lives in <c>SeedLab.Saves.Half16</c> and is not duplicated here.</para>
    /// </summary>
    public static class UnityHalf
    {
        /// <summary>IEEE-754 binary16, ties to even - what <c>(Half)f</c> does.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ushort NetBits(float v) => BitConverter.HalfToUInt16Bits((Half)v);

        /// <summary>Same, except an exact midpoint rounds away from zero: <c>Mathf.FloatToHalf</c>.</summary>
        public static ushort Bits(float v) => Bits(v, out _);

        public static ushort Bits(float v, out bool wasTie)
        {
            wasTie = false;
            Half h = (Half)v;
            ushort b = BitConverter.HalfToUInt16Bits(h);
            if (!float.IsFinite(v) || !Half.IsFinite(h)) return b;

            double dv = v;
            if ((double)(float)h == dv) return b;             // exactly representable, no rounding happened

            // Ties-to-even always clears the trailing mantissa bit, so an odd code cannot be a tie.
            if ((b & 1) != 0) return b;

            Half up = Half.BitIncrement(h);
            if (Half.IsFinite(up) && ((double)(float)h + (double)(float)up) / 2.0 == dv)
            {
                wasTie = true;
                return Math.Abs((float)up) > Math.Abs((float)h) ? BitConverter.HalfToUInt16Bits(up) : b;
            }

            Half dn = Half.BitDecrement(h);
            if (Half.IsFinite(dn) && ((double)(float)h + (double)(float)dn) / 2.0 == dv)
            {
                wasTie = true;
                return Math.Abs((float)dn) > Math.Abs((float)h) ? BitConverter.HalfToUInt16Bits(dn) : b;
            }

            return b;
        }
    }
}
