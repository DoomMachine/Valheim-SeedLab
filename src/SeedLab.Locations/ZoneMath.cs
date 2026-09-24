using System;
using SeedLab.WorldGen;
using SeedLab.WorldGen.Unity;

namespace SeedLab.Locations
{
    /// <summary>
    /// The <c>ZoneSystem</c> statics that location placement uses, plus the two <c>Utils</c> helpers
    /// they depend on. Every one is ported literally; several differ from the obvious implementation by
    /// exactly one rounding, and the rounding is the point.
    /// </summary>
    public static class ZoneMath
    {
        /// <summary><c>ZoneSystem.c_ZoneSize</c>.</summary>
        public const float ZoneSize = 64f;

        /// <summary><c>ZoneSystem.c_ZoneSizeHalf</c>.</summary>
        public const float ZoneHalf = 32f;

        /// <summary><c>ZoneSystem.c_WaterLevel</c>.</summary>
        public const float WaterLevel = 30f;

        /// <summary>The radius <c>GetRandomZone</c> accepts a zone centre within.</summary>
        public const float ZoneAcceptRadius = 10000f;

        /// <summary>
        /// <c>Utils.FloorToInt(float)</c> (assembly_utils, line 1222): <c>(int)(f + 64000f) - 64000</c>.
        /// <b>Not <c>MathF.Floor</c>.</b> The bias is added in float, so the fraction is quantised
        /// before truncation - near 64000 the float spacing is 2^-8. Identical to
        /// <c>SeedLab.Saves.ValheimRounding.FloorToInt</c>; the two are duplicated rather than shared
        /// because SeedLab.Locations must not depend on the save-file layer, and they must stay
        /// character-for-character identical.
        /// </summary>
        public static int FloorToInt(float f) => (int)(f + 64000f) - 64000;

        /// <summary>
        /// <c>Utils.LengthXZ(Vector3)</c> (line 557): <c>Mathf.Sqrt(v.x * v.x + v.z * v.z)</c>.
        /// Mathf.Sqrt widens its float argument, so the sum is narrowed to float FIRST (it is the
        /// argument of a float-taking call) and only then square-rooted in double. That is a different
        /// value from <c>Vector3.magnitude</c>, which keeps the sum at R8 - and the game uses both, on
        /// the same point, in filters 1 and 5.
        /// </summary>
        public static float LengthXZ(float x, float z)
        {
            float sum = (float)((double)x * (double)x + (double)z * (double)z);
            return (float)Math.Sqrt((double)sum);
        }

        /// <summary>
        /// <c>Vector3.get_magnitude</c>: products and sum accumulate at R8, one narrowing at the return.
        /// Used for filter 1 (<c>randomPointInZone.magnitude</c>, with y still 0) and for the
        /// <c>GetRandomZone</c> accept test.
        /// </summary>
        public static float Magnitude3(float x, float y, float z)
            => (float)Math.Sqrt((double)x * (double)x + (double)y * (double)y + (double)z * (double)z);

        /// <summary>
        /// <c>Vector3.get_sqrMagnitude</c> - one narrowing, at the store into the float local.
        /// </summary>
        public static float SqrMagnitude3(float x, float y, float z)
            => (float)((double)x * (double)x + (double)y * (double)y + (double)z * (double)z);

        /// <summary>
        /// <c>ZoneSystem.GetZone(Vector3)</c> (decomp 2973-2978). Note the double promotion and the
        /// narrowing back to float before <see cref="FloorToInt"/> - port it literally, it is
        /// observable at zone boundaries. The z component is the second argument.
        /// </summary>
        public static Vec2s GetZone(float px, float pz)
        {
            int x = FloorToInt((float)(((double)px + 32.0) / 64.0));
            int y = FloorToInt((float)(((double)pz + 32.0) / 64.0));
            return new Vec2s(x, y);
        }

        /// <summary><c>ZoneSystem.GetZonePos(Vector2s)</c> - the zone CENTRE in world metres.</summary>
        public static (float x, float y, float z) GetZonePos(Vec2s id)
            => ((float)id.x * 64f, 0f, (float)id.y * 64f);

