using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.Intrinsics.X86;
using System.Text;

namespace SeedLab.Runtime.Hardware
{
    /// <summary>
    /// Which processor this is: vendor, family, model, stepping and brand string, from the CPUID
    /// instruction through the BCL's <see cref="X86Base.CpuId"/> - no WMI, no registry, no P/Invoke.
    ///
    /// <para><b>What it is for.</b> The self-test stamp is filed under this identity, so a stamp earned
    /// on one processor is never trusted on another (a disk moved to a new PC, a VM migrated to a host
    /// with a different CPU), and a machine report says which processor it came from. It is hardware
    /// only: CPUID leaf 3 (the old processor serial number) is never read, and nothing here names the
    /// machine or its owner.</para>
    ///
    /// <para><b>Family and model</b> are the display values both vendors define: the extended family is
    /// added when the base family is 0xF, and the extended model is prefixed when the base family is 6
    /// or 0xF. A Zen 5 desktop reads family 26 (0x1A); Windows' <c>PROCESSOR_IDENTIFIER</c> uses the same
    /// convention, which is why it is the fallback when CPUID cannot be asked (the runtime reports
    /// <see cref="X86Base.IsSupported"/> false under <c>DOTNET_EnableHWIntrinsic=0</c>), and
    /// <c>/proc/cpuinfo</c> is on Linux.</para>
    /// </summary>
    public sealed class CpuIdentity
    {
        private CpuIdentity(string vendor, int? family, int? model, int? stepping, string brand,
                            bool? hybrid, string source)
        {
            Vendor = vendor;
            Family = family;
            Model = model;
            Stepping = stepping;
            Brand = brand;
            Hybrid = hybrid;
            Source = source;
        }

        /// <summary>"GenuineIntel", "AuthenticAMD", ... or "unknown".</summary>
        public string Vendor { get; }

        public int? Family { get; }
        public int? Model { get; }
        public int? Stepping { get; }

        /// <summary>The marketing name the CPU reports about itself, trimmed; "" when unknown.</summary>
        public string Brand { get; }

        /// <summary>
        /// CPUID.(EAX=7,ECX=0):EDX[15], "hybrid part" (Intel P+E cores). Null when not asked. AMD's
        /// Zen 5 + Zen 5c parts do not set it; their cores differ in clock, not in ISA.
        /// </summary>
        public bool? Hybrid { get; }

        /// <summary>"cpuid", "env:PROCESSOR_IDENTIFIER", "proc-cpuinfo" or "unavailable".</summary>
        public string Source { get; }

        /// <summary>The stamp's CPU component: <c>cpu=AuthenticAMD/26/68/0</c>.</summary>
        public string Key =>
            "cpu=" + Vendor + "/" + N(Family) + "/" + N(Model) + "/" + N(Stepping);

        /// <summary>One line for a machine block: "AuthenticAMD family 26 model 68 stepping 0, AMD Ryzen ...".</summary>
        public string Describe()
        {
            if (Source == "unavailable") return "processor identity unavailable on this runtime";
            string s = Vendor + " family " + N(Family) + " model " + N(Model) + " stepping " + N(Stepping);
            if (Brand.Length > 0) s += ", " + Brand;
            if (Hybrid == true) s += ", hybrid (performance + efficiency cores)";
            if (Source != "cpuid") s += " [" + Source + "]";
            return s;
        }

        private static string N(int? v) => v.HasValue ? v.Value.ToString(CultureInfo.InvariantCulture) : "?";

