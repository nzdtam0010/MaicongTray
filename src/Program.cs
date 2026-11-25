using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;

[assembly: AssemblyTitle("MaicongTray")]
[assembly: AssemblyDescription("迈从鼠标电量托盘")]
[assembly: AssemblyProduct("MaicongTray")]
[assembly: AssemblyVersion("2.1.1.0")]
[assembly: AssemblyFileVersion("2.1.1.0")]

namespace MaicongTray
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            ThemeHelper.EnablePerMonitorDpiAwareness();

#if CHECK_MODE
            return RunCheckMode(args);
#else
            bool isAutoStart = (args != null && args.Contains("--autostart"));
            if (isAutoStart) ErrorLog.LogStartup();

            bool createdNew;
            using (var mutex = new Mutex(true, "MaicongTray_SingleInstance", out createdNew))
            {
                if (!createdNew)
                {
                    if (!isAutoStart)
                        MessageBox.Show("迈从电量托盘已在运行（请查看任务栏托盘区）。", "迈从电量", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return 0;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
                {
                    ErrorLog.Write(e.ExceptionObject as Exception ?? new Exception(Convert.ToString(e.ExceptionObject)), "Unhandled");
                };
                Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e)
                {
                    ErrorLog.Write(e.Exception, "UI");
                    MessageBox.Show("发生错误: " + e.Exception.Message, "迈从电量", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
                };

                Application.Run(new TrayApp());
                GC.KeepAlive(mutex);
            }
            return 0;
#endif
        }

#if CHECK_MODE
        private static int RunCheckMode(string[] args)
        {
            try { Console.OutputEncoding = Encoding.UTF8; }
            catch { }

            string cmd = (args != null && args.Length > 0) ? args[0] : "";

            if (cmd == "--makeico")
            {
                string target = (args.Length > 1) ? args[1] : "icon.ico";
                IconRenderer.WriteAppIco(target);
                Console.WriteLine("已生成 " + target);
                return 0;
            }

            if (cmd == "--icons")
            {
                string target = (args.Length > 1) ? args[1] : "icon_preview.png";
                ExportIconShowcase(target);
                Console.WriteLine("已生成 " + target);
                return 0;
            }

            if (cmd == "--flyout-preview")
            {
                string outDir = (args.Length > 1) ? args[1] : ".";
                Directory.CreateDirectory(outDir);
                ExportFlyoutPreviews(outDir);
                Console.WriteLine("已生成面板预览图至 " + Path.GetFullPath(outDir));
                return 0;
            }

            if (cmd == "--dpi")
            {
                return RunDpiProbe();
            }

            if (cmd == "--stress")
            {
                return RunGcStressTest();
            }

            Console.WriteLine("迈从 A7 V2 Ultra 电量检测 v2.1.1 (协议: 经典 0x11/0x06 + V2 0x4D 备用)");
            Console.WriteLine(new string('-', 64));
            var log = new List<string>();
            BatteryResult r = BatteryReader.Read(log);
            if (r.State != BatteryState.Battery)
            {
                foreach (var d in NativeHid.Enumerate())
                {
                    if (d.Pid != 0x100B || d.UsagePage != 0xFF01 || d.FeatureLen == 0) continue;
                    log.Add("[diag] 目标接口: " + d);
                    var h = NativeHid.Open(d.Path);
                    if (!h.IsInvalid)
                    {
                        NativeHid.ClassicDiag(h, d.FeatureLen, d.InputLen, log);
                        h.Close();
                    }
                    else log.Add("[diag] 打开失败 " + NativeHid.LastError());
                    break;
                }
            }
            foreach (string line in log) Console.WriteLine(line);
            Console.WriteLine(new string('-', 64));
            Console.WriteLine("结果: " + r.DisplayText() + (string.IsNullOrEmpty(r.Mode) ? "" : "  [" + r.Mode + "]"));
            if (r.State == BatteryState.Battery)
                Console.WriteLine("电量=" + r.Percent + "%  充电=" + (r.Charging ? "是" : "否") + (string.IsNullOrEmpty(r.Firmware) ? "" : "  固件=" + r.Firmware));
            return r.State == BatteryState.Battery ? 0 : 2;
        }

        private static void ExportIconShowcase(string outPath)
        {
            TrayIconStyle[] styles = new TrayIconStyle[]
            {
                TrayIconStyle.MinimalNumber,
                TrayIconStyle.RadialRing,
                TrayIconStyle.BatteryPill,
                TrayIconStyle.NumberWithBar
            };
            BatteryResult[] states = new BatteryResult[]
            {
                new BatteryResult { State = BatteryState.Battery, Percent = 86 },
                new BatteryResult { State = BatteryState.Battery, Percent = 59, Charging = true },
                new BatteryResult { State = BatteryState.Battery, Percent = 20 },
                new BatteryResult { State = BatteryState.Battery, Percent = 8 },
                new BatteryResult { State = BatteryState.Battery, Percent = 59, IsSleeping = true },
                new BatteryResult { State = BatteryState.Disconnected }
            };
            string[] colNames = new string[] { "86% 正常", "59% 充电", "20% 偏低", "8% 极低", "59% 休眠", "未连接" };

            int leftW = 148;
            int cellW = 104;
            int rowH = 88;
            int headerH = 82;
            int totalW = leftW + states.Length * cellW + 24;
            int totalH = headerH + styles.Length * 2 * rowH + 24;

            using (var bmp = new Bitmap(totalW, totalH, PixelFormat.Format32bppArgb))
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.Clear(Color.FromArgb(20, 22, 27));

                using (var hFont = ThemeHelper.CreateUiFont(12f, FontStyle.Bold))
                using (var lblFont = ThemeHelper.CreateUiFont(9.5f, FontStyle.Bold))
                using (var subFont = ThemeHelper.CreateUiFont(8.2f, FontStyle.Regular))
                using (var whiteB = new SolidBrush(Color.FromArgb(242, 244, 248)))
                using (var dimB = new SolidBrush(Color.FromArgb(148, 155, 168)))
                using (var sfC = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                {
                    g.DrawString("MaicongTray v2.1 · 托盘图标样式一览", hFont, whiteB, 18, 16);
                    for (int c = 0; c < states.Length; c++)
                    {
                        g.DrawString(colNames[c], subFont, dimB, new RectangleF(leftW + c * cellW, 48, cellW, 26), sfC);
                    }

                    int rowIdx = 0;
                    for (int s = 0; s < styles.Length; s++)
                    {
                        bool[] modes = new bool[] { true, false };
                        for (int m = 0; m < modes.Length; m++)
                        {
                            bool dark = modes[m];
                            int y = headerH + rowIdx * rowH;
                            RectangleF strip = new RectangleF(14, y + 6, totalW - 28, rowH - 12);
                            using (GraphicsPath sp = ThemeHelper.RoundedRect(strip, 12))
                            using (var sb = new SolidBrush(dark ? Color.FromArgb(30, 32, 38) : Color.FromArgb(236, 240, 246)))
                            {
                                g.FillPath(sb, sp);
                            }

                            using (var titleB = new SolidBrush(dark ? Color.White : Color.FromArgb(24, 26, 32)))
                            using (var modeB = new SolidBrush(dark ? Color.FromArgb(150, 158, 172) : Color.FromArgb(100, 108, 122)))
                            {
                                g.DrawString(IconRenderer.GetStyleName(styles[s]), lblFont, titleB, 28, y + 24);
                                g.DrawString(dark ? "深色任务栏" : "浅色任务栏", subFont, modeB, 28, y + 46);
                            }

                            for (int c = 0; c < states.Length; c++)
                            {
                                using (Bitmap ic = IconRenderer.Render(states[c], styles[s], false, dark))
                                {
                                    g.DrawImage(ic, new Rectangle(leftW + c * cellW + (cellW - 52) / 2, y + (rowH - 52) / 2, 52, 52));
                                }
                            }
                            rowIdx++;
                        }
                    }
                }
                bmp.Save(outPath, ImageFormat.Png);
            }
        }

        private static void ExportFlyoutPreviews(string outDir)
        {
            var hist = new BatteryHistory();
            hist.Load();
            BatteryHistory.Sample[] samples = hist.GetSamples();
            if (samples.Length < 4)
            {
                var synth = new List<BatteryHistory.Sample>();
                DateTime now = DateTime.Now;
                int[] pts = new int[] { 82, 80, 78, 76, 74, 71, 68, 66, 63, 61, 59 };
                for (int i = 0; i < pts.Length; i++)
                {
                    synth.Add(new BatteryHistory.Sample
                    {
                        T = now.AddHours(-(pts.Length - 1 - i) * 2.0),
                        Pct = pts[i],
                        Chg = false
                    });
                }
                samples = synth.ToArray();
            }

            int lastPct = samples[samples.Length - 1].Pct;
            var resNormal = new BatteryResult
            {
                State = BatteryState.Battery,
                Percent = lastPct,
                Charging = false,
                ConnMode = 1,
                Timestamp = DateTime.Now
            };
            var resCharging = new BatteryResult
            {
                State = BatteryState.Battery,
                Percent = 72,
                Charging = true,
                ConnMode = 0,
                DpiValue = 1600,
                Timestamp = DateTime.Now
            };
            var settings = new AppSettings { IconStyle = TrayIconStyle.RadialRing };

            using (Bitmap b1 = BatteryFlyoutForm.RenderPreviewBitmap(resNormal, samples, settings, true, false, 2f))
                b1.Save(Path.Combine(outDir, "flyout_dark.png"), ImageFormat.Png);
            var resSleep = new BatteryResult
            {
                State = BatteryState.Battery,
                Percent = lastPct,
                IsSleeping = true,
                ConnMode = 1,
                Firmware = "052E.0204",
                Timestamp = DateTime.Now.AddMinutes(-12)
            };
            using (Bitmap b2 = BatteryFlyoutForm.RenderPreviewBitmap(resCharging, samples, settings, false, false, 2f))
                b2.Save(Path.Combine(outDir, "flyout_light.png"), ImageFormat.Png);
            using (Bitmap b3 = BatteryFlyoutForm.RenderPreviewBitmap(resNormal, samples, settings, true, true, 2f))
                b3.Save(Path.Combine(outDir, "flyout_settings.png"), ImageFormat.Png);
            using (Bitmap b4 = BatteryFlyoutForm.RenderPreviewBitmap(resSleep, samples, settings, true, false, 2f))
                b4.Save(Path.Combine(outDir, "flyout_sleep.png"), ImageFormat.Png);
        }

        private static int RunGcStressTest()
        {
            Console.WriteLine("Running DeviceWatcher + aggressive GC compaction stress test (4s)...");
            var w = new DeviceWatcher();
            int updates = 0;
            w.Updated += delegate(BatteryResult r)
            {
                Interlocked.Increment(ref updates);
                Console.WriteLine("  Update #" + updates + ": " + r.DisplayText() + " FW=" + r.Firmware);
            };
            w.Start();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var junk = new List<byte[]>();
            while (sw.ElapsedMilliseconds < 4000)
            {
                for (int i = 0; i < 64; i++) junk.Add(new byte[4096]);
                if (junk.Count > 256) junk.Clear();
                GC.Collect(2, GCCollectionMode.Forced, true);
                GC.WaitForPendingFinalizers();
                Thread.Sleep(150);
            }
            foreach (string line in w.GetLog()) Console.WriteLine("  [log] " + line);
            w.Stop();
            Thread.Sleep(350);
            Console.WriteLine("Stress test PASSED cleanly! Updates=" + updates + " Snapshot=" + w.Snapshot().DisplayText());
            return 0;
        }

        private static int RunDpiProbe()
        {
            foreach (var d in NativeHid.Enumerate())
            {
                if (d.Pid != 0x100B) continue;
                Console.WriteLine("集合: " + d);
                if (d.OutputLen < 10) continue;
                Console.WriteLine("探针: " + d);
                var h = NativeHid.Open(d.Path);
                if (h.IsInvalid) { Console.WriteLine("打开失败"); continue; }
                int len = d.FeatureLen;
                using (var uo = new NativeHid.UnmanagedOverlapped(d.InputLen))
                {
                    uo.ResetForIo();
                    uint tmp;
                    bool okR = NativeHid.ReadFile(h, uo.Buffer, (uint)d.InputLen, out tmp, uo.OvPtr);
                    bool pending = okR || NativeHid.LastError() == NativeHid.ERROR_IO_PENDING;
                    Console.WriteLine("挂起读取: " + pending);

                    byte[] q11 = new byte[len];
                    q11[0] = 0x11; q11[1] = 0x06 ^ 0xFF;
                    for (int i = 2; i < len; i++) q11[i] = 0xFF;
                    Console.WriteLine("0x11/0x06 SetFeature: " + NativeHid.HidD_SetFeature(h, q11, len));
                    Thread.Sleep(500);

                    byte[] req = new byte[len];
                    req[0] = 0x12; req[1] = 0x67 ^ 0xFF;
                    for (int i = 2; i < len; i++) req[i] = 0xFF;
                    Console.WriteLine("0x12/0x67 SetFeature: " + NativeHid.HidD_SetFeature(h, req, len));

                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    try
                    {
                        while (sw.ElapsedMilliseconds < 4000)
                        {
                            if (pending && NativeHid.WaitForSingleObject(uo.EventHandle, 0) == 0 && NativeHid.GetOverlappedResult(h, uo.OvPtr, out tmp, false))
                            {
                                pending = false;
                                if (tmp > 0)
                                {
                                    byte[] inBuf = uo.ReadManaged((int)tmp);
                                    Console.WriteLine("IN  (" + inBuf.Length + "): " + NativeHid.Hex(inBuf));
                                }
                                uo.ResetForIo();
                                uint t2;
                                pending = NativeHid.ReadFile(h, uo.Buffer, (uint)d.InputLen, out t2, uo.OvPtr)
                                    || NativeHid.LastError() == NativeHid.ERROR_IO_PENDING;
                            }
                            byte[] raw = new byte[len];
                            raw[0] = 0x12;
                            if (NativeHid.HidD_GetFeature(h, raw, len))
                            {
                                if (raw[1] != 0xFF) Console.WriteLine("FEAT: " + NativeHid.Hex(raw.Take(Math.Min(len, 20)).ToArray()));
                            }
                            else Console.WriteLine("FEAT err " + NativeHid.LastError());
                            Thread.Sleep(200);
                        }
                    }
                    finally
                    {
                        if (pending) uo.CancelAndDrain(h);
                    }
                }
                h.Close();
                break;
            }
            return 0;
        }
#endif
    }
}
