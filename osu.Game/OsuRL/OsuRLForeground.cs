// osu_rl (local research fork, not for upstream): bring this game's own window to the front on Windows.

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace osu.Game.OsuRL
{
    public static class OsuRLForeground
    {
        private const int sw_restore = 9;

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr processId);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool BringWindowToTop(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

        /// <summary>Puts this process's main window in front. -> whether it is in front afterwards.</summary>
        public static bool Bring()
        {
            if (!OperatingSystem.IsWindows())
                return false;

            IntPtr hwnd = Process.GetCurrentProcess().MainWindowHandle;
            if (hwnd == IntPtr.Zero)
                return false;

            IntPtr fg = GetForegroundWindow();
            if (fg == hwnd)
                return true;

            uint me = GetCurrentThreadId();
            uint them = fg == IntPtr.Zero ? 0 : GetWindowThreadProcessId(fg, IntPtr.Zero);
            uint owner = GetWindowThreadProcessId(hwnd, IntPtr.Zero);
            bool joinedThem = them != 0 && them != me && AttachThreadInput(me, them, true);
            bool joinedOwner = owner != me && AttachThreadInput(me, owner, true);

            try
            {
                if (IsIconic(hwnd))
                    ShowWindow(hwnd, sw_restore);
                BringWindowToTop(hwnd);
                SetForegroundWindow(hwnd);
            }
            finally
            {
                if (joinedOwner)
                    AttachThreadInput(me, owner, false);
                if (joinedThem)
                    AttachThreadInput(me, them, false);
            }

            return GetForegroundWindow() == hwnd;
        }
    }
}
