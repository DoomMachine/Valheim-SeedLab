using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;

namespace SeedLab.Web
{
    /// <summary>
    /// The front end, served from the assembly.
    ///
    /// <para><b>There is no file serving in this server at all.</b> No static-file middleware, no
    /// physical file provider, no path is ever built from anything a request contained. A request
    /// path is looked up in the fixed table below and is either one of these four names or a 404 -
    /// so "refuse to serve any file outside its own content root" holds by construction, and
    /// <c>/../../Program.cs</c>, <c>/%2e%2e/</c> and every other traversal spelling are simply names
    /// that are not in a dictionary.</para>
    ///
    /// <para>It is also why the page works with no network: every byte of HTML, CSS, JavaScript and
    /// the icon is inside vseed's own binaries. There is no CDN, no web font and no external URL
    /// anywhere in them, and the Content-Security-Policy the server sends forbids one from being
    /// added by accident.</para>
    /// </summary>
    public static class StaticAssets
    {
        public sealed class Asset
        {
            public Asset(string path, string resource, string contentType)
            {
                Path = path;
                Resource = resource;
                ContentType = contentType;
                Bytes = Load(resource);
                ETag = "\"" + Convert.ToHexString(SHA256.HashData(Bytes), 0, 8).ToLowerInvariant() + "\"";
            }

            public string Path { get; }

            public string Resource { get; }

            public string ContentType { get; }

            public byte[] Bytes { get; }

            /// <summary>Content hash, so a reload after a rebuild cannot serve the old page.</summary>
            public string ETag { get; }
        }

        private static readonly Lazy<Dictionary<string, Asset>> Table =
            new Lazy<Dictionary<string, Asset>>(Build, isThreadSafe: true);

        private static Dictionary<string, Asset> Build()
        {
            Asset[] all =
            {
                new Asset("/", "seedlab.web.index.html", "text/html; charset=utf-8"),
                new Asset("/app.css", "seedlab.web.app.css", "text/css; charset=utf-8"),
                new Asset("/app.js", "seedlab.web.app.js", "text/javascript; charset=utf-8"),
                new Asset("/favicon.svg", "seedlab.web.favicon.svg", "image/svg+xml"),
            };

            Dictionary<string, Asset> map = new Dictionary<string, Asset>(StringComparer.Ordinal);
            foreach (Asset a in all) map[a.Path] = a;
            map["/index.html"] = map["/"];
            return map;
        }

        public static bool TryGet(string path, out Asset asset)
            => Table.Value.TryGetValue(path, out asset!);

        public static IEnumerable<Asset> All
        {
            get
            {
                HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (KeyValuePair<string, Asset> kv in Table.Value)
                {
                    if (seen.Add(kv.Value.Resource)) yield return kv.Value;
                }
            }
        }

        private static byte[] Load(string resource)
        {
            Assembly asm = typeof(StaticAssets).Assembly;
            using Stream? s = asm.GetManifestResourceStream(resource);
            if (s == null)
            {
                throw new InvalidOperationException(
                    "Embedded resource '" + resource + "' is missing from " + asm.GetName().Name
                    + ". The wwwroot files are listed one by one in SeedLab.Web.csproj with explicit "
                    + "logical names; a rename there without a rename here produces exactly this.");
            }

            using MemoryStream ms = new MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }
    }
}
