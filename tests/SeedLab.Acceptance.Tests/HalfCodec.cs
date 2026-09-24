using System;
using System.Runtime.CompilerServices;

namespace SeedLabAcceptanceTests
{
    /// <summary>
    /// The half-precision layer that sits between the port's float32 height and what the game stored.
    ///
    /// Utils.FloatsToCompressedHalfBuffer (decomp/Utils.cs 1420-1428) writes every minimap height as
    /// UnityEngine.Mathf.FloatToHalf(h). That is
    ///   [MethodImpl(InternalCall)] [FreeFunction(IsThreadSafe = true)] public static extern ushort FloatToHalf(float);
    /// (decomp/UnityEngine.Mathf.cs 47-49) - native, so its rounding rule cannot be read from managed
    /// code. 05-validation.md section 1.3 marks it **Unverified** and says to assume IEEE
    /// round-to-nearest-EVEN, which is what .NET's (Half) cast does, and to settle it with the dumper
    /// (test T7, which nobody has run - the dumper has never been loaded by the game).
    ///
    /// This class therefore quantises BOTH ways and the suite reports both counts:
    ///   NetBits   - BitConverter.HalfToUInt16Bits((Half)v), ties to even: the specification's rule.
    ///   UnityBits - identical except that an EXACT midpoint is resolved away from zero.
    /// The two differ on nothing but exact midpoints, so the gap between the two counts is a direct
    /// measurement of FloatToHalf's tie-breaking on this data, not a tolerance.
    /// </summary>
    public static class HalfCodec
    {
        /// <summary>IEEE-754 binary16, round-to-nearest-ties-to-EVEN - what 05-validation.md prescribes.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ushort NetBits(float v) => BitConverter.HalfToUInt16Bits((Half)v);

        /// <summary>
        /// Same, except exact midpoints round AWAY FROM ZERO. Measured, not assumed: on the two cached
        /// worlds the game's stored code is the away-from-zero neighbour at every exact midpoint this
        /// data produces (the suite prints the tie census that proves it). Outside the ties this
        /// function is bit-identical to <see cref="NetBits"/>.
        /// </summary>
        public static ushort UnityBits(float v, out bool wasTie)
        {
            wasTie = false;
            Half h = (Half)v;
            ushort b = BitConverter.HalfToUInt16Bits(h);
            if (!float.IsFinite(v) || !Half.IsFinite(h)) return b;

            double dv = v;
            if ((double)(float)h == dv) return b;          // exactly representable: no rounding happened

            // Ties-to-even always leaves the trailing mantissa bit clear, so an odd code cannot be a tie.
            if ((b & 1) != 0) return b;

            Half up = Half.BitIncrement(h);
            if (Half.IsFinite(up))
            {
                double mid = ((double)(float)h + (double)(float)up) / 2.0;
                if (mid == dv)
                {
                    wasTie = true;
                    return Math.Abs((float)up) > Math.Abs((float)h) ? BitConverter.HalfToUInt16Bits(up) : b;
                }
            }
            Half dn = Half.BitDecrement(h);
            if (Half.IsFinite(dn))
            {
                double mid = ((double)(float)h + (double)(float)dn) / 2.0;
                if (mid == dv)
                {
                    wasTie = true;
                    return Math.Abs((float)dn) > Math.Abs((float)h) ? BitConverter.HalfToUInt16Bits(dn) : b;
                }
            }
            return b;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float Decode(ushort bits) => (float)BitConverter.UInt16BitsToHalf(bits);

        /// <summary>
        /// A monotone ordinal over the 16-bit patterns, so that the signed distance between two codes is
        /// their distance in half-ULPs. Subnormals and zero are ordinary members of the ladder
        /// (05-validation.md T3: asdasdasd holds 304 distinct subnormal codes over 363 pixels, so a
        /// decoder that assumes normalised halves mis-scores them).
        /// +0 (0x0000) and -0 (0x8000) share an ordinal; the suite compares raw bits for exactness and
        /// counts a sign-of-zero difference separately.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Ordinal(ushort bits)
            => (bits & 0x8000) != 0 ? (0x8000 - (bits & 0x7FFF)) : (bits + 0x8000);

        /// <summary>Signed distance ours - oracle, in half-ULPs. Non-finite codes give int.MinValue.</summary>
        public static int UlpDistance(ushort ours, ushort oracle)
        {
            if ((ours & 0x7C00) == 0x7C00 || (oracle & 0x7C00) == 0x7C00) return int.MinValue;
            return Ordinal(ours) - Ordinal(oracle);
        }
    }
}
