using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using SeedLab.WorldGen.Diagnostics;

namespace SeedLabTests
{
    /// <summary>
    /// PT1: generator code can write the profiler and can never read it.
    ///
    ///   dotnet run -c Release --project tests\SeedLab.Tests -- profile-tripwire
    ///
    /// <para>Walks every method body of <c>SeedLab.WorldGen</c>, <c>SeedLab.Locations</c>,
    /// <c>SeedLab.LocationOracle</c> and the <c>SeedLab.Search.Evaluation</c> namespace at IL level
    /// (<c>System.Reflection.Metadata</c>, in the BCL) and fails on:</para>
    /// <list type="bullet">
    /// <item>any member of <see cref="PhaseSink"/> other than <c>Current</c>'s getter, <c>Begin</c> and
    /// <c>End</c> - so no <c>Snapshot</c>, <c>Reset</c>, setter or constructor;</item>
    /// <item>any member of <see cref="PhaseClock"/> other than <c>CountersOn</c>, <c>Count</c> and
    /// <c>Add</c>;</item>
    /// <item>any use of <see cref="Stopwatch"/> outside the types that already timed themselves before
    /// the profiler existed and only record what they measured (<c>BuildMilliseconds</c> and the
    /// search evaluator's cost totals), named one by one below.</item>
    /// </list>
    /// <para>If generator code cannot read a time, no value it computes can depend on one - which is
    /// half of why profiling cannot change a world; the fingerprints (PT2) are the other half, measured.
    /// The check proves it can see: it must find the boundaries it expects in the generator and the
    /// oracle, and it must flag every one of the reads planted in this test assembly.</para>
    /// </summary>
    public static class ProfileTripwire
    {
        private const string SinkType = "SeedLab.WorldGen.Diagnostics.PhaseSink";
        private const string ClockType = "SeedLab.WorldGen.Diagnostics.PhaseClock";
        private const string StopwatchType = "System.Diagnostics.Stopwatch";

        private static readonly HashSet<string> SinkAllowed = new HashSet<string>(StringComparer.Ordinal) { "get_Current", "Begin", "End" };
        private static readonly HashSet<string> ClockAllowed = new HashSet<string>(StringComparer.Ordinal) { "CountersOn", "Count", "Add" };

        /// <summary>Types that timed themselves before the profiler existed, and only record the result.</summary>
        private static readonly HashSet<string> StopwatchAllowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "SeedLab.Locations.BiomeField",
            "SeedLab.Locations.BiomeGrid",
            "SeedLab.Locations.LocationPlacementEngine",
            "SeedLab.Locations.WorldLocations",
            "SeedLab.Search.Evaluation.SeedEvaluator",
            "SeedLab.WorldGen.Unity.PerlinSelfTest",
        };

        private sealed class Finding
        {
            public string Assembly = "";
            public string Method = "";
            public string Target = "";
            public bool Violation;
        }

        public static int Run(string[] args)
        {
            Console.WriteLine("SeedLab profile tripwire (PT1): generator code writes the profiler and never reads it");
            string dir = AppContext.BaseDirectory;
            int fail = 0;
            List<Finding> all = new List<Finding>();
            foreach ((string file, string? onlyNamespace) in new (string, string?)[]
            {
                ("SeedLab.WorldGen.dll", null),
                ("SeedLab.Locations.dll", null),
                ("SeedLab.LocationOracle.dll", null),
                ("SeedLab.Search.dll", "SeedLab.Search.Evaluation"),
            })
            {
                string path = Path.Combine(dir, file);
                if (!File.Exists(path))
                {
                    Console.WriteLine("FAIL  " + file + " is not beside the test (" + path + ")");
                    fail++;
                    continue;
                }

                List<Finding> f = Scan(path, onlyNamespace, planted: false, out int methods);
                all.AddRange(f);
                int bad = 0;
                foreach (Finding x in f)
                {
                    if (!x.Violation) continue;
                    Console.WriteLine("FAIL  " + x.Assembly + ": " + x.Method + " uses " + x.Target);
                    bad++;
                }

                Console.WriteLine((bad == 0 ? "PASS" : "FAIL") + "  " + file.PadRight(28) + methods + " method bodies"
                                  + (onlyNamespace != null ? " in " + onlyNamespace : "") + ", " + Count(f, false)
                                  + " allowed profiler/clock references, " + bad + " violation(s)");
                fail += bad;
            }

            // ---- sensitivity: it sees what is there ----------------------------------------------------
            fail += Expect(all, "SeedLab.WorldGen.WorldGeneratorPort", SinkType + "::Begin", "the generator's phase boundaries");
            fail += Expect(all, "SeedLab.WorldGen.WorldGeneratorPort", ClockType + "::Count", "the generator's counter sites");
            fail += Expect(all, "SeedLab.LocationOracle.DumpedLocationOracle", SinkType + "::Begin", "the oracle's t5 boundaries");
            fail += Expect(all, "SeedLab.Locations.BiomeGrid", StopwatchType + "::", "an allowed pre-existing Stopwatch");

            // ---- sensitivity: it flags a read ----------------------------------------------------------
            string self = typeof(ProfileTripwire).Assembly.Location;
            List<Finding> planted = Scan(self, "SeedLabTests.Planted", planted: true, out _);
            HashSet<string> flagged = new HashSet<string>(StringComparer.Ordinal);
            foreach (Finding x in planted) if (x.Violation) flagged.Add(x.Target);
            string[] expected =
            {
                SinkType + "::Snapshot", SinkType + "::set_Current", ClockType + "::SnapshotCounters", StopwatchType + "::GetTimestamp",
            };
            int missed = 0;
            foreach (string e in expected)
            {
                if (!flagged.Contains(e))
                {
                    Console.WriteLine("FAIL  the planted read " + e + " was NOT flagged - the tripwire is blind to it");
                    missed++;
                }
            }

            Console.WriteLine((missed == 0 ? "PASS" : "FAIL") + "  planted reads flagged: " + (expected.Length - missed) + " of " + expected.Length
                              + " (" + string.Join(", ", expected) + ")");
            fail += missed;

            Console.WriteLine();
            Console.WriteLine((fail == 0 ? "PASS" : "FAIL") + "  profile tripwire");
            return fail == 0 ? 0 : 1;
        }

