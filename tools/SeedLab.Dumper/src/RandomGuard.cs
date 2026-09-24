using System;
using System.Collections.Generic;
using System.Reflection;
using URandom = UnityEngine.Random;

namespace SeedLab.Dumper
{
    /// <summary>
    /// Saves and restores the global <c>UnityEngine.Random</c> state around anything this plugin does.
    ///
    /// World generation, alt-biome assignment and location placement all run on that single global
    /// stream, so a dumper that draws from it perturbs the user's world. This is exactly what the game
    /// itself does around its own seeded work: <c>WorldGenerator..ctor</c> saves <c>Random.state</c>,
    /// calls <c>InitState(m_world.m_seed)</c>, draws its seven values and restores
    /// (WorldGenerator.cs:212-234); <c>ZoneSystem.PlaceVegetation</c> at :1383/:1584 and
    /// <c>GenerateLocationsTimeSliced</c> at :1881/:2139 do the same.
    ///
    /// Rule for this plugin: every code path that can touch <c>UnityEngine.Random</c> - including any
    /// <c>new World(...)</c>, because <c>World..ctor</c> calls <c>Utils.GenerateUID</c> which draws one
    /// <c>Random.Range(1, int.MaxValue)</c> (Utils.cs:424) - is inside one of these.
    ///
    /// <b>Why this is a CLASS and not a struct (2026-09-23).</b> It was
    /// <c>internal readonly struct RandomGuard</c> with a single constructor
    /// <c>RandomGuard(int? initState = null)</c>, and every argumentless site was written
    /// <c>using (new RandomGuard())</c>. For a struct the implicit parameterless constructor is always
    /// a member, and C# better-function-member resolution prefers the candidate that needs no
    /// default-argument substitution - so <c>new RandomGuard()</c> bound to the IMPLICIT constructor and
    /// Roslyn emitted <c>initobj</c>, not <c>call .ctor</c>. The constructor body never ran, <c>_saved</c>
    /// stayed <c>default(Random.State)</c> = <c>{0,0,0,0}</c>, and <c>Dispose</c> wrote four zeros into
    /// the global generator. Zero is a FIXED POINT of Unity's xorshift128 (s0..s3 all zero -> <c>Next()</c>
    /// returns 0 forever), so from the first batch of the first asset dump onwards every ambient draw in
    /// the user's session was dead: <c>World.GenerateSeed()</c> offered <c>"aaaaaaaaaa"</c>, every time.
    /// Six sites were affected; the nine that passed an argument compiled to a real <c>call .ctor</c> and
    /// were correct, and that asymmetry is what made the bug survive eye-review.
    ///
    /// Static factories on a STRUCT would not have fixed it: <c>new S()</c> compiles for a struct however
    /// its constructors are declared. Only a reference type makes the argumentless form a COMPILE ERROR
    /// (CS1729). So: sealed class, private constructors, <see cref="Capture"/> and <see cref="Seeded"/>.
    /// The allocation is one small object per guard - a few hundred over a whole dump, against a bug that
    /// silently destroyed the user's session.
    ///
    /// <b>A RandomGuard must never span a <c>yield</c></b>, and never writes a state it did not
    /// verifiably capture: the restore goes through <see cref="RandomStateSafe.Restore"/>, which refuses
    /// to write all-zero words. Both rules are enforced mechanically by <c>preflight.ps1</c>.
    /// </summary>
    internal sealed class RandomGuard : IDisposable
    {
        private readonly URandom.State _saved;
        private readonly string _site;
        private bool _restored;

        /// <param name="initState">When given, <c>InitState</c> is called after the save, so the body
        /// starts on a known stream. Never call <c>InitState</c> outside a guard.</param>
        private RandomGuard(string site, int? initState)
        {
            _site = site ?? "RandomGuard";

            // The capture check (item 3): a state that reads as four zeros HERE means the generator was
            // already dead before the dumper touched it. Restoring it later would re-inflict the fixed
            // point, and every draw the guarded body makes is garbage. RandomStateSafe re-seeds and
            // records it; what this guard then captures is the fresh, sane state.
            if (RandomStateSafe.EnsureLiveAtCapture(_site)) { /* re-seeded; fall through and capture it */ }

            _saved = URandom.state;              // struct copy of four ints; the getter draws nothing
            if (initState.HasValue) URandom.InitState(initState.Value);
        }

        /// <summary>Captures the ambient stream and restores it on Dispose.</summary>
        public static RandomGuard Capture(string site = "capture")
        {
            return new RandomGuard(site, null);
        }

