using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Numerics;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace SeedLabTests
{
    /// <summary>
    /// ST1: no numeric API that could change a last bit is referenced by the generator.
    ///
    ///   dotnet run -c Release --project tests\SeedLab.Tests -- numerics-tripwire
    ///
    /// <para>The port's worlds are bit-exact because every float and double operation in it is the
    /// game's operation, rounded where the game rounds. A handful of .NET APIs compute something else
    /// while looking like the same thing - and a future vector kernel is exactly where one would slip in.
    /// This walks every method body of <c>SeedLab.WorldGen</c> and <c>SeedLab.Locations</c> at IL level
    /// and fails on a reference to any of them (design 12.1):</para>
    /// <list type="bullet">
    /// <item><b>fused multiply-add</b> - <c>Fma.*</c>, <c>*.FusedMultiplyAdd*</c> (Math, MathF, the vector
    /// types, AVX-512, AVX10): one rounding where the game has two;</item>
    /// <item><b>estimates</b> - any <c>*Estimate</c> (<c>MultiplyAddEstimate</c>, <c>ReciprocalEstimate</c>,
    /// ...): the hardware's choice of precision;</item>
    /// <item><b>the vector types' own maths</b> - <c>Vector*.Lerp/Sin/Cos/SinCos/Exp/Log/Log2/Hypot</c>
    /// and <c>Vector2/3/4.Lerp</c>: .NET's algorithms, not the C runtime's;</item>
    /// <item><b>MathF</b> - a different libm from the game's <c>(float)Math.X((double)f)</c>. Only the
    /// operations IEEE-754 defines exactly are allowed: <c>Sqrt</c>, <c>Abs</c>, <c>Floor</c>,
    /// <c>Ceiling</c>, <c>Truncate</c>, <c>CopySign</c> (the port uses <c>MathF.Sqrt</c>, which is sqrtss);</item>
    /// <item><b>rounding conversions</b> - every <c>ConvertTo*Int*</c> from a float or double source that
    /// is not <c>...WithTruncation</c> (CVTPS2DQ and friends round to nearest, where C#'s cast truncates),
    /// and every <c>ConvertTo*Native</c>. Widening an integer vector (<c>ConvertToVector512Int32(Vector128&lt;byte&gt;)</c>,
    /// the future VBMI lookup) is allowed: the member's signature is read to tell the two apart;</item>
    /// <item><b>embedded rounding</b> - any overload with a <c>FloatRoundingMode</c> parameter.</item>
    /// </list>
    /// <para>Reading an ISA's <c>IsSupported</c> is a query, not an operation, and is allowed everywhere.
    /// And no P/Invoke (<c>DllImport</c>/<c>LibraryImport</c>, <c>NativeLibrary</c>) in WorldGen, Locations
    /// or Runtime: native code would bring its own arithmetic and its own FPU state. The only P/Invoke
    /// SeedLab may ever carry is a test-only one.</para>
    /// <para>It proves it can see: it must find the allowed truncating conversion in <c>PerlinFast</c> and
    /// the allowed <c>MathF.Sqrt</c> in <c>UnityRandom</c>, must flag every forbidden use planted in this
    /// test assembly, and must NOT flag the allowed uses planted beside them.</para>
    /// </summary>
    public static class NumericsTripwire
    {
        private sealed class Finding
        {
            public string Method = "";
            public string Target = "";
            public string Rule = "";
        }

        private static readonly HashSet<string> MathFExact = new HashSet<string>(StringComparer.Ordinal)
        {
            "Sqrt", "Abs", "Floor", "Ceiling", "Truncate", "CopySign",
        };

        private static readonly HashSet<string> VectorMaths = new HashSet<string>(StringComparer.Ordinal)
        {
            "Lerp", "Sin", "Cos", "SinCos", "Exp", "Log", "Log2", "Hypot",
        };

        /// <summary>
        /// A Perlin divergence on a VECTOR path stops every tool at load, 'vseed selftest --report'
        /// included, so its sentence has to name the way out: the scalar path (which the same check has
        /// just passed) and the report run on it. A divergence of the scalar reference has no way out and
        /// must not claim one.
        /// </summary>
        private static int RefusalWording()
        {
            string vector = SeedLab.WorldGen.Unity.PerlinSelfTest.DivergenceMessage("O5 AVX2 8-wide (lane 3)", true, 1.5f, 2.5f, 0.25f, 0.26f);
            string scalar = SeedLab.WorldGen.Unity.PerlinSelfTest.DivergenceMessage("O3 byte-table scalar", false, 1.5f, 2.5f, 0.25f, 0.26f);
            bool ok = vector.StartsWith("SeedLab:", StringComparison.Ordinal)
                      && vector.Contains("--simd scalar", StringComparison.Ordinal)
                      && vector.Contains("'vseed --simd scalar selftest --report'", StringComparison.Ordinal)
                      && vector.Contains("SEEDLAB_SIMD=scalar", StringComparison.Ordinal)
                      && scalar.StartsWith("SeedLab:", StringComparison.Ordinal)
                      && !scalar.Contains("--simd", StringComparison.Ordinal);
            Console.WriteLine((ok ? "PASS" : "FAIL") + "  a vector-path divergence names --simd scalar and the report to send under it; "
                              + "a scalar-path divergence claims no way out");
            if (!ok)
            {
                Console.WriteLine("      vector: " + vector);
                Console.WriteLine("      scalar: " + scalar);
            }
            return ok ? 0 : 1;
        }

        public static int Run(string[] args)
        {
            Console.WriteLine("SeedLab numerics tripwire (ST1): no API that could change a last bit, no P/Invoke");
            string dir = AppContext.BaseDirectory;
            int fail = 0;
            List<(string Method, string Target)> seen = new List<(string, string)>();

            foreach ((string file, bool numerics) in new[]
            {
                ("SeedLab.WorldGen.dll", true),
                ("SeedLab.Locations.dll", true),
                ("SeedLab.Runtime.dll", false),
            })
            {
                string path = Path.Combine(dir, file);
                if (!File.Exists(path))
                {
                    Console.WriteLine("FAIL  " + file + " is not beside the test (" + path + ")");
                    fail++;
                    continue;
                }

                List<Finding> f = Scan(path, null, numerics, seen, out int methods, out int pinvokes);
                foreach (Finding x in f) Console.WriteLine("FAIL  " + file + ": " + x.Method + " uses " + x.Target + " (" + x.Rule + ")");
                Console.WriteLine((f.Count == 0 ? "PASS" : "FAIL") + "  " + file.PadRight(24) + methods + " method bodies, "
                                  + (numerics ? "numeric rules and " : "") + "P/Invoke checked: " + f.Count + " violation(s)"
                                  + (pinvokes > 0 ? " incl. " + pinvokes + " P/Invoke" : ""));
                fail += f.Count;
            }

            // ---- sensitivity: it sees what is there ---------------------------------------------------------
            fail += Expect(seen, "SeedLab.WorldGen.Unity.PerlinFast", "System.Runtime.Intrinsics.X86.Avx::ConvertToVector256Int32WithTruncation",
                           "the allowed truncating conversion in the 8-wide Perlin");
            fail += Expect(seen, "SeedLab.WorldGen.Unity.UnityRandom", "System.MathF::Sqrt", "the allowed MathF.Sqrt (sqrtss) in insideUnitCircle");
            fail += Expect(seen, "SeedLab.WorldGen.Simd.IsaSnapshot", "System.Runtime.Intrinsics.X86.Fma::get_IsSupported",
                           "an allowed IsSupported query of a forbidden ISA");

            // ---- sensitivity: it flags what must be flagged, and nothing else ----------------------------------
            string self = typeof(NumericsTripwire).Assembly.Location;
            List<(string, string)> plantSeen = new List<(string, string)>();
            List<Finding> bad = Scan(self, "SeedLabTests.PlantedNumerics.Forbidden", true, plantSeen, out _, out _);
            List<Finding> good = Scan(self, "SeedLabTests.PlantedNumerics.Allowed", true, new List<(string, string)>(), out _, out _);
            string[] mustFlag =
            {
                "Fma::MultiplyAdd", "System.MathF::Sin", "System.Math::FusedMultiplyAdd", "Vector256::FusedMultiplyAdd",
                "Vector128::MultiplyAddEstimate", "System.Math::ReciprocalEstimate", "Avx512F::ConvertToVector512Int32",
                "Avx::ConvertToVector256Int32", "Sse::ConvertToInt32", "Avx512F::Add", "System.Numerics.Vector3::Lerp",
                "Vector128::Sin", "PInvokeCos",
            };
            int missed = 0;
            foreach (string m in mustFlag)
            {
                bool hit = false;
                foreach (Finding x in bad)
                {
                    if (x.Target.Contains(m, StringComparison.Ordinal) || x.Method.Contains(m, StringComparison.Ordinal)) hit = true;
                }

                if (!hit)
                {
                    Console.WriteLine("FAIL  the planted " + m + " was NOT flagged - the tripwire is blind to it");
                    missed++;
                }
            }

            Console.WriteLine((missed == 0 ? "PASS" : "FAIL") + "  planted forbidden uses flagged: " + (mustFlag.Length - missed) + " of " + mustFlag.Length);
            fail += missed;

            foreach (Finding x in good) Console.WriteLine("FAIL  the allowed planted " + x.Target + " was flagged (" + x.Rule + ")");
            Console.WriteLine((good.Count == 0 ? "PASS" : "FAIL") + "  planted allowed uses left alone (truncating and widening conversions, "
                              + "MathF.Sqrt/Abs, Fma.IsSupported): " + good.Count + " false alarm(s)");
            fail += good.Count;

            // The other half of failing closed: when a vector path is refused, the sentence must say how
            // to keep working bit-exactly and which report to send - that report cannot run otherwise.
            fail += RefusalWording();

            Console.WriteLine();
            Console.WriteLine((fail == 0 ? "PASS" : "FAIL") + "  numerics tripwire");
            return fail == 0 ? 0 : 1;
        }

        private static int Expect(List<(string Method, string Target)> seen, string type, string target, string what)
        {
            foreach ((string m, string t) in seen)
            {
                if (m.StartsWith(type, StringComparison.Ordinal) && t.StartsWith(target, StringComparison.Ordinal))
                {
                    Console.WriteLine("PASS  sees " + what + ": " + m + " -> " + t);
                    return 0;
                }
            }

            Console.WriteLine("FAIL  did not see " + what + " (" + type + " -> " + target + ") - the scan is blind");
            return 1;
        }

        // =================================================================================================
        // The scan
        // =================================================================================================

        private static List<Finding> Scan(string path, string? onlyNamespace, bool numerics, List<(string, string)> seen,
                                          out int methods, out int pinvokes)
        {
            List<Finding> found = new List<Finding>();
            methods = 0;
            pinvokes = 0;
            using FileStream fs = File.OpenRead(path);
            using PEReader pe = new PEReader(fs);
            MetadataReader md = pe.GetMetadataReader();
            SignatureNames names = new SignatureNames(md);

            foreach (TypeDefinitionHandle th in md.TypeDefinitions)
            {
                TypeDefinition td = md.GetTypeDefinition(th);
                string outer = ProfileTripwire.OutermostName(md, th);
                if (onlyNamespace != null && !outer.StartsWith(onlyNamespace + ".", StringComparison.Ordinal)) continue;

                foreach (MethodDefinitionHandle mh in td.GetMethods())
                {
                    MethodDefinition m = md.GetMethodDefinition(mh);
                    string method = ProfileTripwire.FullTypeName(md, th) + "::" + md.GetString(m.Name);
                    if ((m.Attributes & MethodAttributes.PinvokeImpl) != 0)
                    {
                        pinvokes++;
                        found.Add(new Finding { Method = method, Target = "(P/Invoke)", Rule = "native code in a numeric assembly" });
                    }

                    if (m.RelativeVirtualAddress == 0) continue;
                    methods++;
                    MethodBodyBlock body = pe.GetMethodBody(m.RelativeVirtualAddress);
                    foreach (int token in ProfileTripwire.Tokens(body.GetILBytes() ?? Array.Empty<byte>()))
                    {
                        string? target = ProfileTripwire.Resolve(md, token);
                        if (target == null) continue;
                        seen.Add((method, target));
                        string? rule = target.StartsWith("System.Runtime.InteropServices.NativeLibrary::", StringComparison.Ordinal)
                            ? "native code in a numeric assembly"
                            : numerics ? Violation(target, names.Parameters(token)) : null;
                        if (rule != null) found.Add(new Finding { Method = method, Target = target, Rule = rule });
                    }
                }
            }

            return found;
        }

        /// <summary>The rule a referenced member breaks, or null. <paramref name="parameters"/> are its parameter types.</summary>
        private static string? Violation(string target, IReadOnlyList<string> parameters)
        {
            int sep = target.IndexOf("::", StringComparison.Ordinal);
            string type = target.Substring(0, sep);
            string member = target.Substring(sep + 2);
            if (member == "get_IsSupported") return null;

            if (type == "System.Runtime.Intrinsics.X86.Fma" || type.StartsWith("System.Runtime.Intrinsics.X86.Fma+", StringComparison.Ordinal))
                return "fused multiply-add";
            if (member.StartsWith("FusedMultiply", StringComparison.Ordinal)) return "fused multiply-add";
            if (member.Contains("Estimate", StringComparison.Ordinal)) return "an estimate, not the IEEE operation";

            bool vectorType = type.StartsWith("System.Runtime.Intrinsics.Vector", StringComparison.Ordinal)
                              || type == "System.Numerics.Vector" || type.StartsWith("System.Numerics.Vector`", StringComparison.Ordinal)
                              || type == "System.Numerics.Vector2" || type == "System.Numerics.Vector3" || type == "System.Numerics.Vector4";
            if (vectorType && VectorMaths.Contains(member)) return ".NET's own vector maths, not the C runtime's";

            if (type == "System.MathF" && !MathFExact.Contains(member)) return "MathF is not the game's (float)Math.X((double)f)";

            foreach (string p in parameters)
            {
                if (p == "System.Runtime.Intrinsics.X86.FloatRoundingMode") return "embedded rounding (FloatRoundingMode)";
            }

            if (member.StartsWith("ConvertTo", StringComparison.Ordinal) && member.EndsWith("Native", StringComparison.Ordinal))
                return "platform-specific conversion";

            if (member.StartsWith("ConvertTo", StringComparison.Ordinal) && member.Contains("Int", StringComparison.Ordinal)
                && !member.Contains("WithTruncation", StringComparison.Ordinal)
                && parameters.Count > 0 && IsFloating(parameters[0]))
            {
                return "a rounding float-to-integer conversion (C#'s cast truncates)";
            }

            return null;
        }

        private static bool IsFloating(string t) =>
            t == "float32" || t == "float64" || t.EndsWith("<float32>", StringComparison.Ordinal) || t.EndsWith("<float64>", StringComparison.Ordinal);

        /// <summary>A member reference's parameter types as strings, decoded from its signature blob.</summary>
        private sealed class SignatureNames : ISignatureTypeProvider<string, object?>
        {
            private readonly MetadataReader _md;

            public SignatureNames(MetadataReader md) => _md = md;

            public IReadOnlyList<string> Parameters(int token)
            {
                try
                {
                    EntityHandle h = MetadataTokens.EntityHandle(token);
                    BlobHandle sig;
                    switch (h.Kind)
                    {
                        case HandleKind.MemberReference:
                            MemberReference r = _md.GetMemberReference((MemberReferenceHandle)h);
                            if (r.GetKind() != MemberReferenceKind.Method) return Array.Empty<string>();
                            sig = r.Signature;
                            break;
                        case HandleKind.MethodDefinition:
                            sig = _md.GetMethodDefinition((MethodDefinitionHandle)h).Signature;
                            break;
                        case HandleKind.MethodSpecification:
                            return Parameters(MetadataTokens.GetToken(_md.GetMethodSpecification((MethodSpecificationHandle)h).Method));
                        default:
                            return Array.Empty<string>();
                    }

                    BlobReader br = _md.GetBlobReader(sig);
                    MethodSignature<string> ms = new SignatureDecoder<string, object?>(this, _md, null).DecodeMethodSignature(ref br);
                    return ms.ParameterTypes;
                }
                catch (Exception)
                {
                    return Array.Empty<string>();
                }
            }

            public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode switch
            {
                PrimitiveTypeCode.Single => "float32",
                PrimitiveTypeCode.Double => "float64",
                _ => typeCode.ToString().ToLowerInvariant(),
            };

            public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) =>
                ProfileTripwire.FullTypeName(reader, handle);

            public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) =>
                ProfileTripwire.RefName(reader, handle);

            public string GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind) =>
                "typespec";

            public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) =>
                genericType + "<" + string.Join(",", typeArguments) + ">";

            public string GetSZArrayType(string elementType) => elementType + "[]";
            public string GetArrayType(string elementType, ArrayShape shape) => elementType + "[,]";
            public string GetByReferenceType(string elementType) => elementType + "&";
            public string GetPointerType(string elementType) => elementType + "*";
            public string GetPinnedType(string elementType) => elementType;
            public string GetGenericMethodParameter(object? genericContext, int index) => "!!" + index;
            public string GetGenericTypeParameter(object? genericContext, int index) => "!" + index;
            public string GetFunctionPointerType(MethodSignature<string> signature) => "fnptr";
            public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;
        }
    }
}

