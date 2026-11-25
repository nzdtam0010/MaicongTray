using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace MaicongTray
{
    // Modern Windows 11 Acrylic floating dashboard card (unified UI for both left & right click).
    // Features:
    //  - Hardware Win11 Acrylic glass backdrop (32bpp premultiplied DIB BitBlt, safe opaque fallback on Win10)
    //  - System accent color adaptation, Per-Monitor DPI scaling, native DWM rounded corners
    //  - Precision tray icon anchoring + ease-out cubic slide-up & fade-in animation
    //  - Hero battery display with sleep-state preservation, charging shimmer & ETA/drain rate
    //  - Interactive 24h / 7-day battery trend sparkline with hover inspection & last-full-charge stat
    //  - Inline Quick-Settings view with Fullscreen Game DND & firmware version readout
    internal class BatteryFlyoutForm : Form
    {
        private const int BaseW = 358;
        private const int BaseH = 442;

        private BatteryResult _result;
        private BatteryHistory.Sample[] _allSamples = new BatteryHistory.Sample[0];
        private AppSettings _settings;
        private string _deviceName = "迈从 A7 V2 Ultra";
        private string _firmware = "";
        private string _etaText;
        private string _lastFullText;
        private double _pctPerHour;
        private bool _hasEta;
        private bool _autoStart;
        private bool _settingsView;
        private bool _refreshing;
        private bool _acrylicActive;
        private float _scale = 1f;
        private ThemePalette _pal;
        private string _toastText;
        private DateTime _toastUntil = DateTime.MinValue;

        // Interactive hitboxes (logical coordinates)
        private readonly List<HitTarget> _hits = new List<HitTarget>();
        private string _hoverId;
        private int _hoverSampleIndex = -1;
        private RectangleF _chartPlotRect;
        private readonly List<PointF> _chartPoints = new List<PointF>();
        private readonly List<BatteryHistory.Sample> _chartSamples = new List<BatteryHistory.Sample>();

        // Entrance slide-up + fade-in animation
        private readonly System.Windows.Forms.Timer _enterTimer;
        private readonly System.Windows.Forms.Timer _animTimer;
        private int _enterStep;
        private const int EnterTotalSteps = 10;
        private int _targetX;
        private int _startY;
        private int _targetY;
        private float _shimmerPhase;
        private DateTime _lastDeactivated = DateTime.MinValue;

        // Callbacks to TrayApp
        internal Action OnRefreshClick;
        internal Action<TrayIconStyle> OnIconStyleChanged;
        internal Action<int> OnChartRangeChanged;
        internal Action<int> OnIntervalChanged;
        internal Action<bool> OnLowNotifyChanged;
        internal Action<int> OnLowThresholdChanged;
        internal Action<bool> OnFullNotifyChanged;
        internal Action<bool> OnFullscreenDndChanged;
        internal Action<bool> OnAutoStartChanged;
        internal Action OnCopyDebugClick;
        internal Action OnOpenDataDirClick;
        internal Action OnCheckUpdateClick;
        internal Action OnInstallStableClick;
        internal Action OnExitClick;

        private class HitTarget
        {
            public string Id;
            public RectangleF Rect;
            public Action Click;
        }

        internal BatteryFlyoutForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            KeyPreview = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);

            _pal = ThemeHelper.GetPalette(ThemeHelper.IsDarkAppTheme());
            BackColor = Color.Black; // Required base for DWM extended glass surface

            _enterTimer = new System.Windows.Forms.Timer();
            _enterTimer.Interval = 15;
            _enterTimer.Tick += delegate
            {
                _enterStep++;
                double t = Math.Min(1.0, (double)_enterStep / EnterTotalSteps);
                double ease = 1.0 - Math.Pow(1.0 - t, 3.0); // Ease-out cubic
                int curY = (int)Math.Round(_startY + (_targetY - _startY) * ease);
                if (Top != curY) Top = curY;
                Opacity = Math.Min(1.0, ease);
                if (_enterStep >= EnterTotalSteps)
                {
                    Top = _targetY;
                    Opacity = 1.0;
                    _enterTimer.Stop();
                }
            };

            _animTimer = new System.Windows.Forms.Timer();
            _animTimer.Interval = 50;
            _animTimer.Tick += delegate
            {
                bool needInvalidate = false;
                if (_result != null && _result.State == BatteryState.Battery && _result.Charging && !_result.IsSleeping)
                {
                    _shimmerPhase += 0.045f;
                    if (_shimmerPhase > 1.4f) _shimmerPhase = -0.4f;
                    needInvalidate = true;
                }
                if (_toastText != null && DateTime.Now > _toastUntil)
                {
                    _toastText = null;
                    needInvalidate = true;
                }
                if (needInvalidate && Visible) Invalidate();
            };

            Deactivate += delegate
            {
                _lastDeactivated = DateTime.Now;
                HideFlyout();
            };
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ClassStyle |= 0x00020000; // CS_DROPSHADOW fallback for Win10
                cp.ExStyle |= 0x00000080;    // WS_EX_TOOLWINDOW (keeps out of Alt+Tab)
                return cp;
            }
        }

        internal bool WasJustDeactivated()
        {
            return (DateTime.Now - _lastDeactivated).TotalMilliseconds < 240.0;
        }

        internal void ShowToast(string text)
        {
            _toastText = text;
            _toastUntil = DateTime.Now.AddSeconds(2.2);
            if (Visible) Invalidate();
        }

        internal void UpdateData(
            BatteryResult result,
            BatteryHistory.Sample[] allSamples,
            AppSettings settings,
            string deviceName,
            string firmware,
            bool hasEta,
            string etaText,
            double pctPerHour,
            string lastFullText,
            bool autoStart,
            bool refreshing)
        {
            _result = result;
            _allSamples = allSamples ?? new BatteryHistory.Sample[0];
            _settings = settings ?? new AppSettings();
            if (!string.IsNullOrEmpty(deviceName)) _deviceName = deviceName;
            if (!string.IsNullOrEmpty(firmware)) _firmware = firmware;
            else if (result != null && !string.IsNullOrEmpty(result.Firmware)) _firmware = result.Firmware;
            _hasEta = hasEta;
            _etaText = etaText;
            _pctPerHour = pctPerHour;
            _lastFullText = lastFullText;
            _autoStart = autoStart;
            _refreshing = refreshing;

            if (Visible) Invalidate();
        }

        internal void ShowAtTray(Rectangle? trayIconRect)
        {
            bool dark = ThemeHelper.IsDarkAppTheme();
            _pal = ThemeHelper.GetPalette(dark);
            _settingsView = false;
            _hoverId = null;
            _hoverSampleIndex = -1;

            Point anchorPt = trayIconRect.HasValue
                ? new Point((trayIconRect.Value.Left + trayIconRect.Value.Right) / 2,
                            (trayIconRect.Value.Top + trayIconRect.Value.Bottom) / 2)
                : Cursor.Position;

            Screen scr = Screen.FromPoint(anchorPt);
            Rectangle wa = scr.WorkingArea;

            if (!IsHandleCreated) CreateHandle();
            _scale = ThemeHelper.GetScaleFactor(Handle);
            int w = (int)Math.Round(BaseW * _scale);
            int h = (int)Math.Round(BaseH * _scale);
            Size = new Size(w, h);

            int margin = (int)Math.Round(10 * _scale);
            int slideOffset = (int)Math.Round(9 * _scale);

            _targetX = anchorPt.X - w / 2;
            bool topTaskbar = (wa.Top > scr.Bounds.Top);

            if (topTaskbar)
            {
                _targetY = wa.Top + margin;
                _startY = _targetY - slideOffset;
            }
            else
            {
                _targetY = wa.Bottom - h - margin;
                _startY = _targetY + slideOffset;
            }

            if (_targetX + w > wa.Right - margin) _targetX = wa.Right - w - margin;
            if (_targetX < wa.Left + margin) _targetX = wa.Left + margin;
            if (_targetY < wa.Top + margin) _targetY = wa.Top + margin;

            Location = new Point(_targetX, _startY);
            _acrylicActive = ThemeHelper.ApplyWin11AcrylicWindow(Handle, dark);
            BackColor = _acrylicActive ? Color.Black : _pal.Surface;

            _enterStep = 0;
            Opacity = 0.0;
            Show();
            Activate();
            _enterTimer.Start();
            _animTimer.Start();
            Invalidate();
        }

        internal void HideFlyout()
        {
            _enterTimer.Stop();
            _animTimer.Stop();
            if (Visible) Hide();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape)
            {
                if (_settingsView)
                {
                    _settingsView = false;
                    Invalidate();
                }
                else
                {
                    HideFlyout();
                }
                e.Handled = true;
            }
            base.OnKeyDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            PointF lp = new PointF(e.X / _scale, e.Y / _scale);
            string nextHover = null;
            for (int i = 0; i < _hits.Count; i++)
            {
                if (_hits[i].Rect.Contains(lp))
                {
                    nextHover = _hits[i].Id;
                    break;
                }
            }

            int nextSample = -1;
            if (!_settingsView && _chartPoints.Count >= 2 && _chartPlotRect.Contains(lp))
            {
                float bestDist = float.MaxValue;
                for (int i = 0; i < _chartPoints.Count; i++)
                {
                    float d = Math.Abs(_chartPoints[i].X - lp.X);
                    if (d < bestDist)
                    {
                        bestDist = d;
                        nextSample = i;
                    }
                }
            }

            Cursor = (nextHover != null) ? Cursors.Hand : Cursors.Default;

            if (nextHover != _hoverId || nextSample != _hoverSampleIndex)
            {
                _hoverId = nextHover;
                _hoverSampleIndex = nextSample;
                Invalidate();
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            if (_hoverId != null || _hoverSampleIndex != -1)
            {
                _hoverId = null;
                _hoverSampleIndex = -1;
                Cursor = Cursors.Default;
                Invalidate();
            }
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            PointF lp = new PointF(e.X / _scale, e.Y / _scale);
            for (int i = 0; i < _hits.Count; i++)
            {
                if (_hits[i].Rect.Contains(lp))
                {
                    Action act = _hits[i].Click;
                    if (act != null) act();
                    Invalidate();
                    return;
                }
            }
            base.OnMouseDown(e);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            // Fully painted via 32bpp DIB BitBlt in OnPaint to avoid flicker & preserve DWM alpha.
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            int w = ClientSize.Width;
            int h = ClientSize.Height;
            if (w <= 0 || h <= 0) return;

            using (var bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    RenderContent(g, _scale, _pal, _acrylicActive, _hits);
                }

                if (_acrylicActive)
                {
                    BlitPremultiplied32(e.Graphics, bmp);
                }
                else
                {
                    e.Graphics.DrawImageUnscaled(bmp, 0, 0);
                }
            }
        }

        // Exact 32bpp premultiplied DIB BitBlt onto DWM-extended glass window DC.
        // Preserves every alpha pixel cleanly with zero ClearType haloing and zero GDI leaks.
        private static void BlitPremultiplied32(Graphics destGraphics, Bitmap bmp)
        {
            int w = bmp.Width;
            int h = bmp.Height;
            IntPtr hdc = destGraphics.GetHdc();
            IntPtr memDc = IntPtr.Zero;
            IntPtr hDib = IntPtr.Zero;
            IntPtr oldObj = IntPtr.Zero;
            try
            {
                memDc = CreateCompatibleDC(hdc);
                var bmi = new BITMAPINFOHEADER();
                bmi.biSize = Marshal.SizeOf(typeof(BITMAPINFOHEADER));
                bmi.biWidth = w;
                bmi.biHeight = -h; // top-down DIB
                bmi.biPlanes = 1;
                bmi.biBitCount = 32;
                bmi.biCompression = 0; // BI_RGB

                IntPtr bits;
                hDib = CreateDIBSection(memDc, ref bmi, 0, out bits, IntPtr.Zero, 0);
                if (hDib == IntPtr.Zero || bits == IntPtr.Zero) return;

                BitmapData data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
                try
                {
                    int expectedBytes = w * 4 * h;
                    int byteCount = Math.Abs(data.Stride) * h;
                    if (byteCount == expectedBytes)
                    {
                        byte[] raw = new byte[byteCount];
                        Marshal.Copy(data.Scan0, raw, 0, byteCount);
                        Marshal.Copy(raw, 0, bits, byteCount);
                    }
                }
                finally
                {
                    bmp.UnlockBits(data);
                }

                oldObj = SelectObject(memDc, hDib);
                BitBlt(hdc, 0, 0, w, h, memDc, 0, 0, 0x00CC0020); // SRCCOPY
            }
            finally
            {
                if (oldObj != IntPtr.Zero && memDc != IntPtr.Zero) SelectObject(memDc, oldObj);
                if (hDib != IntPtr.Zero) DeleteObject(hDib);
                if (memDc != IntPtr.Zero) DeleteDC(memDc);
                destGraphics.ReleaseHdc(hdc);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFOHEADER
        {
            public int biSize;
            public int biWidth;
            public int biHeight;
            public short biPlanes;
            public short biBitCount;
            public int biCompression;
            public int biSizeImage;
            public int biXPelsPerMeter;
            public int biYPelsPerMeter;
            public int biClrUsed;
            public int biClrImportant;
        }

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr obj);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER pbmi, uint usage, out IntPtr bits, IntPtr hSection, uint offset);

        [DllImport("gdi32.dll")]
        private static extern bool BitBlt(IntPtr hdcDest, int xDest, int yDest, int w, int h, IntPtr hdcSrc, int xSrc, int ySrc, int rop);

        // Static helper used by MaicongCheck --flyout-preview to export crisp PNG previews.
        internal static Bitmap RenderPreviewBitmap(
            BatteryResult result,
            BatteryHistory.Sample[] samples,
            AppSettings settings,
            bool dark,
            bool settingsView,
            float scale)
        {
            int w = (int)Math.Round(BaseW * scale);
            int h = (int)Math.Round(BaseH * scale);
            var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (var form = new BatteryFlyoutForm())
            {
                form._pal = ThemeHelper.GetPalette(dark);
                form._scale = scale;
                form._settingsView = settingsView;
                form._result = result;
                form._allSamples = samples ?? new BatteryHistory.Sample[0];
                form._settings = settings ?? new AppSettings();
                form._firmware = (result != null && !string.IsNullOrEmpty(result.Firmware)) ? result.Firmware : "052E.0204";
                form._autoStart = true;
                form._hasEta = result != null && !result.IsSleeping;
                form._etaText = result != null && result.Charging ? "约 42 分钟" : "约 21.5 小时";
                form._pctPerHour = result != null && result.Charging ? 48.0 : 2.8;
                form._lastFullText = "2 天 4 小时前";
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    form.RenderContent(g, scale, form._pal, false, new List<HitTarget>());
                }
            }
            return bmp;
        }

        private void RenderContent(Graphics g, float scale, ThemePalette pal, bool glassMode, List<HitTarget> hits)
        {
            hits.Clear();
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = glassMode ? TextRenderingHint.AntiAliasGridFit : TextRenderingHint.ClearTypeGridFit;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);
            g.ScaleTransform(scale, scale);

            // Base surface (translucent over Acrylic backdrop, or solid fallback)
            using (var bg = new SolidBrush(glassMode ? pal.SurfaceGlass : pal.Surface))
            {
                g.FillRectangle(bg, 0, 0, BaseW, BaseH);
            }

            // Subtle top ambient accent glow
            Color glowCol;
            if (_result != null && _result.State == BatteryState.Battery && !_result.IsSleeping)
                glowCol = ThemeHelper.GetBatteryColor(_result.Percent, _result.Charging, pal);
            else if (_result != null && _result.IsSleeping)
                glowCol = pal.Amber;
            else
                glowCol = pal.Accent;

            using (var gp = new GraphicsPath())
            {
                gp.AddEllipse(-40f, -90f, BaseW + 80f, 180f);
                using (var pgb = new PathGradientBrush(gp))
                {
                    pgb.CenterColor = Color.FromArgb(pal.IsDark ? 28 : 16, glowCol);
                    pgb.SurroundColors = new Color[] { Color.FromArgb(0, glowCol) };
                    g.FillPath(pgb, gp);
                }
            }

            if (!glassMode)
            {
                using (var borderPen = new Pen(pal.Border, 1f))
                    g.DrawRectangle(borderPen, 0.5f, 0.5f, BaseW - 1f, BaseH - 1f);
            }

            if (_settingsView)
            {
                DrawSettingsView(g, pal, glassMode, hits);
            }
            else
            {
                DrawDashboardView(g, pal, glassMode, hits);
            }

            // Transient toast banner
            if (!string.IsNullOrEmpty(_toastText))
            {
                RectangleF tr = new RectangleF(BaseW / 2f - 88f, BaseH - 42f, 176f, 28f);
                using (GraphicsPath tp = ThemeHelper.RoundedRect(tr, 14f))
                using (var tb = new SolidBrush(pal.IsDark ? Color.FromArgb(238, 42, 47, 58) : Color.FromArgb(238, 22, 26, 34)))
                using (var font = ThemeHelper.CreateUiFont(8.4f, FontStyle.Regular))
                using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                using (var tf = new SolidBrush(Color.White))
                {
                    g.FillPath(tb, tp);
                    g.DrawString(_toastText, font, tf, tr, sf);
                }
            }
        }

        private void DrawDashboardView(Graphics g, ThemePalette pal, bool glassMode, List<HitTarget> hits)
        {
            // ---- 1. Header ----
            DrawHeader(g, pal, hits);

            // ---- 2. Hero Battery Card ----
            RectangleF heroRect = new RectangleF(16f, 66f, BaseW - 32f, 114f);
            DrawCard(g, heroRect, pal, glassMode);
            DrawHeroBattery(g, heroRect, pal);

            // ---- 3. 24h / 7-Day Trend Chart Card ----
            RectangleF chartCard = new RectangleF(16f, 190f, BaseW - 32f, 192f);
            DrawCard(g, chartCard, pal, glassMode);
            DrawSparklineCard(g, chartCard, pal, hits);

            // ---- 4. Footer Style Switcher & Exit ----
            DrawFooterBar(g, pal, hits);
        }

        private void DrawHeader(Graphics g, ThemePalette pal, List<HitTarget> hits)
        {
            RectangleF emblemRect = new RectangleF(16f, 16f, 34f, 34f);
            using (Bitmap iconBmp = IconRenderer.RenderAppIcon(64))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(iconBmp, emblemRect);
            }

            using (var titleFont = ThemeHelper.CreateUiFont(10.5f, FontStyle.Bold))
            using (var subFont = ThemeHelper.CreateUiFont(8f, FontStyle.Regular))
            using (var tb = new SolidBrush(pal.TextPrimary))
            {
                g.DrawString(_deviceName, titleFont, tb, 58f, 15f);

                bool sleeping = _result != null && _result.IsSleeping;
                bool activeBatt = _result != null && _result.State == BatteryState.Battery && !sleeping;
                bool unavail = _result != null && _result.State == BatteryState.Unavailable;
                Color dotCol = activeBatt ? pal.Green : ((sleeping || unavail) ? pal.Amber : pal.TextMuted);

                string connLabel;
                if (activeBatt)
                {
                    string cn = _result.ConnName();
                    string dpi = _result.DpiName();
                    connLabel = !string.IsNullOrEmpty(cn) ? cn + " 已连接" : "已连接";
                    if (!string.IsNullOrEmpty(dpi)) connLabel += " · " + dpi;
                }
                else if (sleeping)
                {
                    string cn = _result.ConnName();
                    connLabel = (!string.IsNullOrEmpty(cn) ? cn + " · " : "") + "省电休眠中";
                }
                else if (unavail)
                {
                    connLabel = "休眠或待唤醒";
                }
                else
                {
                    connLabel = "未检测到设备";
                }

                float pillX = 58f;
                float pillY = 36.5f;
                using (var db = new SolidBrush(dotCol))
                    g.FillEllipse(db, pillX, pillY + 3.5f, 6.5f, 6.5f);
                using (var sb = new SolidBrush(pal.TextSecondary))
                    g.DrawString(connLabel, subFont, sb, pillX + 10f, pillY);
            }

            RectangleF settingsBtn = new RectangleF(BaseW - 16f - 28f, 18f, 28f, 28f);
            RectangleF refreshBtn = new RectangleF(settingsBtn.X - 6f - 28f, 18f, 28f, 28f);

            DrawIconButton(g, refreshBtn, "refresh", pal, DrawRefreshGlyph);
            DrawIconButton(g, settingsBtn, "settings", pal, DrawGearGlyph);

            hits.Add(new HitTarget
            {
                Id = "refresh",
                Rect = refreshBtn,
                Click = delegate { if (OnRefreshClick != null) OnRefreshClick(); }
            });
            hits.Add(new HitTarget
            {
                Id = "settings",
                Rect = settingsBtn,
                Click = delegate { _settingsView = true; _hoverId = null; }
            });
        }

        private void DrawHeroBattery(Graphics g, RectangleF card, ThemePalette pal)
        {
            bool hasBatt = _result != null && _result.State == BatteryState.Battery && _result.Percent >= 0;
            bool sleeping = hasBatt && _result.IsSleeping;
            int pct = hasBatt ? Math.Max(0, Math.Min(100, _result.Percent)) : 0;
            bool charging = hasBatt && !sleeping && _result.Charging;

            Color accent = !hasBatt
                ? pal.TextMuted
                : (sleeping ? Color.FromArgb(145, pal.TextSecondary) : ThemeHelper.GetBatteryColor(pct, charging, pal));

            using (var bigFont = ThemeHelper.CreateUiFont(28f, FontStyle.Bold))
            using (var pctFont = ThemeHelper.CreateUiFont(12f, FontStyle.Bold))
            using (var badgeFont = ThemeHelper.CreateUiFont(8f, FontStyle.Bold))
            using (var metaFont = ThemeHelper.CreateUiFont(8f, FontStyle.Regular))
            {
                string numStr = hasBatt ? pct.ToString() : "--";
                Color numCol = sleeping ? pal.TextSecondary : pal.TextPrimary;
                using (var nb = new SolidBrush(numCol))
                    g.DrawString(numStr, bigFont, nb, card.X + 14f, card.Y + 8f);

                SizeF numSz = g.MeasureString(numStr, bigFont);
                if (hasBatt)
                {
                    using (var pb = new SolidBrush(sleeping ? pal.TextMuted : pal.TextSecondary))
                        g.DrawString("%", pctFont, pb, card.X + 10f + numSz.Width - 6f, card.Y + 28f);
                }

                // Right status badge
                string badgeText;
                Color badgeBg;
                Color badgeFg;
                if (!hasBatt)
                {
                    badgeText = (_result != null && _result.State == BatteryState.Unavailable) ? "休眠中" : "已离线";
                    badgeBg = pal.PillBg;
                    badgeFg = pal.TextSecondary;
                }
                else if (sleeping)
                {
                    badgeText = "休眠保底";
                    badgeBg = pal.AmberSoft;
                    badgeFg = pal.Amber;
                }
                else if (charging && pct >= 100)
                {
                    badgeText = "已充满";
                    badgeBg = pal.GreenSoft;
                    badgeFg = pal.Green;
                }
                else if (charging)
                {
                    badgeText = "充电中 ⚡";
                    badgeBg = pal.GreenSoft;
                    badgeFg = pal.Green;
                }
                else if (pct <= 10)
                {
                    badgeText = "电量极低";
                    badgeBg = pal.RedSoft;
                    badgeFg = pal.Red;
                }
                else if (pct <= (_settings != null ? _settings.LowThreshold : 20))
                {
                    badgeText = "电量偏低";
                    badgeBg = pal.AmberSoft;
                    badgeFg = pal.Amber;
                }
                else
                {
                    badgeText = "电量良好";
                    badgeBg = pal.GreenSoft;
                    badgeFg = pal.Green;
                }

                SizeF bsz = g.MeasureString(badgeText, badgeFont);
                float bw = bsz.Width + 16f;
                RectangleF br = new RectangleF(card.Right - 14f - bw, card.Y + 18f, bw, 24f);
                using (GraphicsPath bp = ThemeHelper.RoundedRect(br, 12f))
                using (var bb = new SolidBrush(badgeBg))
                using (var bf = new SolidBrush(badgeFg))
                using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                {
                    g.FillPath(bb, bp);
                    g.DrawString(badgeText, badgeFont, bf, br, sf);
                }

                // Progress bar
                RectangleF barRect = new RectangleF(card.X + 16f, card.Y + 66f, card.Width - 32f, 10.5f);
                using (GraphicsPath trackPath = ThemeHelper.RoundedRect(barRect, 5.5f))
                using (var tb = new SolidBrush(pal.TrackBg))
                {
                    g.FillPath(tb, trackPath);
                }

                if (hasBatt && pct > 0)
                {
                    float fw = Math.Max(11f, barRect.Width * pct / 100f);
                    RectangleF fillRect = new RectangleF(barRect.X, barRect.Y, fw, barRect.Height);
                    Color gradEnd;
                    if (sleeping)
                        gradEnd = Color.FromArgb(165, pal.TextMuted);
                    else if (charging)
                        gradEnd = Color.FromArgb(56, 189, 248);
                    else if (pct <= 10)
                        gradEnd = Color.FromArgb(251, 113, 133);
                    else if (pct <= 25)
                        gradEnd = Color.FromArgb(253, 224, 71);
                    else
                        gradEnd = Color.FromArgb(16, 185, 129);

                    using (GraphicsPath fp = ThemeHelper.RoundedRect(fillRect, 5.5f))
                    using (var lb = new LinearGradientBrush(fillRect, accent, gradEnd, LinearGradientMode.Horizontal))
                    {
                        g.FillPath(lb, fp);

                        if (charging)
                        {
                            Region prevClip = g.Clip;
                            g.SetClip(fp, CombineMode.Intersect);
                            float sx = fillRect.X + _shimmerPhase * fillRect.Width;
                            RectangleF shimRect = new RectangleF(sx - 24f, fillRect.Y, 48f, fillRect.Height);
                            using (var sb = new LinearGradientBrush(shimRect,
                                Color.FromArgb(0, 255, 255, 255),
                                Color.FromArgb(115, 255, 255, 255),
                                LinearGradientMode.Horizontal))
                            {
                                var cb = new ColorBlend(3);
                                cb.Colors = new Color[]
                                {
                                    Color.FromArgb(0, 255, 255, 255),
                                    Color.FromArgb(115, 255, 255, 255),
                                    Color.FromArgb(0, 255, 255, 255)
                                };
                                cb.Positions = new float[] { 0f, 0.5f, 1f };
                                sb.InterpolationColors = cb;
                                g.FillRectangle(sb, shimRect);
                            }
                            g.Clip = prevClip;
                        }
                    }
                }

                // Subtle 25 / 50 / 75 tick marks
                using (var tickPen = new Pen(Color.FromArgb(pal.IsDark ? 55 : 70, pal.Surface), 1f))
                {
                    for (int t = 1; t <= 3; t++)
                    {
                        float tx = barRect.X + barRect.Width * (t * 0.25f);
                        g.DrawLine(tickPen, tx, barRect.Y + 1.5f, tx, barRect.Bottom - 1.5f);
                    }
                }

                // Bottom ETA / Sleep / Last-Full row
                string leftMeta;
                if (_refreshing)
                {
                    leftMeta = "正在同步设备状态…";
                }
                else if (sleeping)
                {
                    leftMeta = "上次电量 " + pct + "% · 移动鼠标自动唤醒";
                }
                else if (hasBatt && _hasEta && !string.IsNullOrEmpty(_etaText))
                {
                    string rateStr = (_pctPerHour >= 0.1)
                        ? string.Format(" · {0}{1:0.0}%/时", charging ? "+" : "-", _pctPerHour)
                        : "";
                    leftMeta = (charging ? "充满预计 " : "预计续航 ") + _etaText + rateStr;
                }
                else if (hasBatt && charging && pct >= 100)
                {
                    leftMeta = "电池已完全充满，可拔下数据线";
                }
                else if (hasBatt)
                {
                    leftMeta = "正在积累历史样本以估算续航";
                }
                else
                {
                    leftMeta = "移动鼠标或插入接收器后自动恢复";
                }

                float rightReserved = 0f;
                if (hasBatt && _result.Timestamp != DateTime.MinValue)
                {
                    string ts = FormatRelativeTime(_result.Timestamp);
                    SizeF tsz = g.MeasureString(ts, metaFont);
                    rightReserved = tsz.Width + 10f;
                    using (var tb2 = new SolidBrush(pal.TextMuted))
                        g.DrawString(ts, metaFont, tb2, card.Right - 14f - tsz.Width, card.Y + 86f);
                }

                RectangleF leftMetaRect = new RectangleF(card.X + 16f, card.Y + 86f, card.Width - 32f - rightReserved, 18f);
                using (var mb = new SolidBrush(pal.TextSecondary))
                using (var sfTrim = new StringFormat(StringFormatFlags.NoWrap) { Trimming = StringTrimming.EllipsisCharacter })
                    g.DrawString(leftMeta, metaFont, mb, leftMetaRect, sfTrim);
            }
        }

        private void DrawSparklineCard(Graphics g, RectangleF card, ThemePalette pal, List<HitTarget> hits)
        {
            _chartPoints.Clear();
            _chartSamples.Clear();

            int rangeHours = (_settings != null && _settings.ChartRangeHours == 168) ? 168 : 24;
            bool is7d = (rangeHours == 168);

            // Filter samples within selected range
            DateTime nowTime = DateTime.Now;
            DateTime cutoff = nowTime.AddHours(-rangeHours);
            var filtered = new List<BatteryHistory.Sample>();
            if (_allSamples != null)
            {
                for (int i = 0; i < _allSamples.Length; i++)
                {
                    if (_allSamples[i].T >= cutoff) filtered.Add(_allSamples[i]);
                }
                // Fallback if preview/history timestamps are slightly older than range
                if (filtered.Count < 2 && _allSamples.Length >= 2)
                {
                    filtered.Clear();
                    DateTime anchor = _allSamples[_allSamples.Length - 1].T;
                    DateTime altCut = anchor.AddHours(-rangeHours);
                    for (int i = 0; i < _allSamples.Length; i++)
                        if (_allSamples[i].T >= altCut) filtered.Add(_allSamples[i]);
                }
            }

            using (var titleFont = ThemeHelper.CreateUiFont(8.5f, FontStyle.Bold))
            using (var subFont = ThemeHelper.CreateUiFont(7.8f, FontStyle.Regular))
            using (var pillFont = ThemeHelper.CreateUiFont(7.4f, FontStyle.Regular))
            using (var pillBold = ThemeHelper.CreateUiFont(7.4f, FontStyle.Bold))
            using (var axisFont = ThemeHelper.CreateUiFont(7.2f, FontStyle.Regular))
            using (var sfC = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            {
                using (var tb = new SolidBrush(pal.TextPrimary))
                    g.DrawString("电量趋势", titleFont, tb, card.X + 14f, card.Y + 12f);

                // Range switcher pills: 24h | 7天
                RectangleF r24 = new RectangleF(card.X + 80f, card.Y + 10f, 36f, 20f);
                RectangleF r7d = new RectangleF(r24.Right + 5f, card.Y + 10f, 36f, 20f);
                DrawMiniPill(g, r24, "24h", !is7d, _hoverId == "rng_24", pal, pillFont, pillBold, sfC);
                DrawMiniPill(g, r7d, "7天", is7d, _hoverId == "rng_7d", pal, pillFont, pillBold, sfC);

                hits.Add(new HitTarget
                {
                    Id = "rng_24",
                    Rect = r24,
                    Click = delegate
                    {
                        if (_settings != null) _settings.ChartRangeHours = 24;
                        _hoverSampleIndex = -1;
                        if (OnChartRangeChanged != null) OnChartRangeChanged(24);
                    }
                });
                hits.Add(new HitTarget
                {
                    Id = "rng_7d",
                    Rect = r7d,
                    Click = delegate
                    {
                        if (_settings != null) _settings.ChartRangeHours = 168;
                        _hoverSampleIndex = -1;
                        if (OnChartRangeChanged != null) OnChartRangeChanged(168);
                    }
                });

                _chartPlotRect = new RectangleF(card.X + 14f, card.Y + 42f, card.Width - 60f, 100f);

                // Horizontal gridlines (100%, 50%, 0%)
                using (var gridPen = new Pen(pal.GridLine, 1f))
                using (var ab = new SolidBrush(pal.TextMuted))
                using (var sfRight = new StringFormat(StringFormatFlags.NoWrap) { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center })
                {
                    gridPen.DashStyle = DashStyle.Dot;
                    int[] levels = new int[] { 100, 50, 0 };
                    for (int i = 0; i < levels.Length; i++)
                    {
                        float y = _chartPlotRect.Bottom - (_chartPlotRect.Height * levels[i] / 100f);
                        g.DrawLine(gridPen, _chartPlotRect.Left, y, _chartPlotRect.Right, y);
                        g.DrawString(levels[i] + "%", axisFont, ab,
                            new RectangleF(_chartPlotRect.Right + 2f, y - 8f, card.Right - _chartPlotRect.Right - 12f, 16f),
                            sfRight);
                    }
                }

                // Bottom info strip inside chart card (Last full charge / sample count)
                float footerY = card.Bottom - 22f;
                using (var sb = new SolidBrush(pal.TextMuted))
                {
                    string leftFoot = !string.IsNullOrEmpty(_lastFullText)
                        ? ("上次充满：" + _lastFullText)
                        : ("保留最近 7 天电量记录");
                    g.DrawString(leftFoot, axisFont, sb, card.X + 14f, footerY);

                    string rightFoot = "样本 " + filtered.Count + " 条";
                    SizeF rfsz = g.MeasureString(rightFoot, axisFont);
                    g.DrawString(rightFoot, axisFont, sb, card.Right - 14f - rfsz.Width, footerY);
                }

                if (filtered.Count < 2)
                {
                    using (var mb = new SolidBrush(pal.TextMuted))
                        g.DrawString("持续使用后将在此展示电量变化曲线", subFont, mb, _chartPlotRect, sfC);
                    return;
                }

                DateTime endT = filtered[filtered.Count - 1].T;
                if (nowTime > endT) endT = nowTime;
                DateTime minT = filtered[0].T;
                double spanHours = Math.Max(1.0, Math.Min(rangeHours, (endT - minT).TotalHours));
                DateTime windowStart = endT.AddHours(-spanHours);

                int minPct = 100, maxPct = 0;
                int step = Math.Max(1, filtered.Count / 80);
                for (int i = 0; i < filtered.Count; i++)
                {
                    var s = filtered[i];
                    if (s.Pct < minPct) minPct = s.Pct;
                    if (s.Pct > maxPct) maxPct = s.Pct;
                    if (i != 0 && i != filtered.Count - 1 && (i % step != 0)) continue;
                    double frac = (s.T - windowStart).TotalSeconds / Math.Max(60.0, (endT - windowStart).TotalSeconds);
                    if (frac < 0) frac = 0;
                    if (frac > 1) frac = 1;
                    float px = _chartPlotRect.Left + (float)(frac * _chartPlotRect.Width);
                    float py = _chartPlotRect.Bottom - (_chartPlotRect.Height * s.Pct / 100f);
                    _chartPoints.Add(new PointF(px, py));
                    _chartSamples.Add(s);
                }

                // Right header summary OR hovered sample inspection
                string rightHeader;
                Color rightCol = pal.TextSecondary;
                if (_hoverSampleIndex >= 0 && _hoverSampleIndex < _chartSamples.Count)
                {
                    var hs = _chartSamples[_hoverSampleIndex];
                    string fmt = is7d ? "MM/dd HH:mm" : "HH:mm";
                    rightHeader = hs.T.ToString(fmt) + " · " + hs.Pct + "%" + (hs.Chg ? " ⚡" : "");
                    rightCol = pal.Accent;
                }
                else
                {
                    rightHeader = string.Format("高 {0}% · 低 {1}%", maxPct, minPct);
                }
                SizeF rhSz = g.MeasureString(rightHeader, subFont);
                using (var rb = new SolidBrush(rightCol))
                    g.DrawString(rightHeader, subFont, rb, card.Right - 14f - rhSz.Width, card.Y + 12f);

                if (_chartPoints.Count >= 2)
                {
                    Color curveCol = (_result != null && _result.State == BatteryState.Battery && !_result.IsSleeping)
                        ? ThemeHelper.GetBatteryColor(_result.Percent, _result.Charging, pal)
                        : pal.Green;

                    using (var areaPath = new GraphicsPath())
                    {
                        areaPath.AddLines(_chartPoints.ToArray());
                        PointF lastPt = _chartPoints[_chartPoints.Count - 1];
                        PointF firstPt = _chartPoints[0];
                        areaPath.AddLine(lastPt.X, _chartPlotRect.Bottom, firstPt.X, _chartPlotRect.Bottom);
                        areaPath.CloseFigure();

                        using (var ab = new LinearGradientBrush(
                            _chartPlotRect,
                            Color.FromArgb(pal.IsDark ? 72 : 52, curveCol),
                            Color.FromArgb(0, curveCol),
                            LinearGradientMode.Vertical))
                        {
                            g.FillPath(ab, areaPath);
                        }
                    }

                    for (int i = 1; i < _chartPoints.Count; i++)
                    {
                        Color segCol = _chartSamples[i].Chg ? pal.Amber : curveCol;
                        using (var pen = new Pen(segCol, 2.0f))
                        {
                            pen.StartCap = LineCap.Round;
                            pen.EndCap = LineCap.Round;
                            g.DrawLine(pen, _chartPoints[i - 1], _chartPoints[i]);
                        }
                    }

                    int focusIdx = (_hoverSampleIndex >= 0 && _hoverSampleIndex < _chartPoints.Count)
                        ? _hoverSampleIndex
                        : _chartPoints.Count - 1;
                    PointF fp = _chartPoints[focusIdx];
                    if (_hoverSampleIndex >= 0)
                    {
                        using (var vPen = new Pen(Color.FromArgb(120, pal.Accent), 1f))
                        {
                            vPen.DashStyle = DashStyle.Dash;
                            g.DrawLine(vPen, fp.X, _chartPlotRect.Top, fp.X, _chartPlotRect.Bottom);
                        }
                    }
                    Color dotCol = _hoverSampleIndex >= 0 ? pal.Accent : curveCol;
                    using (var haloB = new SolidBrush(Color.FromArgb(65, dotCol)))
                        g.FillEllipse(haloB, fp.X - 6f, fp.Y - 6f, 12f, 12f);
                    using (var dotB = new SolidBrush(dotCol))
                        g.FillEllipse(dotB, fp.X - 3f, fp.Y - 3f, 6f, 6f);
                }

                // X-axis ticks
                using (var ab = new SolidBrush(pal.TextMuted))
                {
                    string tickFmt = spanHours > 26.0 ? "MM/dd" : "HH:mm";
                    string startLbl = windowStart.ToString(tickFmt);
                    string midLbl = windowStart.AddHours(spanHours / 2.0).ToString(tickFmt);
                    g.DrawString(startLbl, axisFont, ab, _chartPlotRect.Left, _chartPlotRect.Bottom + 5f);

                    SizeF midSz = g.MeasureString(midLbl, axisFont);
                    g.DrawString(midLbl, axisFont, ab, _chartPlotRect.Left + (_chartPlotRect.Width - midSz.Width) / 2f, _chartPlotRect.Bottom + 5f);

                    SizeF nowSz = g.MeasureString("现在", axisFont);
                    g.DrawString("现在", axisFont, ab, _chartPlotRect.Right - nowSz.Width, _chartPlotRect.Bottom + 5f);
                }
            }
        }

        private static void DrawMiniPill(Graphics g, RectangleF r, string text, bool active, bool hover, ThemePalette pal, Font fReg, Font fBold, StringFormat sf)
        {
            Color bg = active ? pal.AccentSoft : (hover ? pal.CardHover : pal.PillBg);
            Color fg = active ? pal.Accent : pal.TextSecondary;
            using (GraphicsPath p = ThemeHelper.RoundedRect(r, 6f))
            using (var b = new SolidBrush(bg))
            using (var fb = new SolidBrush(fg))
            {
                g.FillPath(b, p);
                if (active)
                {
                    using (var pen = new Pen(Color.FromArgb(135, pal.Accent), 1f))
                        g.DrawPath(pen, p);
                }
                g.DrawString(text, active ? fBold : fReg, fb, r, sf);
            }
        }

        private void DrawFooterBar(Graphics g, ThemePalette pal, List<HitTarget> hits)
        {
            float y = BaseH - 46f;
            float pillH = 28f;

            using (var lblFont = ThemeHelper.CreateUiFont(7.8f, FontStyle.Regular))
            using (var pillFont = ThemeHelper.CreateUiFont(8f, FontStyle.Regular))
            using (var pillBold = ThemeHelper.CreateUiFont(8f, FontStyle.Bold))
            using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            {
                using (var lb = new SolidBrush(pal.TextMuted))
                    g.DrawString("图标样式", lblFont, lb, 16f, y + 6.5f);

                TrayIconStyle[] styles = new TrayIconStyle[]
                {
                    TrayIconStyle.MinimalNumber,
                    TrayIconStyle.RadialRing,
                    TrayIconStyle.BatteryPill,
                    TrayIconStyle.NumberWithBar
                };

                float sx = 78f;
                float sw = 45f;
                float gap = 5f;
                for (int i = 0; i < styles.Length; i++)
                {
                    TrayIconStyle st = styles[i];
                    RectangleF r = new RectangleF(sx + i * (sw + gap), y, sw, pillH);
                    string id = "style_" + i;
                    bool active = (_settings != null && _settings.IconStyle == st);
                    bool hovered = (_hoverId == id);

                    Color bg = active ? pal.AccentSoft : (hovered ? pal.CardHover : pal.PillBg);
                    Color fg = active ? pal.Accent : pal.TextSecondary;
                    using (GraphicsPath p = ThemeHelper.RoundedRect(r, 8f))
                    {
                        using (var b = new SolidBrush(bg)) g.FillPath(b, p);
                        if (active)
                        {
                            using (var pen = new Pen(Color.FromArgb(135, pal.Accent), 1f))
                                g.DrawPath(pen, p);
                        }
                    }
                    using (var fb = new SolidBrush(fg))
                        g.DrawString(IconRenderer.GetStyleShortName(st), active ? pillBold : pillFont, fb, r, sf);

                    TrayIconStyle captured = st;
                    hits.Add(new HitTarget
                    {
                        Id = id,
                        Rect = r,
                        Click = delegate
                        {
                            if (_settings != null) _settings.IconStyle = captured;
                            if (OnIconStyleChanged != null) OnIconStyleChanged(captured);
                        }
                    });
                }

                RectangleF exitRect = new RectangleF(BaseW - 16f - 44f, y, 44f, pillH);
                bool exitHover = (_hoverId == "exit_app");
                using (GraphicsPath ep = ThemeHelper.RoundedRect(exitRect, 8f))
                using (var eb = new SolidBrush(exitHover ? pal.RedSoft : Color.Transparent))
                using (var ef = new SolidBrush(exitHover ? pal.Red : pal.TextMuted))
                {
                    if (exitHover)
                    {
                        g.FillPath(eb, ep);
                        using (var pen = new Pen(Color.FromArgb(115, pal.Red), 1f))
                            g.DrawPath(pen, ep);
                    }
                    g.DrawString("退出", exitHover ? pillBold : pillFont, ef, exitRect, sf);
                }
                hits.Add(new HitTarget
                {
                    Id = "exit_app",
                    Rect = exitRect,
                    Click = delegate { if (OnExitClick != null) OnExitClick(); }
                });
            }
        }

        private void DrawSettingsView(Graphics g, ThemePalette pal, bool glassMode, List<HitTarget> hits)
        {
            RectangleF backBtn = new RectangleF(16f, 16f, 28f, 28f);
            DrawIconButton(g, backBtn, "back", pal, DrawBackGlyph);
            hits.Add(new HitTarget
            {
                Id = "back",
                Rect = backBtn,
                Click = delegate { _settingsView = false; _hoverId = null; }
            });

            using (var hFont = ThemeHelper.CreateUiFont(10.5f, FontStyle.Bold))
            using (var verFont = ThemeHelper.CreateUiFont(8f, FontStyle.Regular))
            using (var tb = new SolidBrush(pal.TextPrimary))
            using (var vb = new SolidBrush(pal.TextMuted))
            {
                g.DrawString("偏好设置", hFont, tb, 52f, 20f);
                string ver = "v2.1.1";
                SizeF vsz = g.MeasureString(ver, verFont);
                g.DrawString(ver, verFont, vb, BaseW - 16f - vsz.Width, 23f);
            }

            float y = 56f;
            float cw = BaseW - 32f;

            // Card 1: Refresh interval
            RectangleF c1 = new RectangleF(16f, y, cw, 62f);
            DrawCard(g, c1, pal, glassMode);
            DrawSegmentedSetting(g, c1, "自动刷新间隔",
                new string[] { "10 秒", "30 秒", "1 分钟", "5 分钟" },
                new int[] { 10, 30, 60, 300 },
                _settings != null ? _settings.IntervalSec : 30,
                "iv_", pal, hits,
                delegate(int v) { if (_settings != null) _settings.IntervalSec = v; if (OnIntervalChanged != null) OnIntervalChanged(v); });

            y += 70f;

            // Card 2: Low battery alert + threshold
            RectangleF c2 = new RectangleF(16f, y, cw, 94f);
            DrawCard(g, c2, pal, glassMode);
            DrawToggleRow(g, new RectangleF(c2.X, c2.Y + 3f, c2.Width, 32f), "低电量气泡提醒",
                _settings != null && _settings.LowNotify, "tgl_low", pal, hits,
                delegate
                {
                    bool next = !(_settings != null && _settings.LowNotify);
                    if (_settings != null) _settings.LowNotify = next;
                    if (OnLowNotifyChanged != null) OnLowNotifyChanged(next);
                });
            DrawSegmentedSetting(g, new RectangleF(c2.X, c2.Y + 33f, c2.Width, 58f), "提醒阈值",
                new string[] { "10%", "15%", "20%", "30%" },
                new int[] { 10, 15, 20, 30 },
                _settings != null ? _settings.LowThreshold : 20,
                "th_", pal, hits,
                delegate(int v) { if (_settings != null) _settings.LowThreshold = v; if (OnLowThresholdChanged != null) OnLowThresholdChanged(v); });

            y += 102f;

            // Card 3: Full notify + Fullscreen Game DND + AutoStart
            RectangleF c3 = new RectangleF(16f, y, cw, 114f);
            DrawCard(g, c3, pal, glassMode);
            DrawToggleRow(g, new RectangleF(c3.X, c3.Y + 3f, c3.Width, 34f), "充满电时气泡提醒 (100%)",
                _settings != null && _settings.FullNotify, "tgl_full", pal, hits,
                delegate
                {
                    bool next = !(_settings != null && _settings.FullNotify);
                    if (_settings != null) _settings.FullNotify = next;
                    if (OnFullNotifyChanged != null) OnFullNotifyChanged(next);
                });
            using (var sep = new Pen(pal.SubtleBorder, 1f))
                g.DrawLine(sep, c3.X + 14f, c3.Y + 38f, c3.Right - 14f, c3.Y + 38f);

            DrawToggleRow(g, new RectangleF(c3.X, c3.Y + 40f, c3.Width, 34f), "全屏游戏 / 视频免打扰",
                _settings == null || _settings.FullscreenDnd, "tgl_dnd", pal, hits,
                delegate
                {
                    bool next = !(_settings == null || _settings.FullscreenDnd);
                    if (_settings != null) _settings.FullscreenDnd = next;
                    if (OnFullscreenDndChanged != null) OnFullscreenDndChanged(next);
                });
            using (var sep = new Pen(pal.SubtleBorder, 1f))
                g.DrawLine(sep, c3.X + 14f, c3.Y + 75f, c3.Right - 14f, c3.Y + 75f);

            DrawToggleRow(g, new RectangleF(c3.X, c3.Y + 77f, c3.Width, 34f), "随 Windows 开机自启",
                _autoStart, "tgl_auto", pal, hits,
                delegate
                {
                    bool next = !_autoStart;
                    _autoStart = next;
                    if (OnAutoStartChanged != null) OnAutoStartChanged(next);
                });

            y += 122f;

            // Firmware & hardware info caption
            using (var infoFont = ThemeHelper.CreateUiFont(7.6f, FontStyle.Regular))
            using (var ib = new SolidBrush(pal.TextMuted))
            {
                string fwLine = !string.IsNullOrEmpty(_firmware)
                    ? ("设备固件：FW " + _firmware + "  ·  跟随系统强调色")
                    : "跟随 Windows 系统强调色与深浅主题";
                g.DrawString(fwLine, infoFont, ib, 18f, y);
            }

            y += 22f;

            // Bottom Action Pills
            string[] actLabels = new string[] { "复制调试", "数据目录", "稳定安装", "检查更新" };
            Action[] actHandlers = new Action[]
            {
                delegate { if (OnCopyDebugClick != null) OnCopyDebugClick(); },
                delegate { if (OnOpenDataDirClick != null) OnOpenDataDirClick(); },
                delegate { if (OnInstallStableClick != null) OnInstallStableClick(); },
                delegate { if (OnCheckUpdateClick != null) OnCheckUpdateClick(); }
            };

            float bw = (cw - 18f) / 4f;
            using (var bFont = ThemeHelper.CreateUiFont(8f, FontStyle.Regular))
            using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            {
                for (int i = 0; i < actLabels.Length; i++)
                {
                    RectangleF br = new RectangleF(16f + i * (bw + 6f), y, bw, 32f);
                    string id = "act_" + i;
                    bool hov = (_hoverId == id);
                    Color cardBase = glassMode ? pal.CardGlass : pal.CardBg;
                    using (GraphicsPath bp = ThemeHelper.RoundedRect(br, 8f))
                    using (var fill = new SolidBrush(hov ? pal.CardHover : cardBase))
                    using (var pen = new Pen(pal.SubtleBorder, 1f))
                    using (var tf = new SolidBrush(pal.TextPrimary))
                    {
                        g.FillPath(fill, bp);
                        g.DrawPath(pen, bp);
                        g.DrawString(actLabels[i], bFont, tf, br, sf);
                    }
                    Action handler = actHandlers[i];
                    hits.Add(new HitTarget { Id = id, Rect = br, Click = handler });
                }
            }
        }

        private void DrawToggleRow(Graphics g, RectangleF row, string label, bool isOn, string id, ThemePalette pal, List<HitTarget> hits, Action onClick)
        {
            using (var font = ThemeHelper.CreateUiFont(8.7f, FontStyle.Regular))
            using (var tb = new SolidBrush(pal.TextPrimary))
            using (var sf = new StringFormat { LineAlignment = StringAlignment.Center })
            {
                g.DrawString(label, font, tb, new RectangleF(row.X + 14f, row.Y, row.Width - 74f, row.Height), sf);
            }

            RectangleF swRect = new RectangleF(row.Right - 50f, row.Y + (row.Height - 20f) / 2f, 36f, 20f);
            Color trackCol = isOn ? pal.Accent : pal.TrackBg;
            using (GraphicsPath tp = ThemeHelper.RoundedRect(swRect, 10f))
            using (var tb = new SolidBrush(trackCol))
            {
                g.FillPath(tb, tp);
            }
            float knobX = isOn ? (swRect.Right - 17f) : (swRect.X + 3f);
            using (var kb = new SolidBrush(Color.White))
            {
                g.FillEllipse(kb, knobX, swRect.Y + 3f, 14f, 14f);
            }

            hits.Add(new HitTarget { Id = id, Rect = row, Click = onClick });
        }

        private void DrawSegmentedSetting(
            Graphics g, RectangleF box, string label,
            string[] labels, int[] values, int currentVal,
            string idPrefix, ThemePalette pal, List<HitTarget> hits, Action<int> onPick)
        {
            using (var lblFont = ThemeHelper.CreateUiFont(8.1f, FontStyle.Regular))
            using (var optFont = ThemeHelper.CreateUiFont(8f, FontStyle.Regular))
            using (var optBold = ThemeHelper.CreateUiFont(8f, FontStyle.Bold))
            using (var lb = new SolidBrush(pal.TextSecondary))
            using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            {
                g.DrawString(label, lblFont, lb, box.X + 14f, box.Y + 7f);

                float px = box.X + 14f;
                float py = box.Y + 27f;
                float totalW = box.Width - 28f;
                float gap = 6f;
                float itemW = (totalW - gap * (labels.Length - 1)) / labels.Length;
                for (int i = 0; i < labels.Length; i++)
                {
                    RectangleF r = new RectangleF(px + i * (itemW + gap), py, itemW, 24f);
                    string id = idPrefix + i;
                    bool active = (values[i] == currentVal);
                    bool hov = (_hoverId == id);
                    Color bg = active ? pal.AccentSoft : (hov ? pal.CardHover : pal.PillBg);
                    Color fg = active ? pal.Accent : pal.TextPrimary;

                    using (GraphicsPath p = ThemeHelper.RoundedRect(r, 6f))
                    using (var b = new SolidBrush(bg))
                    using (var tf = new SolidBrush(fg))
                    {
                        g.FillPath(b, p);
                        if (active)
                        {
                            using (var pen = new Pen(Color.FromArgb(135, pal.Accent), 1f))
                                g.DrawPath(pen, p);
                        }
                        g.DrawString(labels[i], active ? optBold : optFont, tf, r, sf);
                    }
                    int val = values[i];
                    hits.Add(new HitTarget { Id = id, Rect = r, Click = delegate { onPick(val); } });
                }
            }
        }

        private void DrawCard(Graphics g, RectangleF rect, ThemePalette pal, bool glassMode)
        {
            using (GraphicsPath p = ThemeHelper.RoundedRect(rect, 12f))
            using (var fill = new SolidBrush(glassMode ? pal.CardGlass : pal.CardBg))
            using (var pen = new Pen(pal.SubtleBorder, 1f))
            {
                g.FillPath(fill, p);
                g.DrawPath(pen, p);
            }
        }

        private void DrawIconButton(Graphics g, RectangleF rect, string id, ThemePalette pal, Action<Graphics, RectangleF, Color> drawGlyph)
        {
            bool hov = (_hoverId == id);
            if (hov)
            {
                using (GraphicsPath p = ThemeHelper.RoundedRect(rect, 7f))
                using (var b = new SolidBrush(pal.CardHover))
                    g.FillPath(b, p);
            }
            drawGlyph(g, rect, hov ? pal.TextPrimary : pal.TextSecondary);
        }

        private static void DrawRefreshGlyph(Graphics g, RectangleF r, Color col)
        {
            float cx = r.X + r.Width / 2f;
            float cy = r.Y + r.Height / 2f;
            RectangleF arc = new RectangleF(cx - 6f, cy - 6f, 12f, 12f);
            using (var pen = new Pen(col, 1.6f))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                g.DrawArc(pen, arc, -45f, 285f);
                g.DrawLine(pen, cx + 4.3f, cy - 4.3f, cx + 4.8f, cy - 0.8f);
                g.DrawLine(pen, cx + 4.3f, cy - 4.3f, cx + 1.0f, cy - 4.8f);
            }
        }

        private static void DrawGearGlyph(Graphics g, RectangleF r, Color col)
        {
            float cx = r.X + r.Width / 2f;
            float cy = r.Y + r.Height / 2f;
            using (var ringPen = new Pen(col, 1.8f))
            using (var toothPen = new Pen(col, 2.0f))
            {
                toothPen.StartCap = LineCap.Round;
                toothPen.EndCap = LineCap.Round;
                g.DrawEllipse(ringPen, cx - 3.6f, cy - 3.6f, 7.2f, 7.2f);
                for (int i = 0; i < 6; i++)
                {
                    double ang = i * Math.PI / 3.0 + Math.PI / 6.0;
                    float x1 = cx + (float)(Math.Cos(ang) * 4.5);
                    float y1 = cy + (float)(Math.Sin(ang) * 4.5);
                    float x2 = cx + (float)(Math.Cos(ang) * 6.2);
                    float y2 = cy + (float)(Math.Sin(ang) * 6.2);
                    g.DrawLine(toothPen, x1, y1, x2, y2);
                }
            }
        }

        private static void DrawBackGlyph(Graphics g, RectangleF r, Color col)
        {
            float cx = r.X + r.Width / 2f;
            float cy = r.Y + r.Height / 2f;
            using (var pen = new Pen(col, 1.7f))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                pen.LineJoin = LineJoin.Round;
                g.DrawLines(pen, new PointF[]
                {
                    new PointF(cx + 2.5f, cy - 5f),
                    new PointF(cx - 2.5f, cy),
                    new PointF(cx + 2.5f, cy + 5f)
                });
            }
        }

        private static string FormatRelativeTime(DateTime t)
        {
            double sec = (DateTime.Now - t).TotalSeconds;
            if (sec < 15) return "刚刚更新";
            if (sec < 60) return ((int)sec) + " 秒前";
            if (sec < 3600) return ((int)(sec / 60)) + " 分钟前";
            return t.ToString("HH:mm");
        }
    }
}