        /// <summary>Captures the ambient stream, then <c>InitState(seed)</c> so the body starts on a
        /// known stream. The ambient stream is restored on Dispose.</summary>
        public static RandomGuard Seeded(int seed, string site = "seeded")
        {
            return new RandomGuard(site, seed);
        }

        public void Dispose()
        {
            // A second Dispose would re-write a state the game has since drawn past - a rewind, which is
            // a different flavour of the same harm. Once only.
            if (_restored) return;
            _restored = true;
            RandomStateSafe.Restore(_saved, _site);
        }
    }

    /// <summary>
    /// The plugin's ONLY writer of <c>UnityEngine.Random.state</c>, and the rule it enforces:
    /// <b>never write a state you did not verifiably capture.</b>
    ///
    /// Unity's generator is xorshift128 with shifts 11/8/19, and <c>{0,0,0,0}</c> is a fixed point of it:
    /// once the four words are zero, <c>Next()</c> returns 0 for the rest of the process and every
    /// ambient draw in the user's session is silently dead - new-world seeds (<c>"aaaaaaaaaa"</c>),
    /// effects, piece rotations, plant growth times, rune-stone text. Nothing crashes, nothing looks
    /// obviously wrong, and the game's own per-frame save/restore in <c>EnvMan.UpdateEnvironment</c>
    /// faithfully preserves the zeros forever. So a restore that would write four zeros is ALWAYS a bug,
    /// whatever produced it, and this class refuses to perform it: it logs an ERROR naming the call site
    /// and re-seeds from a non-deterministic source instead.
    ///
    /// <b>The check must never be able to skip the restore.</b> That was the 2026-09-23 lesson in
    /// <see cref="NoDrawCheck"/>: a diagnostic that throws before the assignment is a diagnostic that
    /// displaces the stream it was meant to protect. So the zero test is <c>state.Equals(default)</c> -
    /// <c>Random.State</c> is four blittable <c>int</c>s, so <c>ValueType.Equals</c> takes the bitwise
    /// path, allocates nothing reflective and cannot throw - it runs inside its own try/catch, and a
    /// failure to decide degrades to "write the captured state", never to "write nothing".
    /// </summary>
    internal static class RandomStateSafe
    {
        /// <summary>Set once if anything in this session ever found or refused a zero state. The dump's
        /// notes record it, because a dump taken across a dead generator is suspect.</summary>
        public static bool Poisoned;

        private static int _reseedCounter;

        /// <summary>True when <paramref name="state"/> is four zero words. Cannot throw.</summary>
        public static bool IsZero(URandom.State state)
        {
            try
            {
                return state.Equals(default(URandom.State));
            }
            catch
            {
                return false;   // undecidable -> treat as non-zero, i.e. restore as before
            }
        }

        /// <summary>True when the live generator currently reads as four zero words.</summary>
        public static bool CurrentIsZero()
        {
            return IsZero(URandom.state);
        }

        /// <summary>
        /// The single write path. Restores <paramref name="state"/> unless it is all zeros, in which case
        /// it re-seeds instead and reports the call site.
        /// </summary>
        public static void Restore(URandom.State state, string site)
        {
            bool zero = IsZero(state);
            if (!zero)
            {
                URandom.state = state;
                return;
            }

            // Re-seed FIRST: nothing that can throw may sit between deciding and writing.
            int seed = Reseed();
            Poisoned = true;
            try
            {
                Plugin.Log.LogError(
                    "SeedLab.Dumper: REFUSED to restore an all-zero UnityEngine.Random state at '" +
                    (site ?? "?") + "'. Zero is a fixed point of Unity's xorshift128: writing it would " +
                    "have killed every random draw for the rest of this session (new-world seeds would " +
                    "read \"aaaaaaaaaa\"). The generator has been re-seeded with " + seed + " instead. " +
                    "This is a bug in the dumper - a state was restored that was never captured.");
            }
            catch { }
        }

        /// <summary>
        /// Called at guard-capture time. When the LIVE generator is already zero, something outside this
        /// guard has already killed it: re-seed so the guarded body draws from a real stream and so the
        /// guard captures something worth restoring. Returns true when it had to act.
        /// </summary>
        public static bool EnsureLiveAtCapture(string site)
        {
            if (!CurrentIsZero()) return false;

            int seed = Reseed();
            Poisoned = true;
            try
            {
                Plugin.Log.LogError(
                    "SeedLab.Dumper: UnityEngine.Random read as all zeros when capturing the guard at '" +
                    (site ?? "?") + "'. The generator was ALREADY dead before this guard ran, so every " +
                    "ambient draw in this session has been returning 0 and any dump taken now is " +
                    "suspect. It has been re-seeded with " + seed + ". Restart the game before " +
                    "trusting anything random, and treat this dump as unverified.");
            }
            catch { }
            return true;
        }

