using System;
using System.Globalization;
using SeedLab.Locations;
using SeedLab.Saves;
using SeedLab.WorldGen;

namespace SeedLabTests
{
    /// <summary>
    /// The game's three rounding helpers and their two call-site shapes, on inputs a hair from a
    /// boundary - the regression test of the 2026-09-26 full-precision fix.
    ///
    ///   dotnet run -c Release --project tests\SeedLab.Tests -- rounding
    ///
    /// <para><b>Where the expected values come from - not from the code under test.</b> Each one was
    /// produced by the GAME's own IL on the GAME's own x64 Mono runtime
    /// (<c>MonoBleedingEdge\EmbedRuntime\mono-2.0-bdwgc.dll</c>, SHA-256 <c>35FD9D80...A028</c>, Valheim
    /// 1.0.16) started with <c>-O=-float32</c>, the Mono option whose string <c>UnityPlayer.dll</c>
    /// carries (offset 29,338,344) and passes to <c>mono_jit_parse_options</c>:</para>
    /// <list type="bullet">
    /// <item><c>Utils.FloorToInt</c> / <c>Utils.RoundToInt</c> (assembly_utils) and
    /// <c>AltBiomeWorldData.WorldSpaceToMapSpace</c> (assembly_valheim): their IL bytes were read out of
    /// the game's DLLs by reflection and re-emitted byte for byte into a DynamicMethod (the real type
    /// cannot be called outside the player: <c>Utils</c>' static constructor reads
    /// <c>Application.persistentDataPath</c>). A replica compiled from the decompiled source, with
    /// identical IL bytes and the game's <c>[AggressiveInlining]</c>, gave the same value for every input
    /// whether called through a delegate or statically;</item>
    /// <item><c>ZoneSystem.GetZone</c> and <c>Minimap.WorldToPixel</c>: copies of the game's call-site IL
    /// (checked with Mono.Cecil) around that replica.</item>
    /// </list>
    /// <para>The same probe WITHOUT the option reproduces every "old" value below, which is what .NET
    /// computes for the same C#, so the option is what separates the two readings; the option being in
    /// force in the running game is inferred from the string and from two in-game measurements of the
    /// same mechanism (<see cref="BiomeGrid.MapSpaceToWorldSpace"/>: 57 of 938 sector centres wrong at
    /// float precision, 0 in double; <c>GetDeepNorthHeight</c>'s un-narrowed <c>dup</c>, pitfalls section 9).
    /// The probe is <c>tools\SeedLab.MonoProbe</c> (<c>run-rounding-probe.ps1</c> prints both columns);
    /// every value was also derived by hand from the double arithmetic.</para>
    ///
    /// <para><b>Each "differs" case must really sit in the band:</b> the test recomputes the pre-fix
    /// formula (float per step, which is what .NET does with the game's C#) and requires it to give a
    /// DIFFERENT value there - so a test input that drifted out of the band would fail, not pass
    /// vacuously. The "same" cases pin what must not change: exact halves, and the double sum's own
    /// rounding (<c>FloorToInt(-1e-12f) == 0</c>: the helpers are not an exact floor either).</para>
    /// </summary>
    public static class RoundingGoldens
    {
        private static int _pass, _fail;

        // (float32 bits, the game's value, whether the pre-fix code gave another value)
        private static readonly (uint Bits, int Game, bool Differs)[] Floor =
        {
            (0xB8D1B717, -1, true),     // -0.0001
            (0x3F7FBE77, 0, true),      // 0.999
            (0x3F7FF972, 0, true),      // 0.9999
            (0x3F7FFFFF, 0, true),      // 0.99999994, the float below 1
            (0xAEDBE6FF, -1, true),     // -1e-10: 64000 - 1e-10 is a double below 64000
            (0xAB8CBCCC, 0, false),     // -1e-12: the double sum rounds to 64000.0 - not an exact floor
            (0x8DA24260, 0, false),     // -1e-30
            (0x80000001, 0, false),     // -1.4e-45, the negative float nearest zero
            (0x40200000, 2, false),     // 2.5
            (0x3F000000, 0, false),     // 0.5
            (0xBF000000, -1, false),    // -0.5
            (0x43240000, 164, false),   // 164 (what 163.999999f is)
        };

