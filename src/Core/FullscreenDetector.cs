using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace MaicongTray
{
    // Detects exclusive D3D/Vulkan fullscreen games AND borderless fullscreen games/videos
    // so battery notifications never interrupt active gameplay.
    internal static class FullscreenDetector
    {
        private enum QUERY_USER_NOTIFICATION_STATE
        {
            QUNS_NOT_PRESENT = 1,
            QUNS_BUSY = 2,
            QUNS_RUNNING_D3D_FULL_SCREEN = 3,
            QUNS_PRESENTATION_MODE = 4,
            QUNS_ACCEPTS_NOTIFICATIONS = 5,
            QUNS_QUIET_TIME = 6,
            QUNS_APP = 7
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left, Top, Right, Bottom;
        }

        [DllImport("shell32.dll")]
        private static extern int SHQueryUserNotificationState(out QUERY_USER_NOTIFICATION_STATE state);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern int GetClassName(IntPtr hwnd, StringBuilder sb, int maxCount);

        [DllImport("user32.dll")]
        private static extern IntPtr GetDesktopWindow();

        [DllImport("user32.dll")]
        private static extern IntPtr GetShellWindow();

        internal static bool IsFullscreenActive()
        {
            // 1. Official Windows notification state API (catches exclusive D3D/Vulkan/presentations)
            try
            {
                QUERY_USER_NOTIFICATION_STATE state;
                if (SHQueryUserNotificationState(out state) == 0)
                {
                    if (state == QUERY_USER_NOTIFICATION_STATE.QUNS_BUSY
                        || state == QUERY_USER_NOTIFICATION_STATE.QUNS_RUNNING_D3D_FULL_SCREEN
                        || state == QUERY_USER_NOTIFICATION_STATE.QUNS_PRESENTATION_MODE)
                    {
                        return true;
                    }
                }
            }
            catch { }

            // 2. Borderless fullscreen foreground window check
            try
            {
                IntPtr fg = GetForegroundWindow();
                if (fg == IntPtr.Zero || fg == GetDesktopWindow() || fg == GetShellWindow())
                    return false;

                var cls = new StringBuilder(64);
                GetClassName(fg, cls, cls.Capacity);
                string clsName = cls.ToString();
                if (clsName == "Progman" || clsName == "WorkerW" || clsName == "Shell_TrayWnd" || clsName == "Shell_SecondaryTrayWnd")
                    return false;

                RECT r;
                if (!GetWindowRect(fg, out r)) return false;

                int w = r.Right - r.Left;
                int h = r.Bottom - r.Top;
                if (w <= 0 || h <= 0) return false;

                Screen scr = Screen.FromHandle(fg);
                Rectangle b = scr.Bounds;
                // Covers the entire monitor bounds (including taskbar area) = borderless fullscreen
                return r.Left <= b.Left + 2
                    && r.Top <= b.Top + 2
                    && r.Right >= b.Right - 2
                    && r.Bottom >= b.Bottom - 2;
            }
            catch { return false; }
        }
    }
}