        /// <summary>
        /// Puts the generator back onto a real stream from a non-deterministic source. Returns the seed.
        /// <c>InitState</c> is proven never to produce a zero state: the seed itself becomes s0 and the
        /// other three words come from a fixed recurrence, so even <c>InitState(0)</c> gives
        /// <c>[0, 1, 1812433254, 1900727103]</c> (goldens\natives-random.json, D4, 1.0.15-59f53fb5).
        /// </summary>
        private static int Reseed()
        {
            int seed;
            try
            {
                unchecked
                {
                    _reseedCounter++;
                    seed = Environment.TickCount
                         ^ (int)DateTime.UtcNow.Ticks
                         ^ (Guid.NewGuid().GetHashCode() * 31)
                         ^ (_reseedCounter * -1640531535);   // (int)2654435761u, Knuth's 2^32/phi
                }
            }
            catch
            {
                seed = Environment.TickCount;
            }
            URandom.InitState(seed);
            return seed;
        }

        /// <summary>
        /// Item 4: the cheap Info-level breadcrumb. Reading <c>Random.state</c> draws nothing (it is a
        /// plain property getter over native state), so this costs a reflective read of four ints and one
        /// log line. Called at dump start, at every section boundary and at dump end - not per prefab.
        /// </summary>
        public static void Trace(string section)
        {
            try
            {
                int[] w = RandomStateUtil.Current();
                string words = w == null
                    ? "unreadable"
                    : (w.Length >= 4
                        ? (w[0] + "," + w[1] + "," + w[2] + "," + w[3])
                        : string.Join(",", Array.ConvertAll(w, x => x.ToString())));
                Plugin.Log.LogInfo("SeedLab.Dumper: Random.state @ " + (section ?? "?") + " = [" + words + "]" +
                                   (CurrentIsZero() ? "  *** ALL ZERO - the generator is dead ***" : ""));
            }
            catch { }
        }
    }

    /// <summary>
    /// Reads the four words of a <c>UnityEngine.Random.State</c>.
    ///
    /// <c>Random.State</c> declares <c>s0..s3</c> as PRIVATE <c>[SerializeField] int</c>s
    /// (UnityEngine.Random.State, UnityEngine.CoreModule.dll), so there is no public way to see them;
    /// reflection over a boxed copy is the only route. Reading <c>Random.state</c> itself does not
    /// advance the generator - it is a plain property getter over native state.
    ///
    /// This is a DIAGNOSTIC path only. Nothing that decides whether to write the generator may depend on
    /// it: it can throw (a Unity rename, a security policy) and a check that throws must never be able to
    /// skip a restore. <see cref="RandomStateSafe.IsZero"/> uses the reflection-free struct comparison
    /// for that reason.
    /// </summary>
    internal static class RandomStateUtil
    {
        private static FieldInfo[] _words;
        private static bool _resolved;

        /// <summary>Null when the four fields could not be resolved (a Unity update renamed them).
        /// Callers must treat that as "state words unavailable", not as zeros.</summary>
        public static int[] Read(URandom.State state)
        {
            FieldInfo[] words = Resolve();
            if (words == null) return null;
            object boxed = state;               // box once; GetValue needs a reference
            int[] result = new int[words.Length];
            for (int i = 0; i < words.Length; i++)
            {
                result[i] = (int)words[i].GetValue(boxed);
            }
            return result;
        }

        public static int[] Current()
        {
            return Read(URandom.state);
        }

        public static bool Equal(int[] a, int[] b)
        {
            if (a == null || b == null) return false;
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        private static FieldInfo[] Resolve()
        {
            if (_resolved) return _words;
            _resolved = true;
            try
            {
                Type t = typeof(URandom.State);
                var byName = new List<FieldInfo>();
                for (int i = 0; i < 4; i++)
                {
                    FieldInfo f = t.GetField("s" + i, BindingFlags.NonPublic | BindingFlags.Instance);
                    if (f != null && f.FieldType == typeof(int)) byName.Add(f);
                }
                if (byName.Count == 4) { _words = byName.ToArray(); return _words; }

                // Fallback: every int instance field, in metadata (= declaration) order.
                var all = new List<FieldInfo>();
                foreach (FieldInfo f in t.GetFields(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance))
                {
                    if (f.FieldType == typeof(int)) all.Add(f);
                }
                _words = all.Count > 0 ? all.ToArray() : null;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Random.State words are unreadable, traces will omit them: " + e.Message);
                _words = null;
            }
            return _words;
        }
    }
}