        private static readonly (uint Bits, int Game, bool Differs)[] Round =
        {
            (0x3EFFF2E5, 0, true),      // 0.4999
            (0x42C8FFF3, 100, true),    // 100.4999
            (0x3EFFFFFF, 0, true),      // 0.49999997, the float below 0.5
            (0xBF000001, -1, true),     // -0.50000006, the float below -0.5
            (0x44800FFF, 1024, true),   // 1024.4999, the float below 1024.5
            (0x40200000, 3, false),     // 2.5: exact halves round up under both readings
            (0x3F000000, 1, false),     // 0.5
            (0xBF000000, 0, false),     // -0.5
            (0x3FC00000, 2, false),     // 1.5
            (0x42C90000, 101, false),   // 100.5
        };

        private static readonly (uint Bits, int Game, bool Differs)[] MapSpace =
        {
            (0xC5FFF001, 340, true),    // -8190.00048828125 (the audit's example)
            (0x40BFFFFF, 1023, true),   // 5.9999995, the float below the line at 6
            (0x447A7FFF, 1106, true),   // 1001.99994, below the line at 1002
            (0x461C47FF, 1856, true),   // 10001.999, below the line at 10002
            (0x418FFFFF, 1024, true),   // 17.999998, below the line at 18
            (0xC63B6801, 23, false),    // -11994.001: far enough below its line that both agree
            (0x40C00000, 1024, false),  // 6
            (0x447A8000, 1107, false),  // 1002
            (0xC63B6800, 24, false),    // -11994
            (0x461C4800, 1857, false),  // 10002
        };

        // ZoneSystem.GetZone, one axis: FloorToInt((float)(((double)p + 32.0) / 64.0))
        private static readonly (uint Bits, int Game, bool Differs)[] Zone =
        {
            (0x41FF3333, 0, true),      // 31.9
            (0x41FF999A, 0, true),      // 31.95
            (0x41FFEB85, 0, true),      // 31.99
            (0x41FF0000, 0, true),      // 31.875
            (0xC200001A, -1, true),     // -32.0001
            (0xC2003333, -1, true),     // -32.05
            (0xC2000001, -1, true),     // -32.000004, the float below -32
            (0x42BFCCCD, 1, true),      // 95.9
            (0x41FEFDF4, 0, false),     // 31.874: outside the old 0.125 m band
            (0x41FFFFFF, 1, false),     // 31.999998: the quotient 1 - 2^-25 narrows to 1f - zone 1 in the game too
            (0xC2000000, 0, false),     // -32
            (0x42000000, 1, false),     // 32
        };

        // Minimap.WorldToPixel, one axis, m_pixelSize 12, m_textureSize 2048:
        // RoundToInt(x / m_pixelSize + (float)(m_textureSize / 2)). Wide = what the game would give if the
        // argument were NOT narrowed to float at the call; the probe says it is.
        private static readonly (uint Bits, int Game, bool Differs, bool WideDiffers)[] Pixel =
        {
            (0x40BF3B64, 1024, true, false),   // 5.976
            (0x40BFFFE7, 1025, false, true),   // 5.999988: 1024 if the argument stayed double
            (0x40BFFF2E, 1025, false, true),   // 5.9999
            (0x418FFFCC, 1026, false, true),   // 17.9999
            (0x40C00000, 1025, false, false),  // 6: the sample point of column 1024 maps to 1025 (a half-pixel tie)
            (0xC0C00000, 1024, false, false),  // -6
            (0x413FD70A, 1025, false, false),  // 11.99
        };