        private static int Count(List<Finding> f, bool violation)
        {
            int n = 0;
            foreach (Finding x in f) if (x.Violation == violation) n++;
            return n;
        }

        private static int Expect(List<Finding> all, string typePrefix, string targetPrefix, string what)
        {
            foreach (Finding f in all)
            {
                if (!f.Violation && f.Method.StartsWith(typePrefix, StringComparison.Ordinal)
                    && f.Target.StartsWith(targetPrefix, StringComparison.Ordinal))
                {
                    Console.WriteLine("PASS  sees " + what + ": " + f.Method + " -> " + f.Target);
                    return 0;
                }
            }

            Console.WriteLine("FAIL  did not see " + what + " (" + typePrefix + " -> " + targetPrefix + ") - the scan is blind");
            return 1;
        }

        // =============================================================================================
        // The scan
        // =============================================================================================

        private static List<Finding> Scan(string path, string? onlyNamespace, bool planted, out int methods)
        {
            List<Finding> found = new List<Finding>();
            methods = 0;
            using FileStream fs = File.OpenRead(path);
            using PEReader pe = new PEReader(fs);
            MetadataReader md = pe.GetMetadataReader();
            string asm = Path.GetFileName(path);

            foreach (TypeDefinitionHandle th in md.TypeDefinitions)
            {
                TypeDefinition td = md.GetTypeDefinition(th);
                string outer = OutermostName(md, th);
                if (onlyNamespace != null && !outer.StartsWith(onlyNamespace + ".", StringComparison.Ordinal)) continue;
                // The profiler's own types read their own state; that is what they are for.
                if (outer.StartsWith("SeedLab.WorldGen.Diagnostics.", StringComparison.Ordinal)) continue;

                foreach (MethodDefinitionHandle mh in td.GetMethods())
                {
                    MethodDefinition m = md.GetMethodDefinition(mh);
                    if (m.RelativeVirtualAddress == 0) continue;
                    methods++;
                    string method = FullTypeName(md, th) + "::" + md.GetString(m.Name);
                    MethodBodyBlock body = pe.GetMethodBody(m.RelativeVirtualAddress);
                    foreach (string target in Targets(md, body.GetILBytes()))
                    {
                        bool sink = target.StartsWith(SinkType + "::", StringComparison.Ordinal);
                        bool clock = target.StartsWith(ClockType + "::", StringComparison.Ordinal);
                        bool watch = target.StartsWith(StopwatchType + "::", StringComparison.Ordinal);
                        if (!sink && !clock && !watch) continue;
                        string member = target.Substring(target.IndexOf("::", StringComparison.Ordinal) + 2);
                        bool violation = sink ? !SinkAllowed.Contains(member)
                                       : clock ? !ClockAllowed.Contains(member)
                                       : planted || !StopwatchAllowed.Contains(outer);
                        found.Add(new Finding { Assembly = asm, Method = method, Target = target, Violation = violation });
                    }
                }
            }

            return found;
        }

        private static Dictionary<short, OperandType>? s_operands;

        /// <summary>Opcode value -> operand type, from the BCL's own table of opcodes.</summary>
        private static Dictionary<short, OperandType> Operands()
        {
            if (s_operands != null) return s_operands;
            Dictionary<short, OperandType> d = new Dictionary<short, OperandType>();
            foreach (FieldInfo f in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                if (f.GetValue(null) is OpCode op) d[op.Value] = op.OperandType;
            }

            return s_operands = d;
        }

