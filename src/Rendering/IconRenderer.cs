using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;

namespace MaicongTray
{
    internal static class IconRenderer
    {
        internal static Bitmap Render(BatteryResult r, TrayIconStyle style, bool boltAlt)
        {
            return Render(r, style, boltAlt, ThemeHelper.IsDarkTaskbar());
        }

        internal static Bitmap Render(BatteryResult r, TrayIconStyle style, bool boltAlt, bool darkTaskbar)
        {
            switch (style)
            {
                case TrayIconStyle.RadialRing:
                    return RenderRadialRing(r, boltAlt, darkTaskbar);
                case TrayIconStyle.BatteryPill:
                    return RenderBatteryPill(r, boltAlt, darkTaskbar);
                case TrayIconStyle.NumberWithBar:
                    return RenderNumberWithBar(r, boltAlt, darkTaskbar);
                default:
                    return RenderMinimalNumber(r, boltAlt, darkTaskbar);
            }
        }

        internal static string GetStyleName(TrayIconStyle style)
        {
            switch (style)
            {
                case TrayIconStyle.RadialRing: return "微弧光环";
                case TrayIconStyle.BatteryPill: return "经典电池";
                case TrayIconStyle.NumberWithBar: return "数字底条";
                default: return "极简数字";
            }
        }

        internal static string GetStyleShortName(TrayIconStyle style)
        {
            switch (style)
            {
                case TrayIconStyle.RadialRing: return "光环";
                case TrayIconStyle.BatteryPill: return "电池";
                case TrayIconStyle.NumberWithBar: return "底条";
                default: return "数字";
            }
        }

        private static Color GetLevelAccent(BatteryResult r, bool darkTaskbar, bool useNeutralNormal)
        {
            if (r == null || r.State != BatteryState.Battery)
                return darkTaskbar ? Color.FromArgb(165, 170, 180) : Color.FromArgb(110, 116, 126);

            if (r.IsSleeping)
                return darkTaskbar ? Color.FromArgb(155, 185, 195, 210) : Color.FromArgb(150, 95, 102, 116);

            if (r.Charging)
                return darkTaskbar ? Color.FromArgb(52, 211, 153) : Color.FromArgb(5, 150, 105);
            if (r.Percent <= 10)
                return darkTaskbar ? Color.FromArgb(255, 92, 92) : Color.FromArgb(220, 38, 38);
            if (r.Percent <= 25)
                return darkTaskbar ? Color.FromArgb(251, 191, 36) : Color.FromArgb(217, 119, 6);

            if (useNeutralNormal)
                return darkTaskbar ? Color.White : Color.FromArgb(20, 22, 26);

            return darkTaskbar ? Color.FromArgb(52, 211, 153) : Color.FromArgb(16, 163, 107);
        }

        private static string GetShortText(BatteryResult r)
        {
            if (r != null && r.State == BatteryState.Battery) return r.Percent.ToString();
            if (r != null && (r.State == BatteryState.Unavailable || r.State == BatteryState.Disconnected)) return "--";
            return "?";
        }

        // Style 0: Minimal clean numeral with subtle contrast halo & animated bolt
        private static Bitmap RenderMinimalNumber(BatteryResult r, bool boltAlt, bool darkTaskbar)
        {
            const int S = 64;
            var bmp = new Bitmap(S, S, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                SetupGraphics(g);
                bool charging = r != null && r.State == BatteryState.Battery && r.Charging;
                string text = GetShortText(r);
                Color fg = GetLevelAccent(r, darkTaskbar, true);
                Color halo = darkTaskbar ? Color.FromArgb(145, 10, 12, 16) : Color.FromArgb(195, 255, 255, 255);

                float maxSpan = charging ? 50f : 58f;
                float cyOffset = charging ? 2f : 0f;
                DrawCenteredTextPath(g, text, new RectangleF(0, cyOffset, S, S), maxSpan, fg, halo, 3.6f);

                if (charging)
                {
                    DrawBolt(g, 45f, 1f, 17f, boltAlt, darkTaskbar);
                }
            }
            return bmp;
        }

