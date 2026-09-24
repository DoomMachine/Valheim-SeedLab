using System;
using System.Collections;
using System.Collections.Generic;

namespace SeedLab.Runtime.Hardware
{
    /// <summary>
    /// The .NET runtime's instruction-set and code-generation switches that are set in this process's
    /// environment.
    ///
    /// <para><b>Why a prefix scan, not a list.</b> The runtime reads every switch under two prefixes,
    /// <c>DOTNET_</c> and the older <c>COMPlus_</c> (10.0.12 still honours both: measured,
    /// <c>COMPlus_EnableAVX512=0</c> turns AVX-512 off exactly as <c>DOTNET_EnableAVX512=0</c> does), and
    /// each release adds ISA switches (<c>coreclr.dll</c> and <c>clrjit.dll</c> 10.0.12 name
    /// <c>EnableAVX10v2</c>, <c>EnableAPX</c> and <c>EnableAVX512v3</c> among others, and the JIT has
    /// its own <c>Enable*</c> code-generation switches such as <c>EnableEmbeddedBroadcast</c>). A fixed
    /// list misses the next one, and a per-level proof whose "default" level secretly ran with a switch
    /// set would call real levels "not applicable" and still pass. So every variable of the form
    /// <c>DOTNET_Enable*</c> / <c>COMPlus_Enable*</c> counts, as do the two vector-width switches, except
    /// the runtime's <c>Enable*</c> switches that do not change generated code (diagnostics, dumps,
    /// profiler attach, W^X).</para>
    /// </summary>
    public static class RuntimeKnobs
    {
        private static readonly string[] Prefixes = { "DOTNET_", "COMPlus_" };

        private static readonly string[] WidthKnobs = { "PreferredVectorBitWidth", "MaxVectorTBitWidth" };

        /// <summary>
        /// The <c>Enable*</c> switches in <c>coreclr.dll</c> 10.0.12 that do not change the code the JIT
        /// generates. Anything else named <c>Enable*</c> is treated as one that can.
        /// </summary>
        private static readonly string[] NotIsa =
        {
            "EnableDiagnostics", "EnableDiagnostics_IPC", "EnableDiagnostics_Debugger", "EnableDiagnostics_Profiler",
            "EnableEventPipe", "EnableFastHeapDumps", "EnableMiniDump", "EnableRCWCleanupOnSTAShutdown",
            "EnableStackwalk", "EnableV2Profiler", "EnableWriteXorExecute",
        };

        /// <summary>
        /// Whether <paramref name="name"/> is a runtime switch that can change the ISA state, the vector
        /// width or the code the JIT generates. Case-insensitive, as Windows environment names are.
        /// </summary>
        public static bool IsIsaKnob(string name)
        {
            foreach (string prefix in Prefixes)
            {
                if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                string rest = name.Substring(prefix.Length);
                foreach (string w in WidthKnobs)
                {
                    if (string.Equals(rest, w, StringComparison.OrdinalIgnoreCase)) return true;
                }

                if (!rest.StartsWith("Enable", StringComparison.OrdinalIgnoreCase)) return false;
                foreach (string n in NotIsa)
                {
                    if (string.Equals(rest, n, StringComparison.OrdinalIgnoreCase)) return false;
                }

                return true;
            }

            return false;
        }

        /// <summary>Every ISA switch set to a non-empty value in this process's environment, sorted by name.</summary>
        public static IReadOnlyList<(string Name, string Value)> Set()
        {
            List<(string, string)> r = new List<(string, string)>();
            IDictionary env;
            try { env = Environment.GetEnvironmentVariables(); }
            catch (Exception) { return r; }

            foreach (DictionaryEntry e in env)
            {
                string? name = e.Key as string;
                string? value = e.Value as string;
                if (name == null || string.IsNullOrEmpty(value) || !IsIsaKnob(name)) continue;
                r.Add((name, value));
            }

            r.Sort((a, b) => string.CompareOrdinal(a.Item1, b.Item1));
            return r;
        }

        /// <summary>
        /// The ISA switches in <paramref name="environment"/> (a child's <c>ProcessStartInfo.Environment</c>),
        /// so a harness can take them all out before it sets the one its level names.
        /// </summary>
        public static List<string> In(IDictionary<string, string?> environment)
        {
            List<string> r = new List<string>();
            foreach (KeyValuePair<string, string?> kv in environment)
            {
                if (IsIsaKnob(kv.Key)) r.Add(kv.Key);
            }

            return r;
        }
    }
}