        /// <summary>Every method or field an IL body references (call, callvirt, newobj, ld/st(s)fld(a), ldftn, ldtoken).</summary>
        private static IEnumerable<string> Targets(MetadataReader md, byte[] il)
        {
            Dictionary<short, OperandType> ops = Operands();
            int i = 0;
            while (i < il.Length)
            {
                short code = il[i] == 0xFE && i + 1 < il.Length ? (short)(0xFE00 | il[i + 1]) : il[i];
                i += il[i] == 0xFE ? 2 : 1;
                if (!ops.TryGetValue(code, out OperandType ot))
                {
                    throw new InvalidDataException("unknown IL opcode 0x" + code.ToString("X4") + " at offset " + i);
                }

                switch (ot)
                {
                    case OperandType.InlineNone:
                        break;
                    case OperandType.ShortInlineBrTarget:
                    case OperandType.ShortInlineI:
                    case OperandType.ShortInlineVar:
                        i += 1;
                        break;
                    case OperandType.InlineVar:
                        i += 2;
                        break;
                    case OperandType.InlineI8:
                    case OperandType.InlineR:
                        i += 8;
                        break;
                    case OperandType.InlineSwitch:
                        int n = BitConverter.ToInt32(il, i);
                        i += 4 + 4 * n;
                        break;
                    case OperandType.InlineMethod:
                    case OperandType.InlineField:
                    case OperandType.InlineTok:
                        int token = BitConverter.ToInt32(il, i);
                        i += 4;
                        string? t = Resolve(md, token);
                        if (t != null) yield return t;
                        break;
                    default:
                        i += 4;
                        break;
                }
            }
        }

        private static string? Resolve(MetadataReader md, int token)
        {
            EntityHandle h = MetadataTokens.EntityHandle(token);
            switch (h.Kind)
            {
                case HandleKind.MethodDefinition:
                    {
                        MethodDefinition m = md.GetMethodDefinition((MethodDefinitionHandle)h);
                        return FullTypeName(md, m.GetDeclaringType()) + "::" + md.GetString(m.Name);
                    }
                case HandleKind.FieldDefinition:
                    {
                        FieldDefinition f = md.GetFieldDefinition((FieldDefinitionHandle)h);
                        return FullTypeName(md, f.GetDeclaringType()) + "::" + md.GetString(f.Name);
                    }
                case HandleKind.MemberReference:
                    {
                        MemberReference r = md.GetMemberReference((MemberReferenceHandle)h);
                        string? parent = r.Parent.Kind switch
                        {
                            HandleKind.TypeReference => RefName(md, (TypeReferenceHandle)r.Parent),
                            HandleKind.TypeDefinition => FullTypeName(md, (TypeDefinitionHandle)r.Parent),
                            _ => null,   // generic instantiations: never the profiler's or Stopwatch's types
                        };
                        return parent == null ? null : parent + "::" + md.GetString(r.Name);
                    }
                case HandleKind.MethodSpecification:
                    {
                        MethodSpecification s = md.GetMethodSpecification((MethodSpecificationHandle)h);
                        return Resolve(md, MetadataTokens.GetToken(s.Method));
                    }
                default:
                    return null;
            }
        }

        private static string RefName(MetadataReader md, TypeReferenceHandle h)
        {
            TypeReference r = md.GetTypeReference(h);
            string name = md.GetString(r.Name);
            if (r.ResolutionScope.Kind == HandleKind.TypeReference) return RefName(md, (TypeReferenceHandle)r.ResolutionScope) + "+" + name;
            string ns = md.GetString(r.Namespace);
            return ns.Length == 0 ? name : ns + "." + name;
        }

        private static string FullTypeName(MetadataReader md, TypeDefinitionHandle h)
        {
            TypeDefinition t = md.GetTypeDefinition(h);
            string name = md.GetString(t.Name);
            TypeDefinitionHandle outer = t.GetDeclaringType();
            if (!outer.IsNil) return FullTypeName(md, outer) + "+" + name;
            string ns = md.GetString(t.Namespace);
            return ns.Length == 0 ? name : ns + "." + name;
        }

        /// <summary>The top-level type a (possibly nested, possibly compiler-generated) type lives in.</summary>
        private static string OutermostName(MetadataReader md, TypeDefinitionHandle h)
        {
            TypeDefinition t = md.GetTypeDefinition(h);
            while (!t.GetDeclaringType().IsNil)
            {
                h = t.GetDeclaringType();
                t = md.GetTypeDefinition(h);
            }

            return FullTypeName(md, h);
        }
    }
}

namespace SeedLabTests.Planted
{
    /// <summary>
    /// Reads the profiler the way generator code must never do. Never called: it exists so that the
    /// tripwire has to find it, which is how the tripwire proves it is not blind.
    /// </summary>
    internal static class TripwirePlant
    {
        internal static long ReadsTheClock()
        {
            Span<long> s = stackalloc long[PhaseSink.SnapshotLength];
            PhaseSink.Current?.Snapshot(s);
            PhaseSink.Current = null;
            Span<long> c = stackalloc long[PhaseClock.Capacity];
            PhaseClock.SnapshotCounters(c);
            return s[0] + c[0] + Stopwatch.GetTimestamp();
        }
    }
}
