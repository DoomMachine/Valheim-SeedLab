// A 64-bit .NET host that runs a .NET Framework exe on the GAME's own Mono runtime DLL, read in place from
// the install (nothing is copied or written there). The recipe of SeedLab's Valheim 1.0.16 audit; used by
// run-rounding-probe.ps1.
// Usage: MonoHost <mono-2.0-bdwgc.dll> <valheim_Data\Managed> <MonoBleedingEdge\etc> <probe.exe> [mono option, e.g. -O=-float32]
using System;
using System.Runtime.InteropServices;

unsafe class Program
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void SetPath([MarshalAs(UnmanagedType.LPStr)] string p);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void SetDirs([MarshalAs(UnmanagedType.LPStr)] string a, [MarshalAs(UnmanagedType.LPStr)] string c);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr JitInit([MarshalAs(UnmanagedType.LPStr)] string n, [MarshalAs(UnmanagedType.LPStr)] string v);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr AsmOpen(IntPtr d, [MarshalAs(UnmanagedType.LPStr)] string p);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void ParseOpts(int argc, IntPtr argv);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int JitExec(IntPtr d, IntPtr a, int argc, IntPtr argv);

    static T F<T>(IntPtr lib, string n) => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(lib, n));

    static int Main(string[] args)
    {
        string monoDll = args[0], managed = args[1], etc = args[2], exe = args[3];
        IntPtr lib = NativeLibrary.Load(monoDll);
        F<SetDirs>(lib, "mono_set_dirs")(managed, etc);
        F<SetPath>(lib, "mono_set_assemblies_path")(managed);
        if (args.Length > 4)
        {
            IntPtr o = Marshal.StringToHGlobalAnsi(args[4]);
            IntPtr* ov = stackalloc IntPtr[1];
            ov[0] = o;
            F<ParseOpts>(lib, "mono_jit_parse_options")(1, (IntPtr)ov);
            Console.WriteLine("mono_jit_parse_options(" + args[4] + ")");
        }
        else
        {
            Console.WriteLine("no mono option (the runtime's defaults)");
        }

        IntPtr dom = F<JitInit>(lib, "mono_jit_init_version")("probe", "v4.0.30319");
        IntPtr asm = F<AsmOpen>(lib, "mono_domain_assembly_open")(dom, exe);
        if (asm == IntPtr.Zero) { Console.WriteLine("assembly open failed"); return 3; }
        IntPtr s1 = Marshal.StringToHGlobalAnsi(exe);
        IntPtr s2 = Marshal.StringToHGlobalAnsi(managed);
        IntPtr* argv = stackalloc IntPtr[2];
        argv[0] = s1;
        argv[1] = s2;
        return F<JitExec>(lib, "mono_jit_exec")(dom, asm, 2, (IntPtr)argv);
    }
}