        /// <summary>Reads this processor's identity. Never throws.</summary>
        public static CpuIdentity Probe()
        {
            CpuIdentity? id = null;
            try { id = FromCpuId(); }
            catch (Exception) { id = null; }
            if (id != null) return id;

            try { id = FromProcessorIdentifier(Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER")); }
            catch (Exception) { id = null; }
            if (id != null) return id;

            try { id = FromProcCpuInfo(); }
            catch (Exception) { id = null; }
            return id ?? new CpuIdentity("unknown", null, null, null, "", null, "unavailable");
        }

        private static CpuIdentity? FromCpuId()
        {
            if (!X86Base.IsSupported) return null;

            (int maxLeaf, int b0, int c0, int d0) = X86Base.CpuId(0, 0);
            string vendor = Ascii(b0, d0, c0).Trim();
            if (vendor.Length == 0) vendor = "unknown";

            int? family = null, model = null, stepping = null;
            if (maxLeaf >= 1)
            {
                (int a1, _, _, _) = X86Base.CpuId(1, 0);
                (family, model, stepping) = Decode(a1);
            }

            bool? hybrid = null;
            if (maxLeaf >= 7)
            {
                (_, _, _, int d7) = X86Base.CpuId(7, 0);
                hybrid = (d7 & (1 << 15)) != 0;
            }

            string brand = "";
            (int maxExt, _, _, _) = X86Base.CpuId(unchecked((int)0x80000000), 0);
            if (unchecked((uint)maxExt) >= 0x80000004u)
            {
                StringBuilder sb = new StringBuilder(48);
                for (uint leaf = 0x80000002u; leaf <= 0x80000004u; leaf++)
                {
                    (int a, int b, int c, int d) = X86Base.CpuId(unchecked((int)leaf), 0);
                    sb.Append(Ascii(a, b, c, d));
                }

                brand = Clean(sb.ToString());
            }

            return new CpuIdentity(vendor, family, model, stepping, brand, hybrid, "cpuid");
        }

        /// <summary>CPUID leaf 1 EAX to the display family, model and stepping (both vendors' rule).</summary>
        public static (int Family, int Model, int Stepping) Decode(int eax)
        {
            int stepping = eax & 0xF;
            int baseModel = (eax >> 4) & 0xF;
            int baseFamily = (eax >> 8) & 0xF;
            int extModel = (eax >> 16) & 0xF;
            int extFamily = (eax >> 20) & 0xFF;
            int family = baseFamily == 0xF ? baseFamily + extFamily : baseFamily;
            int model = baseFamily == 0x6 || baseFamily == 0xF ? (extModel << 4) + baseModel : baseModel;
            return (family, model, stepping);
        }

        /// <summary>"AMD64 Family 26 Model 68 Stepping 0, AuthenticAMD" (Windows sets it for every process).</summary>
        public static CpuIdentity? FromProcessorIdentifier(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            string[] parts = text.Split(',');
            string vendor = parts.Length > 1 ? parts[parts.Length - 1].Trim() : "unknown";
            string[] words = parts[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            int? family = null, model = null, stepping = null;
            for (int i = 0; i + 1 < words.Length; i++)
            {
                if (!int.TryParse(words[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)) continue;
                switch (words[i])
                {
                    case "Family": family = v; break;
                    case "Model": model = v; break;
                    case "Stepping": stepping = v; break;
                }
            }

            if (family == null && model == null) return null;
            return new CpuIdentity(vendor.Length == 0 ? "unknown" : vendor, family, model, stepping, "", null,
                                   "env:PROCESSOR_IDENTIFIER");
        }

        private static CpuIdentity? FromProcCpuInfo()
        {
            const string path = "/proc/cpuinfo";
            if (!File.Exists(path)) return null;
            Dictionary<string, string> first = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string line in File.ReadLines(path))
            {
                if (line.Trim().Length == 0)
                {
                    if (first.Count > 0) break;      // the first processor's block is enough
                    continue;
                }

                int colon = line.IndexOf(':');
                if (colon <= 0) continue;
                string key = line.Substring(0, colon).Trim();
                if (!first.ContainsKey(key)) first[key] = line.Substring(colon + 1).Trim();
            }

            if (!first.TryGetValue("vendor_id", out string? vendor)) return null;
            return new CpuIdentity(vendor, Int(first, "cpu family"), Int(first, "model"), Int(first, "stepping"),
                                   first.TryGetValue("model name", out string? brand) ? Clean(brand) : "", null,
                                   "proc-cpuinfo");
        }

        private static int? Int(Dictionary<string, string> d, string key) =>
            d.TryGetValue(key, out string? s) && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)
                ? v
                : null;

        private static string Ascii(params int[] regs)
        {
            StringBuilder sb = new StringBuilder(regs.Length * 4);
            foreach (int r in regs)
            {
                for (int i = 0; i < 4; i++)
                {
                    char c = (char)((r >> (8 * i)) & 0xFF);
                    if (c != '\0') sb.Append(c);
                }
            }

            return sb.ToString();
        }

        /// <summary>Printable ASCII only, runs of spaces folded: brand strings are padded and sometimes odd.</summary>
        private static string Clean(string s)
        {
            StringBuilder sb = new StringBuilder(s.Length);
            bool space = false;
            foreach (char c in s)
            {
                if (c < 0x20 || c > 0x7E) continue;
                if (c == ' ')
                {
                    if (!space && sb.Length > 0) sb.Append(' ');
                    space = true;
                    continue;
                }

                sb.Append(c);
                space = false;
            }

            return sb.ToString().Trim();
        }

        // ---- the C runtime's math library ------------------------------------------------------------

        /// <summary>
        /// The version of <c>ucrtbase.dll</c> in the system directory, "10.0.19041.3636", or "n/a" off
        /// Windows. <c>Math.Sin/Cos/Atan2/Pow</c> in .NET on Windows are that DLL's functions (coreclr
        /// imports the CRT's math API set), and it picks an FMA3 or a non-FMA3 implementation by CPU, so
        /// a Windows update that replaces it could change a last bit. It is part of the self-test stamp
        /// for that reason: a new ucrtbase re-runs the self-test. Only the version is kept - never the
        /// path, which would name nothing useful and could name the machine.
        /// </summary>
        public static string UcrtVersion()
        {
            try
            {
                if (!OperatingSystem.IsWindows()) return "n/a";
                string path = Path.Combine(Environment.SystemDirectory, "ucrtbase.dll");
                if (!File.Exists(path)) return "absent";
                FileVersionInfo v = FileVersionInfo.GetVersionInfo(path);

                // The version resource's text, "10.0.19041.3636", first: measured on Windows 10 19045
                // under .NET 10.0.12, the numeric FileMajorPart/FileMinorPart read 6.2 for the same file
                // (while Windows PowerShell reads 10.0), so the numbers alone would name a version no
                // one would recognise. They are only the fallback.
                foreach (string? text in new[] { v.ProductVersion, v.FileVersion })
                {
                    string first = (text ?? "").Trim().Split(' ')[0];
                    if (first.Length > 0 && char.IsDigit(first[0])) return first;
                }

                return v.FileMajorPart.ToString(CultureInfo.InvariantCulture) + "."
                       + v.FileMinorPart.ToString(CultureInfo.InvariantCulture) + "."
                       + v.FileBuildPart.ToString(CultureInfo.InvariantCulture) + "."
                       + v.FilePrivatePart.ToString(CultureInfo.InvariantCulture);
            }
            catch (Exception)
            {
                return "unreadable";
            }
        }
    }
}
