using System;
using System.Collections.Generic;

namespace SeedLab.Dumper
{
    /// <summary>
    /// The console command handlers. Each one re-checks the refusal rules itself, at the moment it is
    /// invoked - a peer can connect between the moment the command was typed and the moment it runs.
    /// </summary>
    internal static class Commands
    {
        public static void Status(Terminal.ConsoleEventArgs args)
        {
            Terminal t = args.Context;
            try
            {
                Plugin.Say(t, "SeedLab.Dumper " + Plugin.VERSION + " - mode '" +
                              Plugin.Mode.ToString().ToLowerInvariant() + "'" +
                              (Plugin.Disabled ? ", DISABLED for this session" : ""));
                Plugin.Say(t, "output: " + Plugin.Instance.ResolveOutputRoot());

                string reason;
                Plugin.Say(t, "asset dump allowed: " +
                              (Safety.SoloHostWithWorld(out reason) ? "yes" : "no - " + reason));
                Plugin.Say(t, "native dump allowed: " +
                              (Safety.NoPeers(out reason) ? "yes" : "no - " + reason));
                Plugin.Say(t, "worldgen dump allowed: " +
                              (Safety.MainMenuOnly(out reason) ? "yes" : "no - " + reason));

                var g = GameInfo.Read();
                Plugin.Say(t, "game " + g.version + " network " + g.networkVersion + " unity " + g.unityVersion);
                Plugin.Say(t, "assembly_valheim sha256 " + (g.assemblyValheimSha256 ?? "?"));
                Plugin.Say(t, "UnityPlayer.dll   sha256 " + (g.unityPlayerSha256 ?? "?"));
                Plugin.Say(t, "GenerateAltBiomes has run " + AltBiomeCapture.Runs + " time(s) this session" +
                              (AltBiomeCapture.Contaminated ? " - CONTAMINATED" : ""));
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("seedlab_status failed: " + e);
            }
        }

        public static void Assets(Terminal.ConsoleEventArgs args)
        {
            Terminal t = args.Context;
            try
            {
                if (!Plugin.Allows(DumpMode.Assets))
                {
                    Plugin.Say(t, "SeedLab.Dumper: mode is '" + Plugin.Mode.ToString().ToLowerInvariant() +
                                  "'. Put 'assets' (or 'all') in dumper.enable and restart the game.");
                    return;
                }
                string reason;
                if (Safety.MultiplayerBreach(out reason))
                {
                    Plugin.Refuse(t, "the asset dump", reason);
                    return;
                }
                if (!Safety.SoloHostWithWorld(out reason))
                {
                    // Not a breach, just not yet: say so and stay armed.
                    Plugin.Say(t, "SeedLab.Dumper: not now - " + reason);
                    return;
                }
                string grid = "none";
                for (int i = 1; i < args.Length; i++)
                {
                    string a = args[i];
                    if (!string.IsNullOrEmpty(a) && a.StartsWith("grid=", StringComparison.OrdinalIgnoreCase))
                    {
                        grid = a.Substring(5).Trim().ToLowerInvariant();
                    }
                }
                if (!Grids.IsKnown(grid))
                {
                    Plugin.Say(t, "SeedLab.Dumper: unknown grid '" + grid +
                                  "'. Use none, coarse128, findlakes, edges or full12.");
                    return;
                }
                Plugin.Instance.RunAssets(t, grid);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("seedlab_dump failed: " + e);
            }
        }

        public static void Natives(Terminal.ConsoleEventArgs args)
        {
            Terminal t = args.Context;
            try
            {
                if (!Plugin.Allows(DumpMode.Natives))
                {
                    Plugin.Say(t, "SeedLab.Dumper: mode is '" + Plugin.Mode.ToString().ToLowerInvariant() +
                                  "'. Put 'natives' (or 'all') in dumper.enable and restart the game.");
                    return;
                }
                string reason;
                if (Safety.MultiplayerBreach(out reason))
                {
                    Plugin.Refuse(t, "the native-function dump", reason);
                    return;
                }
                Plugin.Instance.RunNatives(t);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("seedlab_natives failed: " + e);
            }
        }

        public static void WorldGen(Terminal.ConsoleEventArgs args)
        {
            Terminal t = args.Context;
            try
            {
                if (!Plugin.Allows(DumpMode.WorldGen))
                {
                    Plugin.Say(t, "SeedLab.Dumper: mode is '" + Plugin.Mode.ToString().ToLowerInvariant() +
                                  "'. Put 'worldgen' (or 'all') in dumper.enable and restart the game.");
                    return;
                }
                string reason;
                if (Safety.MultiplayerBreach(out reason))
                {
                    Plugin.Refuse(t, "the world-generator dump", reason);
                    return;
                }
                if (!Safety.MainMenuOnly(out reason))
                {
                    // Not a Refuse(): running it in-world is a mistake, not a breach, and the user
                    // should be able to quit to the menu and try again in the same session.
                    Plugin.Say(t, "SeedLab.Dumper: " + reason);
                    return;
                }

                var seeds = new List<string>();
                string grid = "none";
                for (int i = 1; i < args.Length; i++)
                {
                    string a = args[i];
                    if (string.IsNullOrEmpty(a)) continue;
                    if (a.StartsWith("grid=", StringComparison.OrdinalIgnoreCase))
                    {
                        grid = a.Substring(5).Trim().ToLowerInvariant();
                        continue;
                    }
                    seeds.Add(a);
                }
                if (seeds.Count == 0)
                {
                    Plugin.Say(t, "usage: seedlab_worldgen <seedText> [<seedText> ...] " +
                                  "[grid=none|coarse128|findlakes|full12]");
                    return;
                }
                if (seeds.Count > 32)
                {
                    Plugin.Say(t, "SeedLab.Dumper: 32 seeds at a time is the limit - this produces " +
                                  "reference data, not the product.");
                    return;
                }
                Plugin.Instance.RunWorldGen(t, seeds.ToArray(), grid);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("seedlab_worldgen failed: " + e);
            }
        }
    }
}
