using System;

namespace SeedLab.Cli.Infra
{
    /// <summary>
    /// <c>--simd auto|scalar|avx2|avx512</c>: which vector path the generator may use, for this process.
    ///
    /// <para>The generator's dispatch (<c>SeedLab.WorldGen.Simd.SimdDispatch</c>) reads
    /// <c>SEEDLAB_SIMD</c> once, in a type initialiser, into <c>static readonly</c> fields the JIT folds
    /// into constants - which is why the vector path costs nothing to choose. The price is that the
    /// choice must be in the environment before the first generator type is touched, so
    /// <see cref="Apply"/> is one of the first statements of <c>Main</c>, beside the profiler's counter
    /// switch, and it reads the raw arguments itself. It must not call into <c>SimdDispatch</c> to
    /// validate the value: that would run the type initialiser before the variable is set. The four
    /// names here are <c>SimdDispatch.TryParseRequest</c>'s.</para>
    ///
    /// <para>Every path is proved to give the same bits (the startup self-test, and the per-level
    /// proof), so the flag changes only speed. It exists for the machines where the widest path is not
    /// the fastest (slow gathers on some older CPUs) and for the per-level proof itself.</para>
    /// </summary>
    public static class SimdSwitch
    {
        /// <summary>SimdDispatch.EnvironmentVariable - a const, so reading it here loads nothing.</summary>
        private const string Variable = SeedLab.WorldGen.Simd.SimdDispatch.EnvironmentVariable;

        /// <summary>
        /// Sets <c>SEEDLAB_SIMD</c> from <c>--simd</c> when it is present. Returns null, or the error
        /// line for a value that is not one of the four names (Main prints it and exits 2).
        /// </summary>
        public static string? Apply(string[] rawArgs)
        {
            string? value = null;
            bool seen = false;
            for (int i = 0; i < rawArgs.Length; i++)
            {
                string t = rawArgs[i];
                if (t == "--") break;
                if (t == "--simd")
                {
                    seen = true;
                    value = i + 1 < rawArgs.Length ? rawArgs[i + 1] : null;
                    i++;
                }
                else if (t.StartsWith("--simd=", StringComparison.Ordinal))
                {
                    seen = true;
                    value = t.Substring("--simd=".Length);
                }
            }

            if (!seen) return null;
            string v = (value ?? "").Trim().ToLowerInvariant();
            if (v is "auto" or "scalar" or "avx2" or "avx512")
            {
                Environment.SetEnvironmentVariable(Variable, v);
                return null;
            }

            return "--simd: '" + (value ?? "") + "' is not auto, scalar, avx2 or avx512.";
        }
    }
}
