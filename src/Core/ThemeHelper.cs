using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace MaicongTray
{
    internal class ThemePalette
    {
        public bool IsDark;
        public Color Surface;
        public Color SurfaceGlass;
        public Color CardBg;
        public Color CardGlass;
        public Color CardHover;
        public Color Border;
        public Color SubtleBorder;
        public Color TextPrimary;
        public Color TextSecondary;
        public Color TextMuted;
        public Color Accent;
        public Color AccentSoft;
        public Color Green;
        public Color GreenSoft;
        public Color Amber;
        public Color AmberSoft;
        public Color Red;
        public Color RedSoft;
        public Color TrackBg;
        public Color GridLine;
        public Color PillBg;
        public Color PillActiveBg;
    }

    internal static class ThemeHelper
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct MARGINS
        {
            public int cxLeftWidth;
            public int cxRightWidth;
            public int cyTopHeight;
            public int cyBottomHeight;
        }

        [DllImport("user32.dll")]
        private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        [DllImport("dwmapi.dll")]
        private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS margins);

        [DllImport("user32.dll")]
        internal static extern uint GetDpiForWindow(IntPtr hwnd);

        internal static void EnablePerMonitorDpiAwareness()
        {
            try
            {
                if (SetProcessDpiAwarenessContext(new IntPtr(-4))) return; // PER_MONITOR_AWARE_V2
            }
            catch { }
            try { SetProcessDPIAware(); }
            catch { }
        }

        internal static float GetScaleFactor(IntPtr hwnd)
        {
            try
            {
                uint dpi = GetDpiForWindow(hwnd);
                if (dpi >= 96) return dpi / 96f;
            }
            catch { }
            try
            {
                using (Graphics g = Graphics.FromHwnd(IntPtr.Zero))
                {
                    if (g.DpiX >= 96f) return g.DpiX / 96f;
                }
            }
            catch { }
            return 1f;
        }

        internal static bool IsDarkAppTheme()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    if (k == null) return false;
                    return Convert.ToInt32(k.GetValue("AppsUseLightTheme", 1)) == 0;
                }
            }
            catch { return false; }
        }

        internal static bool IsDarkTaskbar()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    if (k == null) return false;
                    object v = k.GetValue("SystemUsesLightTheme", null);
                    if (v == null) v = k.GetValue("AppsUseLightTheme", 1);
                    return Convert.ToInt32(v) == 0;
                }
            }
            catch { return false; }
        }

        // Read Windows 11 system accent color from HKCU\Software\Microsoft\Windows\DWM\AccentColor (0xAABBGGRR)
        // and adapt luminance gently so it always reads crisply in both Dark and Light themes.
        internal static Color GetSystemAccentColor(bool dark)
        {
            Color fallback = dark ? Color.FromArgb(96, 165, 250) : Color.FromArgb(37, 99, 235);
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM"))
                {
                    if (k == null) return fallback;
                    object raw = k.GetValue("AccentColor", null);
                    if (raw == null) return fallback;
                    uint abgr = unchecked((uint)Convert.ToInt32(raw));
                    int r = (int)(abgr & 0xFF);
                    int g = (int)((abgr >> 8) & 0xFF);
                    int b = (int)((abgr >> 16) & 0xFF);
                    if (r == 0 && g == 0 && b == 0) return fallback;
                    return AdjustAccentForTheme(Color.FromArgb(r, g, b), dark);
                }
            }
            catch { return fallback; }
        }

        private static Color AdjustAccentForTheme(Color c, bool dark)
        {
            float r = c.R / 255f;
            float g = c.G / 255f;
            float b = c.B / 255f;
            float lum = 0.2126f * r + 0.7152f * g + 0.0722f * b;

            if (dark && lum < 0.42f)
            {
                // Lift dark accent colors toward soft luminous highlight on dark surface
                float lift = Math.Min(0.45f, (0.46f - lum) * 1.15f);
                return Color.FromArgb(
                    ClampByte(c.R + (255 - c.R) * lift),
                    ClampByte(c.G + (255 - c.G) * lift),
                    ClampByte(c.B + (255 - c.B) * lift));
            }
            if (!dark && lum > 0.58f)
            {
                // Deepen overly bright accents so text/toggles remain legible on light surface
                float factor = 0.72f;
                return Color.FromArgb(
                    ClampByte(c.R * factor),
                    ClampByte(c.G * factor),
                    ClampByte(c.B * factor));
            }
            return c;
        }

        private static int ClampByte(float v)
        {
            int n = (int)Math.Round(v);
            if (n < 0) return 0;
            if (n > 255) return 255;
            return n;
        }

        // Applies Win11 rounded corners, immersive dark mode, hairline border, and Acrylic backdrop.
        // Returns true if hardware Win11 Acrylic backdrop was successfully enabled.
        internal static bool ApplyWin11AcrylicWindow(IntPtr hwnd, bool dark)
        {
            if (hwnd == IntPtr.Zero) return false;
            try
            {
                int darkVal = dark ? 1 : 0;
                DwmSetWindowAttribute(hwnd, 20, ref darkVal, 4); // DWMWA_USE_IMMERSIVE_DARK_MODE
            }
            catch { }
            try
            {
                int pref = 2; // DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWCP_ROUND = 2
                DwmSetWindowAttribute(hwnd, 33, ref pref, 4);
            }
            catch { }
            try
            {
                // DWMWA_BORDER_COLOR = 34 (COLORREF 0x00BBGGRR)
                int borderColor = dark ? 0x0046423E : 0x00D8D4CE;
                DwmSetWindowAttribute(hwnd, 34, ref borderColor, 4);
            }
            catch { }

            // Enable Win11 22H2+ TransientWindow (Acrylic) backdrop
            try
            {
                var margins = new MARGINS { cxLeftWidth = -1, cxRightWidth = -1, cyTopHeight = -1, cyBottomHeight = -1 };
                DwmExtendFrameIntoClientArea(hwnd, ref margins);

                int backdrop = 3; // DWMSBT_TRANSIENTWINDOW (Acrylic)
                int hr = DwmSetWindowAttribute(hwnd, 38, ref backdrop, 4);
                return hr == 0;
            }
            catch { return false; }
        }

        internal static ThemePalette GetPalette(bool dark)
        {
            Color accent = GetSystemAccentColor(dark);

            if (dark)
            {
                return new ThemePalette
                {
                    IsDark = true,
                    Surface = Color.FromArgb(24, 25, 29),
                    SurfaceGlass = Color.FromArgb(148, 20, 22, 27),
                    CardBg = Color.FromArgb(33, 35, 41),
                    CardGlass = Color.FromArgb(165, 36, 39, 47),
                    CardHover = Color.FromArgb(195, 50, 54, 65),
                    Border = Color.FromArgb(56, 59, 68),
                    SubtleBorder = Color.FromArgb(38, 255, 255, 255),
                    TextPrimary = Color.FromArgb(245, 246, 248),
                    TextSecondary = Color.FromArgb(172, 178, 190),
                    TextMuted = Color.FromArgb(124, 130, 144),
                    Accent = accent,
                    AccentSoft = Color.FromArgb(40, accent),
                    Green = Color.FromArgb(52, 211, 153),
                    GreenSoft = Color.FromArgb(38, 52, 211, 153),
                    Amber = Color.FromArgb(251, 191, 36),
                    AmberSoft = Color.FromArgb(40, 251, 191, 36),
                    Red = Color.FromArgb(248, 113, 113),
                    RedSoft = Color.FromArgb(40, 248, 113, 113),
                    TrackBg = Color.FromArgb(72, 255, 255, 255),
                    GridLine = Color.FromArgb(38, 255, 255, 255),
                    PillBg = Color.FromArgb(42, 255, 255, 255),
                    PillActiveBg = Color.FromArgb(68, 255, 255, 255)
                };
            }
            return new ThemePalette
            {
                IsDark = false,
                Surface = Color.FromArgb(248, 249, 251),
                SurfaceGlass = Color.FromArgb(176, 244, 246, 251),
                CardBg = Color.FromArgb(255, 255, 255),
                CardGlass = Color.FromArgb(204, 255, 255, 255),
                CardHover = Color.FromArgb(220, 236, 240, 248),
                Border = Color.FromArgb(224, 228, 235),
                SubtleBorder = Color.FromArgb(26, 0, 0, 0),
                TextPrimary = Color.FromArgb(22, 24, 29),
                TextSecondary = Color.FromArgb(90, 98, 112),
                TextMuted = Color.FromArgb(138, 145, 158),
                Accent = accent,
                AccentSoft = Color.FromArgb(28, accent),
                Green = Color.FromArgb(16, 163, 107),
                GreenSoft = Color.FromArgb(28, 16, 163, 107),
                Amber = Color.FromArgb(217, 119, 6),
                AmberSoft = Color.FromArgb(30, 217, 119, 6),
                Red = Color.FromArgb(220, 38, 38),
                RedSoft = Color.FromArgb(28, 220, 38, 38),
                TrackBg = Color.FromArgb(38, 0, 0, 0),
                GridLine = Color.FromArgb(24, 0, 0, 0),
                PillBg = Color.FromArgb(24, 0, 0, 0),
                PillActiveBg = Color.FromArgb(42, 0, 0, 0)
            };
        }

        internal static Color GetBatteryColor(int percent, bool charging, ThemePalette pal)
        {
            if (charging) return pal.Green;
            if (percent <= 10) return pal.Red;
            if (percent <= 25) return pal.Amber;
            return pal.Green;
        }

        // Always create fonts in GraphicsUnit.Pixel (pt * 96 / 72) so they scale strictly via
        // our logical ScaleTransform(scale, scale) and NEVER suffer double-DPI scaling on 125%/150%/200% screens.
        internal static Font CreateUiFont(float ptSize, FontStyle style)
        {
            float pxSize = ptSize * 96f / 72f;
            string[] candidates = new string[] { "Segoe UI Variable Display", "Microsoft YaHei UI", "Segoe UI" };
            foreach (string name in candidates)
            {
                try
                {
                    using (var ff = new FontFamily(name))
                    {
                        if (ff.IsStyleAvailable(style))
                            return new Font(name, pxSize, style, GraphicsUnit.Pixel);
                    }
                }
                catch { }
            }
            return new Font(SystemFonts.MessageBoxFont.FontFamily, pxSize, style, GraphicsUnit.Pixel);
        }

        internal static GraphicsPath RoundedRect(RectangleF rect, float radius)
        {
            var path = new GraphicsPath();
            float r = Math.Min(radius, Math.Min(rect.Width, rect.Height) / 2f);
            if (r <= 0.5f)
            {
                path.AddRectangle(rect);
                return path;
            }
            float d = r * 2f;
            path.AddArc(rect.X, rect.Y, d, d, 180, 90);
            path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
            path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}
