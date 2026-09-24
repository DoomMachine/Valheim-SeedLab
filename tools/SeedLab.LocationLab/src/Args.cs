using System;
using System.Globalization;

namespace SeedLab.LocationLab
{
    internal static class Args
    {
        public static bool Has(string[] a, string flag)
        {
            foreach (string s in a) if (string.Equals(s, flag, StringComparison.Ordinal)) return true;
            return false;
        }

        public static string Str(string[] a, string flag, string fallback)
        {
            for (int i = 0; i < a.Length - 1; i++)
                if (string.Equals(a[i], flag, StringComparison.Ordinal)) return a[i + 1];
            return fallback;
        }

        public static int Int(string[] a, string flag, int fallback)
        {
            string s = Str(a, flag, "");
            return s.Length > 0 && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)
                ? v : fallback;
        }
    }
}
