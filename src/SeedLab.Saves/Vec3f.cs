using System;
using System.Globalization;

namespace SeedLab.Saves
{
    /// <summary>
    /// A stored <c>UnityEngine.Vector3</c>: three float32 in x, y, z order
    /// (<c>ZPackage.Write(Vector3)</c> / <c>ReadVector3</c>, decompiled - 05-validation.md section 5.0).
    /// <para>
    /// This is a transport type for what is on disk. It deliberately has no arithmetic: any maths on
    /// world coordinates belongs in SeedLab.WorldGen, where the float/double cast discipline of
    /// 01-worldgen-core.md section 6 is enforced. Do not add operators here.
    /// </para>
    /// </summary>
    public readonly struct Vec3f : IEquatable<Vec3f>
    {
        public readonly float X;
        public readonly float Y;
        public readonly float Z;

        public Vec3f(float x, float y, float z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public bool Equals(Vec3f other)
        {
            // Bit equality, not numeric equality: these values are an oracle for the port
            // (05-validation.md T5 compares the float32 bit pattern), so -0.0 != 0.0 here.
            return BitConverter.SingleToInt32Bits(X) == BitConverter.SingleToInt32Bits(other.X)
                && BitConverter.SingleToInt32Bits(Y) == BitConverter.SingleToInt32Bits(other.Y)
                && BitConverter.SingleToInt32Bits(Z) == BitConverter.SingleToInt32Bits(other.Z);
        }

        public override bool Equals(object? obj) => obj is Vec3f v && Equals(v);

        public override int GetHashCode() => HashCode.Combine(
            BitConverter.SingleToInt32Bits(X),
            BitConverter.SingleToInt32Bits(Y),
            BitConverter.SingleToInt32Bits(Z));

        public override string ToString() => string.Format(
            CultureInfo.InvariantCulture, "({0}, {1}, {2})", X, Y, Z);
    }
}
