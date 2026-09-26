// The game's own rounding helpers, run on the game's own x64 Mono runtime (MonoBleedingEdge\EmbedRuntime
// mono-2.0-bdwgc.dll), with and without the option UnityPlayer.dll passes it (-O=-float32).
// C# 5, compiled with the .NET Framework's csc (no reference to any game assembly is needed):
//   csc /nologo /optimize+ /out:RoundingProbe.exe RoundingProbe.cs
// Run by MonoHost: MonoHost <mono dll> <Managed> <MonoBleedingEdge\etc> RoundingProbe.exe [-O=-float32]
//
// Why not call Utils.FloorToInt directly: Utils has a static constructor that reads
// UnityEngine.Application.persistentDataPath, an engine internal call that does not exist outside the
// player, so the first call throws TypeInitializationException. So the probe:
//   1. reads the IL bytes of the REAL methods (Utils.FloorToInt, Utils.RoundToInt in assembly_utils.dll,
//      AltBiomeWorldData.WorldSpaceToMapSpace in assembly_valheim.dll) by reflection - which runs no type
//      initialiser - and re-emits exactly those opcodes and operands into a DynamicMethod. It checks that
//      the re-emitted bytes equal the originals and calls them through a delegate (never inlined);
//   2. has a replica class U compiled from the same source text, with [AggressiveInlining] like the game's.
//      It checks U's IL bytes against the real ones, then calls U statically (Mono may inline it) and
//      from two call-site shapes copied from the game (ZoneSystem.GetZone, Minimap.WorldToPixel), whose
//      IL can be checked with il.ps1.
// Every input is given by its float32 bits, so nothing depends on decimal parsing.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;

static class U
{
    // Same source text as assembly_utils Utils.RoundToInt / FloorToInt (decompiled 1.0.16).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int RoundToInt(float f) { return (int)(f + 64000.5f) - 64000; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int FloorToInt(float f) { return (int)(f + 64000f) - 64000; }

    // Same source text as assembly_valheim AltBiomeWorldData.WorldSpaceToMapSpace (decompiled 1.0.16).
    public static int WorldSpaceToMapSpace(float x) { return (int)((x - 6f) / 12f + 1024f); }
}

static class Shapes
{
    static float s_pixelSize = 12f;   // Minimap.m_pixelSize: a field load, as in the game (ldfld there)
    static int s_textureSize = 2048;  // Minimap.m_textureSize

    // Minimap.WorldToPixel, x half: int num = m_textureSize / 2; Utils.RoundToInt(p.x / m_pixelSize + (float)num)
    public static int WorldToPixelX(float x)
    {
        int num = s_textureSize / 2;
        return U.RoundToInt(x / s_pixelSize + (float)num);
    }

    // ZoneSystem.GetZone, x half: Utils.FloorToInt((float)(((double)p.x + 32.0) / 64.0))
    public static int GetZoneX(float x)
    {
        return U.FloorToInt((float)(((double)x + 32.0) / 64.0));
    }
}

static class P
{
    delegate int FI(float f);

    static float F(uint bits) { return BitConverter.ToSingle(BitConverter.GetBytes(bits), 0); }

    static string Hex(byte[] b) { return BitConverter.ToString(b).Replace("-", " "); }