        public static int Run(string[] args)
        {
            Console.WriteLine("SeedLab: the game's rounding helpers at full precision (Utils.FloorToInt, Utils.RoundToInt,");
            Console.WriteLine("         AltBiomeWorldData.WorldSpaceToMapSpace, ZoneSystem.GetZone, Minimap.WorldToPixel)");

            foreach ((uint b, int game, bool differs) in Floor)
            {
                float f = F(b);
                int old = (int)(float)(f + 64000f) - 64000;
                Case("Utils.FloorToInt  (ZoneMath)       ", b, game, ZoneMath.FloorToInt(f), old, differs);
                Case("Utils.FloorToInt  (ValheimRounding)", b, game, ValheimRounding.FloorToInt(f), old, differs);
            }

            foreach ((uint b, int game, bool differs) in Round)
            {
                float f = F(b);
                int old = (int)(float)(f + 64000.5f) - 64000;
                Case("Utils.RoundToInt  (ValheimRounding)", b, game, ValheimRounding.RoundToInt(f), old, differs);
            }

            foreach ((uint b, int game, bool differs) in MapSpace)
            {
                float x = F(b);
                int old = (int)(float)((float)((float)(x - 6f) / 12f) + 1024f);
                Case("WorldSpaceToMapSpace (BiomeGrid)   ", b, game, BiomeGrid.WorldSpaceToMapSpace(x), old, differs);
            }

            foreach ((uint b, int game, bool differs) in Zone)
            {
                float p = F(b);
                float arg = (float)(((double)p + 32.0) / 64.0);
                int old = (int)(float)(arg + 64000f) - 64000;
                Vec2s zx = ZoneMath.GetZone(p, 0f), zz = ZoneMath.GetZone(0f, p);
                Case("ZoneSystem.GetZone x (ZoneMath)    ", b, game, zx.x, old, differs);
                Case("ZoneSystem.GetZone z (ZoneMath)    ", b, game, zz.y, old, differs);
                Case("GetZone via ValheimRounding (vseed at, save zones)", b, game, ValheimRounding.FloorToInt(arg), old, differs);
            }

            MinimapGeometry g = new MinimapGeometry(2048, 12f);
            foreach ((uint b, int game, bool differs, bool wideDiffers) in Pixel)
            {
                float x = F(b);
                g.WorldToPixel(x, x, out int col, out int row);
                int old = (int)(float)((float)((float)(x / 12f) + 1024f) + 64000.5f) - 64000;
                int wide = (int)((double)x / 12.0 + 1024.0 + 64000.5) - 64000;
                Case("Minimap.WorldToPixel col (MinimapGeometry)", b, game, col, old, differs);
                Case("Minimap.WorldToPixel row (MinimapGeometry)", b, game, row, old, differs);
                Check(wideDiffers == (wide != game),
                      "  " + Hex(b) + " the argument is narrowed at the call: " + (wideDiffers ? "kept wide it would be " + wide : "wide and narrowed agree"),
                      "  " + Hex(b) + " the narrowed/wide expectation is wrong (wide " + wide + ", game " + game + ")");
            }

            // The two FloorToInt copies must stay character-for-character identical in effect: a sweep of
            // bit patterns over every exponent a world or map coordinate can have, both signs.
            Random rng = new Random(20260926);
            long n = 0, differ = 0;
            for (int i = 0; i < 2_000_000; i++)
            {
                uint bits = (uint)rng.Next() ^ ((uint)rng.Next() << 16);
                float f = F(bits);
                if (float.IsNaN(f) || float.IsInfinity(f) || Math.Abs(f) > 1_000_000f) continue;
                n++;
                if (ZoneMath.FloorToInt(f) != ValheimRounding.FloorToInt(f)) differ++;
            }

            Check(differ == 0 && n > 1_000_000, "ZoneMath.FloorToInt == ValheimRounding.FloorToInt on " + n.ToString("N0", CultureInfo.InvariantCulture) + " random floats",
                  differ + " of " + n + " random floats differ between the two FloorToInt copies");

            Console.WriteLine();
            Console.WriteLine((_fail == 0 ? "PASS" : "FAIL") + "  rounding: " + _pass + " checks passed, " + _fail + " failed");
            return _fail == 0 ? 0 : 1;
        }

        private static void Case(string what, uint bits, int game, int got, int old, bool differs)
        {
            string v = F(bits).ToString("R", CultureInfo.InvariantCulture);
            Check(got == game, what + " " + Hex(bits) + " (" + v + ") = " + got + (differs ? "   (was " + old + ")" : ""),
                  what + " " + Hex(bits) + " (" + v + ") = " + got + ", the game gives " + game);
            Check(differs == (old != game),
                  "  " + Hex(bits) + (differs ? " sits in the band: the pre-fix float formula gives " + old : " is not in the band: the pre-fix formula agrees"),
                  "  " + Hex(bits) + " was expected " + (differs ? "IN" : "OUTSIDE") + " the band, but the pre-fix formula gives " + old + " against " + game);
        }

        private static void Check(bool ok, string passText, string failText)
        {
            if (ok) { _pass++; Console.WriteLine("PASS  " + passText); }
            else { _fail++; Console.WriteLine("FAIL  " + failText); }
        }

        private static float F(uint bits) => BitConverter.Int32BitsToSingle(unchecked((int)bits));

        private static string Hex(uint bits) => bits.ToString("X8", CultureInfo.InvariantCulture);
    }
}