        // Style 1: Radial progress ring + centered compact percentage
        private static Bitmap RenderRadialRing(BatteryResult r, bool boltAlt, bool darkTaskbar)
        {
            const int S = 64;
            var bmp = new Bitmap(S, S, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                SetupGraphics(g);
                bool hasBatt = r != null && r.State == BatteryState.Battery;
                bool charging = hasBatt && r.Charging;
                int pct = hasBatt ? Math.Max(0, Math.Min(100, r.Percent)) : 0;

                Color accent = GetLevelAccent(r, darkTaskbar, false);
                if (charging && boltAlt)
                    accent = darkTaskbar ? Color.FromArgb(110, 231, 183) : Color.FromArgb(16, 185, 129);

                Color track = darkTaskbar ? Color.FromArgb(70, 255, 255, 255) : Color.FromArgb(55, 0, 0, 0);
                Color fg = (hasBatt && r.IsSleeping)
                    ? (darkTaskbar ? Color.FromArgb(165, 200, 208, 220) : Color.FromArgb(155, 85, 92, 106))
                    : (darkTaskbar ? Color.White : Color.FromArgb(20, 22, 26));
                if (hasBatt && !r.IsSleeping && !charging && pct <= 10) fg = accent;

                float stroke = 6.2f;
                RectangleF ringRect = new RectangleF(stroke / 2f + 1.5f, stroke / 2f + 1.5f, S - stroke - 3f, S - stroke - 3f);

                using (var trackPen = new Pen(track, stroke))
                {
                    g.DrawEllipse(trackPen, ringRect);
                }

                if (hasBatt && pct > 0)
                {
                    float sweep = Math.Max(10f, 360f * pct / 100f);
                    using (var arcPen = new Pen(accent, stroke))
                    {
                        arcPen.StartCap = LineCap.Round;
                        arcPen.EndCap = LineCap.Round;
                        g.DrawArc(arcPen, ringRect, -90f, sweep);
                    }
                }

                string text = GetShortText(r);
                float maxSpan = (text.Length >= 3) ? 33f : 35f;
                DrawCenteredTextPath(g, text, new RectangleF(0, 0.5f, S, S), maxSpan, fg, Color.Transparent, 0f);

                if (charging)
                {
                    DrawBolt(g, 46f, 0f, 15f, boltAlt, darkTaskbar);
                }
            }
            return bmp;
        }

        // Style 2: Sleek macOS/Win11 horizontal battery capsule with live fill + crisp number
        private static Bitmap RenderBatteryPill(BatteryResult r, bool boltAlt, bool darkTaskbar)
        {
            const int S = 64;
            var bmp = new Bitmap(S, S, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                SetupGraphics(g);
                bool hasBatt = r != null && r.State == BatteryState.Battery;
                bool charging = hasBatt && r.Charging;
                int pct = hasBatt ? Math.Max(0, Math.Min(100, r.Percent)) : 0;

                Color border = darkTaskbar ? Color.FromArgb(225, 235, 240, 248) : Color.FromArgb(215, 32, 36, 44);
                Color bodyBg = darkTaskbar ? Color.FromArgb(55, 255, 255, 255) : Color.FromArgb(30, 0, 0, 0);
                Color fillCol = GetLevelAccent(r, darkTaskbar, false);

                RectangleF body = new RectangleF(2.5f, 13f, 52.5f, 38f);
                using (GraphicsPath bp = ThemeHelper.RoundedRect(body, 9f))
                {
                    using (var bg = new SolidBrush(bodyBg)) g.FillPath(bg, bp);

                    if (hasBatt && pct > 0)
                    {
                        float pad = 4.2f;
                        float availW = body.Width - pad * 2f;
                        float fillW = Math.Max(6f, availW * pct / 100f);
                        RectangleF fillRect = new RectangleF(body.X + pad, body.Y + pad, fillW, body.Height - pad * 2f);
                        using (GraphicsPath fp = ThemeHelper.RoundedRect(fillRect, 5f))
                        using (var fb = new SolidBrush(Color.FromArgb(charging ? 220 : 210, fillCol)))
                        {
                            g.FillPath(fb, fp);
                        }
                    }

                    using (var pen = new Pen(border, 3.4f)) g.DrawPath(pen, bp);
                }

                // Battery terminal nub on right
                RectangleF nub = new RectangleF(57.2f, 24f, 4.8f, 16f);
                using (GraphicsPath np = ThemeHelper.RoundedRect(nub, 2.4f))
                using (var nb = new SolidBrush(border))
                {
                    g.FillPath(nb, np);
                }

                string text = GetShortText(r);
                Color txtFg = Color.White;
                Color txtHalo = Color.FromArgb(190, 12, 14, 18);
                if (!darkTaskbar && (!hasBatt || pct < 45))
                {
                    txtFg = Color.FromArgb(20, 22, 26);
                    txtHalo = Color.FromArgb(200, 255, 255, 255);
                }
                DrawCenteredTextPath(g, text, body, text.Length >= 3 ? 38f : 34f, txtFg, txtHalo, 3.2f);

                if (charging)
                {
                    DrawBolt(g, 43f, 1f, 16f, boltAlt, darkTaskbar);
                }
            }
            return bmp;
        }