    // Re-emits a body made only of the opcodes these three methods use. Anything else is refused.
    static FI Copy(MethodInfo real, out byte[] original, out string note)
    {
        original = real.GetMethodBody().GetILAsByteArray();
        DynamicMethod dm = new DynamicMethod("copy_" + real.Name, typeof(int), new Type[] { typeof(float) }, typeof(P).Module, true);
        ILGenerator g = dm.GetILGenerator();
        List<byte> again = new List<byte>();
        int i = 0;
        while (i < original.Length)
        {
            byte op = original[i++];
            again.Add(op);
            switch (op)
            {
                case 0x02: g.Emit(OpCodes.Ldarg_0); break;
                case 0x58: g.Emit(OpCodes.Add); break;
                case 0x59: g.Emit(OpCodes.Sub); break;
                case 0x5B: g.Emit(OpCodes.Div); break;
                case 0x69: g.Emit(OpCodes.Conv_I4); break;
                case 0x2A: g.Emit(OpCodes.Ret); break;
                case 0x22:
                {
                    float c = BitConverter.ToSingle(original, i);
                    for (int k = 0; k < 4; k++) again.Add(original[i + k]);
                    i += 4;
                    g.Emit(OpCodes.Ldc_R4, c);
                    break;
                }
                case 0x20:
                {
                    int c = BitConverter.ToInt32(original, i);
                    for (int k = 0; k < 4; k++) again.Add(original[i + k]);
                    i += 4;
                    g.Emit(OpCodes.Ldc_I4, c);
                    break;
                }
                default:
                    throw new NotSupportedException("opcode 0x" + op.ToString("X2") + " in " + real.Name);
            }
        }

        bool same = again.Count == original.Length;
        for (int k = 0; same && k < original.Length; k++) same = again[k] == original[k];
        note = "IL " + Hex(original) + (same ? "  (re-emitted byte for byte)" : "  (RE-EMISSION DIFFERS)");
        return (FI)dm.CreateDelegate(typeof(FI));
    }

    static bool SameIL(MethodInfo a, MethodInfo b)
    {
        byte[] x = a.GetMethodBody().GetILAsByteArray(), y = b.GetMethodBody().GetILAsByteArray();
        if (x.Length != y.Length) return false;
        for (int k = 0; k < x.Length; k++) if (x[k] != y[k]) return false;
        return true;
    }

