using System;
using System.Collections.Generic;
using SeedLab.Contracts.Dump;

namespace SeedLab.Dumper
{
    /// <summary>
    /// Writes the manifest that lets the offline tool refuse stale or partial data.
    ///
    /// The three modes share one per-build folder, and a folder can therefore hold files from several
    /// runs. So the file list is not "what this run wrote" - it is a fresh scan and re-hash of
    /// <b>every</b> file in the folder. That way a natives run after an asset run cannot leave behind a
    /// manifest that silently omits half the dump, and a file changed or truncated by anything at all
    /// shows up as a hash mismatch.
    ///
    /// Each run writes two copies: <c>manifest-&lt;mode&gt;.json</c>, which keeps that run's own
    /// metadata (the world it came from, the counts, its warnings) permanently, and
    /// <c>manifest.json</c>, which is the entry point the tool reads and always reflects the latest
    /// run. Neither lists itself.
    /// </summary>
    internal static class Manifest
    {
        public static void Write(DumpWriter w, DumpManifest manifest, string mode)
        {
            manifest.files = new List<FileEntryDef>(w.ScanAllFiles(
                DumpFormat.ManifestFile,
                DumpFormat.ManifestFileForMode("assets"),
                DumpFormat.ManifestFileForMode("natives"),
                DumpFormat.ManifestFileForMode("worldgen"))).ToArray();

            var notes = new List<string>(manifest.notes ?? new string[0]);
            notes.Add("files[] is a scan of the whole dump folder, so it may include files written by " +
                      "an earlier run in another mode. Each file carries its own stamp; this run's own " +
                      "metadata is preserved in manifest-" + mode + ".json.");
            manifest.notes = notes.ToArray();

            w.WriteJson(DumpFormat.ManifestFileForMode(mode), manifest);
            w.WriteJson(DumpFormat.ManifestFile, manifest);
        }
    }
}
