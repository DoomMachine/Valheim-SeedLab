using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;

namespace SeedLab.Runtime.SelfTest
{
    /// <summary>
    /// <c>denormals</c>: a thread whose floating-point control register flushes subnormal results to
    /// zero (FTZ) or reads subnormal inputs as zero (DAZ) computes different floats from the game, and
    /// nothing in a world's output would say so. .NET starts every thread with both off and gives
    /// managed code no way to turn them on, but native code loaded into the process can (a driver hook,
    /// an overlay, a profiler), and the setting is per thread.
    ///
    /// <para><b>The probe</b> is four operations whose correct IEEE result is known exactly: half the
    /// smallest normal float and double (a subnormal result - FTZ gives zero) and twice the smallest
    /// subnormal (a subnormal input - DAZ gives zero). The operands come through non-inlined calls so
    /// the JIT cannot fold them into constants at compile time, which would test the compiler instead
    /// of the thread.</para>
    ///
    /// <para>The suite runs it on the calling thread and on a fresh thread. <see cref="CheckCurrentThread"/>
    /// is public so a worker can run it on itself before its first seed; wiring it into the search
    /// workers is not done yet (their loop lives in the search layer).</para>
    /// </summary>
    public sealed class DenormalProbe : ISelfTestSuite
    {
        public string Name => "denormals";

        public string Describes =>
            "subnormal floats and doubles are produced and read as IEEE-754 requires (no flush-to-zero on this thread)";

        public SelfTestSuiteResult Run(CancellationToken cancel)
        {
            Stopwatch sw = Stopwatch.StartNew();
            int checks = 0, failures = 0;
            string first = "";

            string? here = CheckCurrentThread();
            checks += 4;
            if (here != null)
            {
                failures++;
                first = "the calling thread: " + here;
            }

            string? other = null;
            Thread t = new Thread(() => other = CheckCurrentThread()) { IsBackground = true, Name = "denormal-probe" };
            t.Start();
            t.Join();
            checks += 4;
            if (other != null)
            {
                failures++;
                if (first.Length == 0) first = "a new thread: " + other;
            }

            sw.Stop();
            return new SelfTestSuiteResult(Name, checks, failures, first, sw.Elapsed);
        }

        /// <summary>
        /// Null when this thread computes subnormals correctly; otherwise which operation was wrong, with
        /// the bit patterns. Cheap enough (four operations) for every worker to call before its first seed.
        /// </summary>
        public static string? CheckCurrentThread()
        {
            float minNormalF = FloatFromBits(0x00800000);          // 1.17549435e-38
            float halfF = Multiply(minNormalF, 0.5f);               // 0x00400000, subnormal
            if (BitConverter.SingleToInt32Bits(halfF) != 0x00400000)
            {
                return "float 2^-126 * 0.5 gave 0x" + BitConverter.SingleToInt32Bits(halfF).ToString("X8")
                       + ", expected the subnormal 0x00400000 (flush-to-zero is on)";
            }

            float tinyF = FloatFromBits(0x00000001);                // the smallest subnormal
            float twiceF = Multiply(tinyF, 2f);                     // 0x00000002
            if (BitConverter.SingleToInt32Bits(twiceF) != 0x00000002)
            {
                return "float 2^-149 * 2 gave 0x" + BitConverter.SingleToInt32Bits(twiceF).ToString("X8")
                       + ", expected 0x00000002 (denormals-are-zero is on)";
            }

            double minNormalD = DoubleFromBits(0x0010000000000000L);
            double halfD = Multiply(minNormalD, 0.5);
            if (BitConverter.DoubleToInt64Bits(halfD) != 0x0008000000000000L)
            {
                return "double 2^-1022 * 0.5 gave 0x" + BitConverter.DoubleToInt64Bits(halfD).ToString("X16")
                       + ", expected the subnormal 0x0008000000000000 (flush-to-zero is on)";
            }

            double tinyD = DoubleFromBits(0x0000000000000001L);
            double twiceD = Multiply(tinyD, 2.0);
            if (BitConverter.DoubleToInt64Bits(twiceD) != 0x0000000000000002L)
            {
                return "double 2^-1074 * 2 gave 0x" + BitConverter.DoubleToInt64Bits(twiceD).ToString("X16")
                       + ", expected 0x0000000000000002 (denormals-are-zero is on)";
            }

            return null;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static float FloatFromBits(int bits) => BitConverter.Int32BitsToSingle(bits);

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static double DoubleFromBits(long bits) => BitConverter.Int64BitsToDouble(bits);

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static float Multiply(float a, float b) => a * b;

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static double Multiply(double a, double b) => a * b;
    }
}