        /// <summary>
        /// <c>ZoneSystem.GetZoneCenter(Vector2s)</c> - the same centre as SHORTS, which is what
        /// <c>WorldGenerator.GetBiomeArea(Vector2s)</c> is handed. <c>id.x * 64</c> is an int multiply
        /// truncated into a short; it cannot overflow for a legal zone.
        /// </summary>
        public static Vec2s GetZoneCenter(Vec2s id) => new Vec2s(id.x * 64, id.y * 64);

        /// <summary>
        /// <c>ZoneSystem.GetRandomZone(float range)</c> (decomp 2154-2164).
        ///
        /// <para><b>Two int draws per do-while iteration</b>, repeated until the zone centre is within
        /// 10 000 m. <c>Range(int,int)</c>'s upper bound is exclusive, so the reachable range is
        /// <c>[-num, num-1]</c>; with <c>num == 0</c> the call is <c>Range(0, 0)</c>, which this port's
        /// UnityRandom answers WITHOUT consuming state (rva 0x00054900 returns min when min == max), and
        /// the accept test passes immediately. So a <c>m_centerFirst</c> entry with a small
        /// <c>m_minDistance</c> spends its first <c>64 * k</c> attempts drawing nothing at all.</para>
        /// </summary>
        public static Vec2s GetRandomZone(UnityRandom rnd, float range)
        {
            int num = (int)range / 64;
            for (int guard = 0; guard < 10_000_000; guard++)
            {
                Vec2s v = new Vec2s(rnd.Range(-num, num), rnd.Range(-num, num));
                (float zx, float zy, float zz) = GetZonePos(v);
                if (Magnitude3(zx, zy, zz) < ZoneAcceptRadius) return v;
            }
            throw new InvalidOperationException(
                "GetRandomZone did not accept a zone in 10 000 000 iterations (range=" + range + "). "
                + "The game would spin here too; this guard only makes the hang visible.");
        }

        /// <summary>
        /// <c>ZoneSystem.GetRandomPointInZone(Vector2s, float)</c> (decomp 2166-2172).
        /// <b>Exactly two float draws, always</b>, before any filter runs.
        ///
        /// <para><b>Correction to spec 02 sections 5.4 and 6.1.</b> The spec says a location with
        /// <c>maxRadius &gt; 32</c> can land outside its candidate zone. <b>The threshold is 64, not
        /// 32.</b> <c>Range(minInclusive, maxInclusive)</c> is the lerp
        /// <c>(1-f)*max + f*min</c> with <c>f</c> in <c>[0,1)</c>, so it spans the interval whichever
        /// way round the bounds are. With <c>r &gt; 32</c> the bounds invert to
        /// <c>(r-32, 32-r)</c> and the offset spans <c>[-(r-32), r-32]</c> - a band that is narrower
        /// than the zone until <c>r - 32 &gt; 32</c>. So the offset only exceeds the 32 m half-zone
        /// when <c>r &gt; 64</c>. Measured: a synthetic entry with <c>exteriorRadius = 40</c> put
        /// 0 of its points outside the candidate zone. (Range(float,float) being a lerp is now a
        /// dumped measurement, not disassembly alone - see UnityRandom, goldens/natives-random.json
        /// D6/D6b.)</para>
        ///
        /// <para><b>Neither threshold is reached in vanilla 1.0.15.</b> The largest
        /// <c>Mathf.Max(m_exteriorRadius, m_interiorRadius)</c> over the 183 entries that run is exactly
        /// 32, so the bounds never invert and no point ever leaves its candidate zone (measured
        /// 2026-09-23). The code stays as it is because a modded table can reach both.</para>
        /// </summary>
        public static (float x, float y, float z) GetRandomPointInZone(UnityRandom rnd, Vec2s zone, float locationRadius)
        {
            (float zx, float zy, float zz) = GetZonePos(zone);
            float x = rnd.Range(-32f + locationRadius, 32f - locationRadius);
            float z = rnd.Range(-32f + locationRadius, 32f - locationRadius);
            return (zx + x, zy + 0f, zz + z);
        }

        /// <summary><c>Mathf.Max(float, float)</c> - <c>(a &gt; b) ? a : b</c>.</summary>
        public static float MathfMax(float a, float b) => (a > b) ? a : b;
    }
}
