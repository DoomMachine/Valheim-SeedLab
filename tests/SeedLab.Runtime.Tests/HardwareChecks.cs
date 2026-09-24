using System;
using System.IO;
using SeedLab.Runtime.Hardware;

namespace SeedLab.RuntimeTests
{
    public static class HardwareChecks
    {
        public static void Run(Action<bool, string, string> check)
        {
            HardwareInfo hw = HardwareProbe.Probe();

            check(hw.LogicalCores >= 1, "logical cores", hw.LogicalCores + " reported");
            check(hw.TotalMemoryBytes > 0, "total memory", Bytes.Human(hw.TotalMemoryBytes));
            check(hw.AvailableMemoryBytes > 0 && hw.AvailableMemoryBytes <= hw.TotalMemoryBytes,
                "available memory is real and no larger than total",
                Bytes.Human(hw.AvailableMemoryBytes) + " of " + Bytes.Human(hw.TotalMemoryBytes)
                + " [" + hw.MemorySource + "]");

            // GCMemoryInfo.MemoryLoadBytes is zero until the first GC; the probe forces one, so
            // "available" must not simply equal "total" on a machine that is using memory.
            check(hw.AvailableMemoryBytes < hw.TotalMemoryBytes,
                "the memory-load refresh worked (available < total)",
                Bytes.Human(hw.TotalMemoryBytes - hw.AvailableMemoryBytes) + " in use");

            check(hw.RuntimeIdentifier.Length > 0 && hw.FrameworkDescription.Length > 0,
                "platform identity", hw.RuntimeIdentifier + ", " + hw.FrameworkDescription);
            check(hw.PlatformKey.Length > 0, "platform key for the self-test stamp", hw.PlatformKey);

            check(hw.PhysicalCores == null || hw.PhysicalCores > 0,
                "physical cores are reported or honestly absent",
                hw.PhysicalCores?.ToString() ?? ("unavailable (" + hw.PhysicalCoreSource + ")"));

            VolumeInfo v = VolumeInfo.For(Directory.GetCurrentDirectory());
            check(v.IsReady && v.FreeBytes > 0 && v.TotalBytes >= v.FreeBytes,
                "free space on the working volume", v.ToString());

            VolumeInfo missing = VolumeInfo.For(Path.Combine(Path.GetTempPath(), "seedlab-does-not-exist", "x.jsonl"));
            check(missing.TotalBytes > 0, "a path that does not exist still resolves to its volume",
                missing.Name + " " + Bytes.Human(missing.FreeBytes) + " free");

            check(VolumeInfo.For("").TotalBytes >= 0, "an empty path does not throw", "falls back to the cwd");

            // Overrides let every later test plan for a machine that is not this one.
            HardwareInfo small = HardwareProbe.Probe(new HardwareProbeOptions
            {
                LogicalCoresOverride = 2,
                AvailableMemoryOverride = 2L * Bytes.GiB,
                PhysicalCoresOverride = 2
            });
            check(small.LogicalCores == 2 && small.AvailableMemoryBytes == 2L * Bytes.GiB
                  && small.PhysicalCores == 2,
                "probe overrides model a smaller machine", small.ToString());

            check(hw.Features.Key.Length > 0, "ISA flags", hw.Features.Describe());
            check(!(hw.ProcessArchitecture == System.Runtime.InteropServices.Architecture.X64 && !hw.Features.Sse2),
                "x64 implies SSE2", hw.ProcessArchitecture.ToString());

            check(Bytes.Human(1536) == "1.5 KiB" && Bytes.Human(0) == "0 B",
                "byte formatting", Bytes.Human(1536) + ", " + Bytes.Human(3_221_225_472L));
            check(Bytes.Duration(TimeSpan.FromSeconds(3661)).StartsWith("1 h"),
                "duration formatting", Bytes.Duration(TimeSpan.FromSeconds(3661))
                + ", " + Bytes.Duration(TimeSpan.FromSeconds(400_000)));
        }
    }
}