namespace SeedLabTests.PlantedNumerics.Forbidden
{
    /// <summary>
    /// Every kind of reference the numerics tripwire must flag. Never called: it exists so the tripwire
    /// has to find it, which is how the tripwire proves it is not blind.
    /// </summary>
    internal static class Plant
    {
        internal static float Forbidden(Vector128<float> a, Vector256<float> b, Vector512<float> c, Vector512<double> d, float x)
        {
            Vector128<float> f1 = Fma.MultiplyAdd(a, a, a);
            float f2 = MathF.Sin(x);
            double f3 = Math.FusedMultiplyAdd(x, x, x);
            Vector256<float> f4 = Vector256.FusedMultiplyAdd(b, b, b);
            Vector128<float> f5 = Vector128.MultiplyAddEstimate(a, a, a);
            double f6 = Math.ReciprocalEstimate(x);
            Vector512<int> f7 = Avx512F.ConvertToVector512Int32(c);
            Vector256<int> f8 = Avx.ConvertToVector256Int32(b);
            int f9 = Sse.ConvertToInt32(a);
            Vector512<double> f10 = Avx512F.Add(d, d, FloatRoundingMode.ToZero);
            Vector3 f11 = Vector3.Lerp(Vector3.One, Vector3.Zero, x);
            Vector128<float> f12 = Vector128.Sin(a);
            double f13 = PInvokeCos(x);
            return f1.ToScalar() + f2 + (float)f3 + f4.ToScalar() + f5.ToScalar() + (float)f6 + f7.ToScalar() + f8.ToScalar()
                   + f9 + (float)f10.ToScalar() + f11.X + f12.ToScalar() + (float)f13;
        }

        [DllImport("ucrtbase.dll", EntryPoint = "cos")]
        private static extern double PInvokeCos(double x);
    }
}

namespace SeedLabTests.PlantedNumerics.Allowed
{
    /// <summary>References the tripwire must leave alone: exact conversions and operations, and a query.</summary>
    internal static class Plant
    {
        internal static float Allowed(Vector256<float> b, Vector128<byte> bytes, float x)
        {
            Vector256<int> a1 = Avx.ConvertToVector256Int32WithTruncation(b);
            Vector512<int> a2 = Avx512F.ConvertToVector512Int32(bytes);
            float a3 = MathF.Sqrt(x) + MathF.Abs(x);
            bool a4 = Fma.IsSupported;
            return a1.ToScalar() + a2.ToScalar() + a3 + (a4 ? 1f : 0f);
        }
    }
}