    static int Main(string[] args)
    {
        string managed = args.Length > 0 ? args[0] : "";
        Type mono = Type.GetType("Mono.Runtime");
        string ver = mono == null ? "not Mono" : (string)mono.GetMethod("GetDisplayName", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, null);
        Console.WriteLine("runtime: " + ver + ", 64-bit process " + Environment.Is64BitProcess);
        float a = F(0x3E99999A), b = F(0x3DCCCCCD); // 0.3f, 0.1f
        double w = (double)(a + b);
        Console.WriteLine("float evaluation: (double)(0.3f + 0.1f) = " + w.ToString("R", CultureInfo.InvariantCulture)
            + (w == 0.40000001341104507 ? "  -> double on the stack (-O=-float32 in effect)" : w == 0.40000000596046448 ? "  -> float32 arithmetic" : "  -> ?"));

        Assembly au = Assembly.LoadFrom(System.IO.Path.Combine(managed, "assembly_utils.dll"));
        Assembly av = Assembly.LoadFrom(System.IO.Path.Combine(managed, "assembly_valheim.dll"));
        Console.WriteLine("assembly_utils   " + au.Location);
        Console.WriteLine("assembly_valheim " + av.Location);
        MethodInfo realFloor = au.GetType("Utils", true).GetMethod("FloorToInt", BindingFlags.Public | BindingFlags.Static, null, new Type[] { typeof(float) }, null);
        MethodInfo realRound = au.GetType("Utils", true).GetMethod("RoundToInt", BindingFlags.Public | BindingFlags.Static, null, new Type[] { typeof(float) }, null);
        MethodInfo realW2m = av.GetType("AltBiomeWorldData", true).GetMethod("WorldSpaceToMapSpace", BindingFlags.Public | BindingFlags.Static, null, new Type[] { typeof(float) }, null);

        byte[] il;
        string note;
        FI floorCopy = Copy(realFloor, out il, out note);
        Console.WriteLine("Utils.FloorToInt                     " + note + "; replica U.FloorToInt IL identical: " + SameIL(realFloor, typeof(U).GetMethod("FloorToInt")));
        FI roundCopy = Copy(realRound, out il, out note);
        Console.WriteLine("Utils.RoundToInt                     " + note + "; replica U.RoundToInt IL identical: " + SameIL(realRound, typeof(U).GetMethod("RoundToInt")));
        FI w2mCopy = Copy(realW2m, out il, out note);
        Console.WriteLine("AltBiomeWorldData.WorldSpaceToMapSpace " + note + "; replica U.WorldSpaceToMapSpace IL identical: " + SameIL(realW2m, typeof(U).GetMethod("WorldSpaceToMapSpace")));
        Console.WriteLine("columns: input bits, value; the real IL through a delegate (never inlined); the replica called statically (may be inlined)");

        uint[] floorIn = { 0xB8D1B717, 0x3F7FBE77, 0x3F7FF972, 0xAEDBE6FF, 0xAB8CBCCC, 0x8DA24260, 0x3F7FFFFF, 0x80000001, 0x40200000, 0x3F000000, 0xBF000000, 0x43240000 };
        foreach (uint u in floorIn)
        {
            float f = F(u);
            Console.WriteLine("FloorToInt        " + u.ToString("X8") + " " + f.ToString("R", CultureInfo.InvariantCulture).PadRight(24)
                + " real " + floorCopy(f).ToString().PadLeft(6) + "  replica " + U.FloorToInt(f).ToString().PadLeft(6));
        }

        uint[] roundIn = { 0x3EFFF2E5, 0x42C8FFF3, 0x3EFFFFFF, 0xBF000001, 0x44800FFF, 0x40200000, 0x3F000000, 0xBF000000, 0x3FC00000, 0x42C90000 };
        foreach (uint u in roundIn)
        {
            float f = F(u);
            Console.WriteLine("RoundToInt        " + u.ToString("X8") + " " + f.ToString("R", CultureInfo.InvariantCulture).PadRight(24)
                + " real " + roundCopy(f).ToString().PadLeft(6) + "  replica " + U.RoundToInt(f).ToString().PadLeft(6));
        }

        uint[] w2mIn = { 0xC5FFF001, 0x40C00000, 0x447A8000, 0xC63B6800, 0x461C4800, 0x40BFFFFF, 0x447A7FFF, 0xC63B6801, 0x461C47FF, 0x418FFFFF };
        foreach (uint u in w2mIn)
        {
            float f = F(u);
            Console.WriteLine("W2M               " + u.ToString("X8") + " " + f.ToString("R", CultureInfo.InvariantCulture).PadRight(24)
                + " real " + w2mCopy(f).ToString().PadLeft(6) + "  replica " + U.WorldSpaceToMapSpace(f).ToString().PadLeft(6));
        }

        uint[] zoneIn = { 0x41FF3333, 0x41FF999A, 0x41FFEB85, 0x41FF0000, 0x41FEFDF4, 0x41FFFFFF, 0xC200001A, 0xC2003333, 0xC2000000, 0xC2000001, 0x42000000, 0x42BFCCCD };
        foreach (uint u in zoneIn)
        {
            float f = F(u);
            Console.WriteLine("GetZone.x         " + u.ToString("X8") + " " + f.ToString("R", CultureInfo.InvariantCulture).PadRight(24)
                + " shape " + Shapes.GetZoneX(f).ToString().PadLeft(6)
                + "  real-on-narrowed-arg " + floorCopy((float)(((double)f + 32.0) / 64.0)).ToString().PadLeft(6));
        }

        uint[] pixelIn = { 0x40BF3B64, 0x40BFFFE7, 0x40BFFF2E, 0x40C00000, 0xC0C00000, 0x413FD70A, 0x418FFFCC };
        foreach (uint u in pixelIn)
        {
            float f = F(u);
            float narrowed = (float)((double)f / 12.0 + 1024.0);
            Console.WriteLine("WorldToPixel.x    " + u.ToString("X8") + " " + f.ToString("R", CultureInfo.InvariantCulture).PadRight(24)
                + " shape " + Shapes.WorldToPixelX(f).ToString().PadLeft(6)
                + "  real-on-narrowed-arg " + roundCopy(narrowed).ToString().PadLeft(6));
        }

        return 0;
    }
}