        // Style 3: Large crisp numeral + razor-clean bottom progress micro-bar
        private static Bitmap RenderNumberWithBar(BatteryResult r, bool boltAlt, bool darkTaskbar)
        {
            const int S = 64;
            var bmp = new Bitmap(S, S, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                SetupGraphics(g);
                bool hasBatt = r != null && r.State == BatteryState.Battery;
                bool charging = hasBatt && r.Charging;
                int pct = hasBatt ? Math.Max(0, Math.Min(100, r.Percent)) : 0;

                Color fg = (hasBatt && r.IsSleeping)
                    ? (darkTaskbar ? Color.FromArgb(165, 200, 208, 220) : Color.FromArgb(155, 85, 92, 106))
                    : (darkTaskbar ? Color.White : Color.FromArgb(20, 22, 26));
                Color accent = GetLevelAccent(r, darkTaskbar, false);
                if (hasBatt && !r.IsSleeping && !charging && pct <= 10) fg = accent;
                Color halo = darkTaskbar ? Color.FromArgb(135, 10, 12, 16) : Color.FromArgb(185, 255, 255, 255);

                string text = GetShortText(r);
                DrawCenteredTextPath(g, text, new RectangleF(0, -1f, S, 50f), charging ? 44f : 48f, fg, halo, 3.0f);

                // Bottom micro-bar
                RectangleF trackRect = new RectangleF(4f, 52f, S - 8f, 8.5f);
                Color trackBg = darkTaskbar ? Color.FromArgb(78, 255, 255, 255) : Color.FromArgb(55, 0, 0, 0);
                using (GraphicsPath tp = ThemeHelper.RoundedRect(trackRect, 4.2f))
                using (var tb = new SolidBrush(trackBg))
                {
                    g.FillPath(tb, tp);
                }

                if (hasBatt && pct > 0)
                {
                    float w = Math.Max(8f, trackRect.Width * pct / 100f);
                    RectangleF fillRect = new RectangleF(trackRect.X, trackRect.Y, w, trackRect.Height);
                    Color barCol = (charging && boltAlt)
                        ? (darkTaskbar ? Color.FromArgb(110, 231, 183) : Color.FromArgb(16, 185, 129))
                        : accent;
                    using (GraphicsPath fp = ThemeHelper.RoundedRect(fillRect, 4.2f))
                    using (var fb = new SolidBrush(barCol))
                    {
                        g.FillPath(fb, fp);
                    }
                }

                if (charging)
                {
                    DrawBolt(g, 46f, 0f, 15f, boltAlt, darkTaskbar);
                }
            }
            return bmp;
        }

        private static void SetupGraphics(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);
        }

        private static void DrawCenteredTextPath(Graphics g, string text, RectangleF targetBox, float maxSpan, Color fill, Color halo, float haloWidth)
        {
            using (var family = new FontFamily("Segoe UI"))
            using (var path = new GraphicsPath())
            using (var sf = new StringFormat(StringFormatFlags.NoWrap))
            {
                sf.Alignment = StringAlignment.Center;
                sf.LineAlignment = StringAlignment.Center;
                path.AddString(text, family, (int)FontStyle.Bold, 56f, new RectangleF(-68f, -68f, 200f, 200f), sf);
                RectangleF bounds = path.GetBounds();
                if (bounds.Width > 0f && bounds.Height > 0f)
                {
                    float availW = Math.Min(maxSpan, targetBox.Width - 4f);
                    float availH = Math.Min(maxSpan, targetBox.Height - 4f);
                    float scale = Math.Min(1f, Math.Min(availW / bounds.Width, availH / bounds.Height));
                    float cx = bounds.Left + bounds.Width / 2f;
                    float cy = bounds.Top + bounds.Height / 2f;
                    using (var m = new Matrix())
                    {
                        m.Translate(targetBox.X + targetBox.Width / 2f, targetBox.Y + targetBox.Height / 2f);
                        m.Scale(scale, scale);
                        m.Translate(-cx, -cy);
                        path.Transform(m);
                    }
                }
                if (halo.A > 0 && haloWidth > 0f)
                {
                    using (var pen = new Pen(halo, haloWidth))
                    {
                        pen.LineJoin = LineJoin.Round;
                        g.DrawPath(pen, path);
                    }
                }
                using (var b = new SolidBrush(fill))
                {
                    g.FillPath(b, path);
                }
            }
        }

        private static void DrawBolt(Graphics g, float x, float y, float h, bool alt, bool darkTaskbar)
        {
            float w = h * 0.68f;
            PointF[] pts = new PointF[]
            {
                new PointF(x + w * 0.62f, y),
                new PointF(x + w * 0.08f, y + h * 0.56f),
                new PointF(x + w * 0.46f, y + h * 0.56f),
                new PointF(x + w * 0.28f, y + h),
                new PointF(x + w * 0.96f, y + h * 0.40f),
                new PointF(x + w * 0.56f, y + h * 0.40f)
            };
            using (var path = new GraphicsPath())
            {
                path.AddPolygon(pts);
                Color outline = darkTaskbar ? Color.FromArgb(225, 12, 14, 18) : Color.FromArgb(220, 255, 255, 255);
                Color fill = darkTaskbar
                    ? (alt ? Color.FromArgb(250, 204, 21) : Color.FromArgb(254, 240, 138))
                    : (alt ? Color.FromArgb(217, 119, 6) : Color.FromArgb(245, 158, 11));
                using (var pen = new Pen(outline, 2.6f))
                {
                    pen.LineJoin = LineJoin.Round;
                    g.DrawPath(pen, path);
                }
                using (var b = new SolidBrush(fill))
                {
                    g.FillPath(b, path);
                }
            }
        }

