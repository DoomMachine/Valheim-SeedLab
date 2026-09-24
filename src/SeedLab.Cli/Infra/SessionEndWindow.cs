using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace SeedLab.Cli.Infra
{
    /// <summary>
    /// A hidden window that hears Windows end the session - signing out, shutting down, restarting - for
    /// <c>vseed serve</c> (review of 2026-09-25).
    ///
    /// <para><b>Why a window, in a console program.</b> A console program is told about a shutdown or a
    /// sign-out through its console handler (CTRL_SHUTDOWN_EVENT, CTRL_LOGOFF_EVENT) - but not once it has
    /// loaded user32.dll, and a running <c>vseed serve</c> has (measured: USER32, win32u, GDI32 and
    /// gdi32full are among its modules). Microsoft's SetConsoleCtrlHandler documentation says exactly that,
    /// and recommends a hidden top-level window that handles WM_QUERYENDSESSION and WM_ENDSESSION instead.
    /// Without one, a shutdown ended the server abruptly: a running search lost everything since its last
    /// 30-second checkpoint, and the registry file was left behind.</para>
    ///
    /// <para><b>What it does.</b> WM_QUERYENDSESSION is answered "yes, end it" at once: SeedLab never holds up a
    /// shutdown, and nothing is stopped yet, because another program may still cancel it. WM_ENDSESSION with
    /// "the session IS ending" runs the server's one graceful stop - every running search stopped with its
    /// checkpoint saved, the registry file deleted - INSIDE the message, because Windows may end the process
    /// as soon as it returns; the stop is bounded well inside the five seconds Windows allows.</para>
    ///
    /// <para><b>Top-level, not message-only.</b> A message-only window (parent HWND_MESSAGE) is never sent the
    /// broadcast session messages; this one is an ordinary window that is never shown - no taskbar button,
    /// nothing on screen. Its class name is <see cref="ClassName"/>, which a test uses to find it. Windows
    /// only; <see cref="Start"/> returns null elsewhere, where SIGTERM and SIGHUP do this job.</para>
    /// </summary>
    public sealed class SessionEndWindow : IDisposable
    {
        /// <summary>The window class, per process: a test finds the window by it and by the process id.</summary>
        public const string ClassName = "SeedLabWebServerSessionEnd";

        private const uint WM_DESTROY = 0x0002;
        private const uint WM_CLOSE = 0x0010;
        private const uint WM_QUERYENDSESSION = 0x0011;
        private const uint WM_ENDSESSION = 0x0016;
        private const uint WM_APP_QUIT = 0x8000 + 1;
        private const long ENDSESSION_LOGOFF = 0x80000000L;

        private readonly Action<string> _onEnd;
        private readonly WndProc _proc;
        private readonly ManualResetEventSlim _created = new ManualResetEventSlim(false);
        private IntPtr _hwnd;
        private int _ended;

        private SessionEndWindow(Action<string> onEnd)
        {
            _onEnd = onEnd;
            _proc = Proc;   // kept in a field: the window procedure must outlive every message
        }

        /// <summary>
        /// Starts the window on a background thread of its own and returns once it exists, or null when it
        /// could not be made (not Windows, no desktop): then a shutdown ends the server abruptly, as before.
        /// </summary>
        public static SessionEndWindow? Start(Action<string> onEnd)
        {
            if (!OperatingSystem.IsWindows() || onEnd == null) return null;
            try
            {
                SessionEndWindow w = new SessionEndWindow(onEnd);
                Thread t = new Thread(w.Loop) { IsBackground = true, Name = "vseed-session-end" };
                t.Start();
                w._created.Wait(TimeSpan.FromSeconds(5));
                return w._hwnd != IntPtr.Zero ? w : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private void Loop()
        {
            try
            {
                IntPtr instance = GetModuleHandleW(null);
                WNDCLASSEX wc = new WNDCLASSEX
                {
                    cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
                    lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
                    hInstance = instance,
                    lpszClassName = ClassName,
                };

                // 1410 = the class is registered already (a second console in this process): use it.
                if (RegisterClassExW(ref wc) == 0 && Marshal.GetLastWin32Error() != 1410)
                {
                    return;
                }

                // exStyle 0 and no WS_VISIBLE: a top-level window that is never shown.
                _hwnd = CreateWindowExW(0, ClassName, "SeedLab web server", 0, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, instance,
                                        IntPtr.Zero);
            }
            catch (Exception)
            {
                _hwnd = IntPtr.Zero;
            }
            finally
            {
                _created.Set();
            }

            if (_hwnd == IntPtr.Zero) return;
            while (GetMessageW(out MSG msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessageW(ref msg);
            }
        }

        private IntPtr Proc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            switch (msg)
            {
                case WM_QUERYENDSESSION:
                    // Never hold up a shutdown; nothing is stopped yet - another program may still cancel it.
                    return new IntPtr(1);

                case WM_ENDSESSION:
                    if (wParam != IntPtr.Zero && Interlocked.Exchange(ref _ended, 1) == 0)
                    {
                        string reason = (lParam.ToInt64() & ENDSESSION_LOGOFF) != 0
                            ? "Windows is signing you out"
                            : "Windows is shutting down or restarting";
                        try
                        {
                            _onEnd(reason);
                        }
                        catch (Exception)
                        {
                            // The session is ending either way.
                        }
                    }

                    return IntPtr.Zero;

                case WM_CLOSE:
                    // Only Dispose closes it (WM_APP_QUIT): a stray WM_CLOSE would leave the server deaf to a
                    // shutdown for the rest of its life.
                    return IntPtr.Zero;

                case WM_APP_QUIT:
                    DestroyWindow(hwnd);
                    return IntPtr.Zero;

                case WM_DESTROY:
                    PostQuitMessage(0);
                    return IntPtr.Zero;
            }

            return DefWindowProcW(hwnd, msg, wParam, lParam);
        }

        public void Dispose()
        {
            IntPtr h = _hwnd;
            if (h == IntPtr.Zero) return;
            _hwnd = IntPtr.Zero;
            try
            {
                PostMessageW(h, WM_APP_QUIT, IntPtr.Zero, IntPtr.Zero);
            }
            catch (Exception)
            {
            }
        }

        // ---- user32 -------------------------------------------------------------------------------------

        private delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WNDCLASSEX
        {
            public uint cbSize;
            public uint style;
            public IntPtr lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            public string? lpszMenuName;
            public string lpszClassName;
            public IntPtr hIconSm;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public int ptX;
            public int ptY;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern ushort RegisterClassExW(ref WNDCLASSEX wc);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateWindowExW(uint exStyle, string className, string windowName, uint style, int x, int y,
                                                     int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

        [DllImport("user32.dll")]
        private static extern IntPtr DefWindowProcW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern int GetMessageW(out MSG msg, IntPtr hwnd, uint min, uint max);

        [DllImport("user32.dll")]
        private static extern bool TranslateMessage(ref MSG msg);

        [DllImport("user32.dll")]
        private static extern IntPtr DispatchMessageW(ref MSG msg);

        [DllImport("user32.dll")]
        private static extern bool DestroyWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern void PostQuitMessage(int code);

        [DllImport("user32.dll")]
        private static extern bool PostMessageW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandleW(string? name);
    }
}