        // Modern Fluent-style multi-resolution app icon:
        // Obsidian squircle + glass rim + emerald/cyan power arc + golden bolt.
        internal static Bitmap RenderAppIcon(int s)
        {
            var bmp = new Bitmap(s, s, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                SetupGraphics(g);
                float m = s * 0.055f;
                float w = s - 2f * m;
                float radius = w * 0.24f;
                RectangleF rect = new RectangleF(m, m, w, w);

                using (GraphicsPath sq = ThemeHelper.RoundedRect(rect, radius))
                {
                    using (var bg = new LinearGradientBrush(rect,
                        Color.FromArgb(34, 39, 50),
                        Color.FromArgb(16, 18, 24),
                        LinearGradientMode.Vertical))
                    {
                        g.FillPath(bg, sq);
                    }
                    // Subtle glass rim
                    using (var rim = new Pen(Color.FromArgb(75, 255, 255, 255), Math.Max(1f, s * 0.024f)))
                    {
                        g.DrawPath(rim, sq);
                    }
                }

                // Emerald power arc
                float pad = s * 0.21f;
                RectangleF arcRect = new RectangleF(pad, pad, s - 2f * pad, s - 2f * pad);
                float arcW = Math.Max(1.8f, s * 0.085f);
                using (var trackPen = new Pen(Color.FromArgb(42, 255, 255, 255), arcW))
                {
                    g.DrawEllipse(trackPen, arcRect);
                }
                using (var arcBrush = new LinearGradientBrush(arcRect,
                    Color.FromArgb(52, 211, 153),
                    Color.FromArgb(56, 189, 248),
                    LinearGradientMode.ForwardDiagonal))
                using (var arcPen = new Pen(arcBrush, arcW))
                {
                    arcPen.StartCap = LineCap.Round;
                    arcPen.EndCap = LineCap.Round;
                    g.DrawArc(arcPen, arcRect, -120f, 275f);
                }

                // Golden lightning core
                float bh = s * 0.42f;
                float bw = bh * 0.68f;
                float bx = (s - bw) / 2f;
                float by = (s - bh) / 2f;
                PointF[] pts = new PointF[]
                {
                    new PointF(bx + bw * 0.62f, by),
                    new PointF(bx + bw * 0.08f, by + bh * 0.56f),
                    new PointF(bx + bw * 0.47f, by + bh * 0.56f),
                    new PointF(bx + bw * 0.28f, by + bh),
                    new PointF(bx + bw * 0.94f, by + bh * 0.40f),
                    new PointF(bx + bw * 0.55f, by + bh * 0.40f)
                };
                using (var bp = new GraphicsPath())
                {
                    bp.AddPolygon(pts);
                    using (var fill = new LinearGradientBrush(
                        new RectangleF(bx, by, bw, bh),
                        Color.FromArgb(254, 240, 138),
                        Color.FromArgb(245, 158, 11),
                        LinearGradientMode.Vertical))
                    {
                        g.FillPath(fill, bp);
                    }
                }
            }
            return bmp;
        }

        internal static void WriteAppIco(string file)
        {
            int[] sizes = new int[] { 16, 24, 32, 48, 64, 128, 256 };
            var pngs = new List<byte[]>();
            foreach (int s in sizes)
            {
                using (Bitmap bmp = RenderAppIcon(s))
                using (var ms = new MemoryStream())
                {
                    bmp.Save(ms, ImageFormat.Png);
                    pngs.Add(ms.ToArray());
                }
            }
            using (var fs = new FileStream(file, FileMode.Create))
            using (var bw = new BinaryWriter(fs))
            {
                bw.Write((short)0);
                bw.Write((short)1);
                bw.Write((short)pngs.Count);
                int offset = 6 + 16 * pngs.Count;
                for (int i = 0; i < sizes.Length; i++)
                {
                    bw.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                    bw.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                    bw.Write((byte)0);
                    bw.Write((byte)0);
                    bw.Write((short)1);
                    bw.Write((short)32);
                    bw.Write(pngs[i].Length);
                    bw.Write(offset);
                    offset += pngs[i].Length;
                }
                foreach (byte[] p in pngs) bw.Write(p);
            }
        }
    }
}
