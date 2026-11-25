using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace MaicongTray
{
    internal class TrayApp : ApplicationContext
    {
        private NotifyIcon _icon;
        private System.Windows.Forms.Timer _timer;
        private System.Windows.Forms.Timer _chargeTimer;
        private System.Threading.Timer _resumeTimer;
        private System.Threading.Timer _updateTimer;

        private HiddenForm _hidden;
        private DeviceWatcher _watcher;
        private BatteryFlyoutForm _flyout;
        private AppSettings _settings;

        private BatteryResult _last = new BatteryResult { State = BatteryState.Unavailable, Message = "读取中…" };
        private bool _querying;
        private bool _lowNotified;
        private bool _fullNotified;
        private bool _deferredLowNotify;
        private bool _deferredFullNotify;
        private bool _boltAlt;

        private IntPtr _devNotify = IntPtr.Zero;
        private IntPtr _devFilter = IntPtr.Zero;
        private DateTime _lastArrivalQuery = DateTime.MinValue;
        private DateTime _lastResumeEvent = DateTime.MinValue;

        internal TrayApp()
        {
            _settings = SettingsStore.Load();
            _hidden = new HiddenForm();
            _watcher = new DeviceWatcher();
            _watcher.Updated += OnWatcherUpdated;
            _watcher.Start();

            _flyout = new BatteryFlyoutForm();
            WireFlyoutCallbacks();

            _resumeTimer = new System.Threading.Timer(delegate
            {
                SafeUiInvoke(delegate { QueryAsync(true); });
            });

            _updateTimer = new System.Threading.Timer(delegate
            {
                SafeUiInvoke(delegate { CheckForUpdate(false); });
            }, null, 5000, 86400000);

            _icon = new NotifyIcon();
            _icon.Icon = DrawIcon(_last, false);
            _icon.Text = "迈从 A7 V2 Ultra — 读取中…";
            _icon.Visible = true;
            _icon.MouseUp += delegate(object s, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left || e.Button == MouseButtons.Right)
                {
                    ToggleFlyout();
                }
            };

            _timer = new System.Windows.Forms.Timer();
            _timer.Tick += delegate
            {
                FlushDeferredNotificationsIfSafe();
                QueryAsync(false);
            };

            _chargeTimer = new System.Windows.Forms.Timer();
            _chargeTimer.Interval = 700;
            _chargeTimer.Tick += delegate
            {
                if (_last != null && _last.State == BatteryState.Battery && _last.Charging && !_last.IsSleeping)
                {
                    _boltAlt = !_boltAlt;
                    ReplaceIcon(DrawIcon(_last, _boltAlt));
                }
                else
                {
                    _chargeTimer.Stop();
                }
            };

            SetInterval(_settings.IntervalSec, false);
            QueryAsync(false);

            _hidden.DeviceArrival = OnDeviceArrival;
            _hidden.TaskbarCreated = OnTaskbarCreated;
            _hidden.PowerResumed = OnPowerResumed;
            _hidden.ThemeChanged = OnThemeChanged;
            RegisterHidNotifications();
        }

        private void WireFlyoutCallbacks()
        {
            _flyout.OnRefreshClick = delegate
            {
                QueryAsync(true);
                _flyout.ShowToast("正在刷新设备电量…");
            };
            _flyout.OnIconStyleChanged = delegate(TrayIconStyle st)
            {
                SetIconStyle(st);
                _flyout.ShowToast("已切换：" + IconRenderer.GetStyleName(st));
            };
            _flyout.OnChartRangeChanged = delegate(int hours)
            {
                _settings.ChartRangeHours = hours;
                SettingsStore.Save(_settings);
                SyncFlyoutData();
            };
            _flyout.OnIntervalChanged = delegate(int sec)
            {
                SetInterval(sec, true);
                _flyout.ShowToast("刷新间隔已更新");
            };
            _flyout.OnLowNotifyChanged = delegate(bool on)
            {
                _settings.LowNotify = on;
                SettingsStore.Save(_settings);
            };
            _flyout.OnLowThresholdChanged = delegate(int th)
            {
                _settings.LowThreshold = th;
                SettingsStore.Save(_settings);
            };
            _flyout.OnFullNotifyChanged = delegate(bool on)
            {
                _settings.FullNotify = on;
                SettingsStore.Save(_settings);
            };
            _flyout.OnFullscreenDndChanged = delegate(bool on)
            {
                _settings.FullscreenDnd = on;
                SettingsStore.Save(_settings);
                _flyout.ShowToast(on ? "已开启全屏免打扰" : "已关闭全屏免打扰");
            };
            _flyout.OnAutoStartChanged = delegate(bool on)
            {
                try
                {
                    SettingsStore.SetAutoStart(on);
                    _flyout.ShowToast(on ? "已开启开机自启" : "已关闭开机自启");
                }
                catch (Exception ex)
                {
                    MessageBox.Show(_hidden, "设置开机自启失败：" + ex.Message, "迈从电量", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
                }
                SyncFlyoutData();
            };
            _flyout.OnCopyDebugClick = delegate { CopyDebug(); };
            _flyout.OnOpenDataDirClick = delegate { OpenDataFolder(); };
            _flyout.OnInstallStableClick = delegate { InstallStableCopy(); };
            _flyout.OnCheckUpdateClick = delegate { CheckForUpdate(true); };
            _flyout.OnExitClick = delegate { ExitApp(); };
        }

        private void ToggleFlyout()
        {
            if (_flyout.Visible)
            {
                _flyout.HideFlyout();
            }
            else if (!_flyout.WasJustDeactivated())
            {
                ShowFlyout();
            }
        }

        private void ShowFlyout()
        {
            SyncFlyoutData();
            Rectangle? trayRect = TryGetTrayIconRect();
            _flyout.ShowAtTray(trayRect);
        }

        private void SyncFlyoutData()
        {
            bool hasEta = false;
            string etaText = null;
            double rate = 0.0;
            if (_last != null && _last.State == BatteryState.Battery && !_last.IsSleeping && !(_last.Charging && _last.Percent >= 100))
            {
                TimeSpan eta;
                if (_watcher.History.Estimate(_last.Percent, _last.Charging, out eta, out rate))
                {
                    hasEta = true;
                    etaText = BatteryHistory.FormatEta(eta);
                }
            }

            string lastFullText = null;
            DateTime? fullAt = _watcher.History.GetLastFullChargeTime();
            if (fullAt.HasValue)
            {
                TimeSpan diff = DateTime.Now - fullAt.Value;
                if (diff.TotalSeconds >= 0)
                    lastFullText = BatteryHistory.FormatElapsed(diff);
            }

            _flyout.UpdateData(
                _last,
                _watcher.History.GetSamples(),
                _settings,
                _watcher.DeviceName,
                _watcher.Firmware,
                hasEta,
                etaText,
                rate,
                lastFullText,
                SettingsStore.IsAutoStartEnabled(),
                _querying);
        }

        private void OnWatcherUpdated(BatteryResult r)
        {
            SafeUiInvoke(delegate { SetStatus(r); });
        }

        private void QueryAsync(bool manual)
        {
            if (_querying) return;
            _querying = true;
            if (_flyout.Visible) SyncFlyoutData();

            DateTime before = _watcher.LatestAt;
            if (manual) _watcher.RequestDpiRefresh();
            else _watcher.RequestQuery(false);

            ThreadPool.QueueUserWorkItem(delegate
            {
                BatteryResult r = null;
                for (int i = 0; i < 10; i++)
                {
                    Thread.Sleep(250);
                    r = _watcher.Snapshot();
                    if (_watcher.LatestAt > before) break;
                }
                if (r == null) r = _watcher.Snapshot();
                BatteryResult captured = r;
                SafeUiInvoke(delegate
                {
                    _querying = false;
                    SetStatus(captured);
                });
            });
        }

        private void SetStatus(BatteryResult r)
        {
            if (r == null) return;
            _last = r;

            string text = r.DisplayText();
            string eta = EstimateTooltipText(r);
            if (!string.IsNullOrEmpty(eta))
                text = text + " ｜ " + eta;

            string tip = _watcher.DeviceName + " — " + text;
            if (tip.Length > 63) tip = tip.Substring(0, 63);
            _icon.Text = tip;

            bool chargingNow = r.State == BatteryState.Battery && r.Charging && !r.IsSleeping;
            if (!chargingNow) _boltAlt = false;

            ReplaceIcon(DrawIcon(r, _boltAlt));
            if (chargingNow) _chargeTimer.Start();
            else _chargeTimer.Stop();

            SyncFlyoutData();
            HandleNotifications(r);
        }

        private void HandleNotifications(BatteryResult r)
        {
            bool suppressedByGame = _settings.FullscreenDnd && FullscreenDetector.IsFullscreenActive();

            if (_settings.LowNotify)
            {
                if (r.State == BatteryState.Battery && !r.IsSleeping && r.Percent <= _settings.LowThreshold && !r.Charging)
                {
                    if (!_lowNotified)
                    {
                        if (suppressedByGame)
                        {
                            _deferredLowNotify = true;
                        }
                        else
                        {
                            _lowNotified = true;
                            _deferredLowNotify = false;
                            ShowLowBatteryTip(r.Percent);
                        }
                    }
                }
                else if (r.State != BatteryState.Battery || r.Percent > _settings.LowThreshold + 5 || r.Charging)
                {
                    _lowNotified = false;
                    _deferredLowNotify = false;
                }
            }

            if (_settings.FullNotify)
            {
                if (r.State == BatteryState.Battery && !r.IsSleeping && r.Charging && r.Percent >= 100)
                {
                    if (!_fullNotified)
                    {
                        if (suppressedByGame)
                        {
                            _deferredFullNotify = true;
                        }
                        else
                        {
                            _fullNotified = true;
                            _deferredFullNotify = false;
                            ShowFullBatteryTip();
                        }
                    }
                }
                else if (r.State != BatteryState.Battery || !r.Charging || r.Percent < 100)
                {
                    _fullNotified = false;
                    _deferredFullNotify = false;
                }
            }
        }

        private void FlushDeferredNotificationsIfSafe()
        {
            if (!_deferredLowNotify && !_deferredFullNotify) return;
            if (_settings.FullscreenDnd && FullscreenDetector.IsFullscreenActive()) return;

            if (_deferredLowNotify && _last != null && _last.State == BatteryState.Battery && _last.Percent <= _settings.LowThreshold && !_last.Charging)
            {
                _deferredLowNotify = false;
                _lowNotified = true;
                ShowLowBatteryTip(_last.Percent);
            }
            if (_deferredFullNotify && _last != null && _last.State == BatteryState.Battery && _last.Charging && _last.Percent >= 100)
            {
                _deferredFullNotify = false;
                _fullNotified = true;
                ShowFullBatteryTip();
            }
        }

        private void ShowLowBatteryTip(int percent)
        {
            _icon.ShowBalloonTip(
                5000,
                "迈从鼠标电量低",
                "当前电量 " + percent + "%（提醒阈值 " + _settings.LowThreshold + "%），请及时充电或连接 USB 线。",
                ToolTipIcon.Warning);
        }

        private void ShowFullBatteryTip()
        {
            _icon.ShowBalloonTip(
                5000,
                "迈从鼠标已充满",
                "电量已达 100%，可以取下充电线了。",
                ToolTipIcon.Info);
        }

        private string EstimateTooltipText(BatteryResult r)
        {
            if (r == null || r.State != BatteryState.Battery || r.IsSleeping) return null;
            if (r.Charging && r.Percent >= 100) return null;
            TimeSpan eta;
            double rate;
            if (!_watcher.History.Estimate(r.Percent, r.Charging, out eta, out rate)) return null;
            return (r.Charging ? "充满还需" : "还能用") + BatteryHistory.FormatEta(eta);
        }

        private void SetIconStyle(TrayIconStyle style)
        {
            _settings.IconStyle = style;
            SettingsStore.Save(_settings);
            ReplaceIcon(DrawIcon(_last, _boltAlt));
            SyncFlyoutData();
        }

        private void SetInterval(int seconds, bool save)
        {
            _settings.IntervalSec = seconds;
            _timer.Stop();
            _timer.Interval = Math.Max(5, seconds) * 1000;
            _timer.Start();
            if (save) SettingsStore.Save(_settings);
        }

        private void InstallStableCopy()
        {
            try
            {
                string dst = SettingsStore.InstallStableCopy();
                SyncFlyoutData();
                MessageBox.Show(_hidden, "已安装/修复到：" + dst + "\n开机自启已指向稳定副本。", "迈从电量", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(_hidden, "安装失败：" + ex.Message, "迈从电量", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
            }
        }

        private void OpenDataFolder()
        {
            try
            {
                string dir = BatteryHistory.DataDirectory;
                Directory.CreateDirectory(dir);
                Process.Start("explorer.exe", dir);
            }
            catch { }
        }

        private void CopyDebug()
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== " + _watcher.DeviceName + " 托盘电量 v" + Assembly.GetExecutingAssembly().GetName().Version.ToString(3) + " - 调试信息 ===");
            foreach (string line in _watcher.GetLog()) sb.AppendLine(line);
            sb.AppendLine("状态: " + _last.DisplayText());
            if (!string.IsNullOrEmpty(_watcher.Firmware))
                sb.AppendLine("固件: FW " + _watcher.Firmware);
            sb.AppendLine("图标样式: " + IconRenderer.GetStyleName(_settings.IconStyle));
            sb.AppendLine("全屏免打扰: " + (_settings.FullscreenDnd ? "开启" : "关闭"));
            sb.AppendLine("历史样本: " + _watcher.History.SampleCount + " 条（7天滚动 · %APPDATA%\\MaicongTray\\history.csv）");
            try
            {
                Clipboard.SetText(sb.ToString());
                _flyout.ShowToast("调试信息已复制");
            }
            catch (Exception ex)
            {
                MessageBox.Show(_hidden, "复制失败: " + ex.Message, "迈从电量", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
            }
        }

        private void CheckForUpdate(bool manual)
        {
            if (manual && _flyout.Visible) _flyout.ShowToast("正在检查更新…");
            ThreadPool.QueueUserWorkItem(delegate
            {
                UpdateInfo info = UpdateChecker.Fetch();
                SafeUiInvoke(delegate { HandleUpdateResult(info, manual); });
            });
        }

        private void HandleUpdateResult(UpdateInfo info, bool manual)
        {
            if (info == null) return;
            if (!string.IsNullOrEmpty(info.Error))
            {
                if (manual)
                    MessageBox.Show(_hidden, "检查更新失败：" + info.Error, "迈从电量", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
                return;
            }
            Version cur = Assembly.GetExecutingAssembly().GetName().Version;
            if (info.Version <= cur)
            {
                if (manual)
                {
                    if (_flyout.Visible) _flyout.ShowToast("已是最新版本 v" + cur.ToString(3));
                    else MessageBox.Show(_hidden, "当前已是最新版本（v" + cur.ToString(3) + "）。", "迈从电量", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                return;
            }
            string msg = "发现新版本 v" + info.Version.ToString(3);
            if (!string.IsNullOrEmpty(info.Notes)) msg += "\n" + info.Notes;
            if (manual)
            {
                if (MessageBox.Show(_hidden, msg + "\n\n是否打开下载页面？", "迈从电量", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                    UpdateChecker.OpenUrl(info.Url);
            }
            else if (!(_settings.FullscreenDnd && FullscreenDetector.IsFullscreenActive()))
            {
                _icon.ShowBalloonTip(8000, "迈从电量托盘有新版本", msg, ToolTipIcon.Info);
            }
        }

        // Precision tray icon rect via Shell_NotifyIconGetRect (fallback to null -> cursor position).
        private Rectangle? TryGetTrayIconRect()
        {
            try
            {
                FieldInfo winField = typeof(NotifyIcon).GetField("window", BindingFlags.NonPublic | BindingFlags.Instance);
                FieldInfo idField = typeof(NotifyIcon).GetField("id", BindingFlags.NonPublic | BindingFlags.Instance);
                if (winField == null || idField == null) return null;

                var nw = winField.GetValue(_icon) as NativeWindow;
                if (nw == null || nw.Handle == IntPtr.Zero) return null;
                int id = (int)idField.GetValue(_icon);

                var nii = new NOTIFYICONIDENTIFIER();
                nii.cbSize = (uint)Marshal.SizeOf(typeof(NOTIFYICONIDENTIFIER));
                nii.hWnd = nw.Handle;
                nii.uID = (uint)id;
                nii.guidItem = Guid.Empty;

                RECT rc;
                if (Shell_NotifyIconGetRect(ref nii, out rc) == 0 && rc.Right > rc.Left && rc.Bottom > rc.Top)
                {
                    return new Rectangle(rc.Left, rc.Top, rc.Right - rc.Left, rc.Bottom - rc.Top);
                }
            }
            catch { }
            return null;
        }

        private Icon DrawIcon(BatteryResult r, bool boltAlt)
        {
            using (Bitmap bmp = IconRenderer.Render(r, _settings.IconStyle, boltAlt))
            {
                return Icon.FromHandle(bmp.GetHicon());
            }
        }

        private void ReplaceIcon(Icon next)
        {
            Icon old = _icon.Icon;
            _icon.Icon = next;
            if (old != null)
            {
                DestroyIcon(old.Handle);
                old.Dispose();
            }
        }

        private void SafeUiInvoke(Action action)
        {
            try
            {
                if (_hidden != null && !_hidden.IsDisposed && _hidden.IsHandleCreated)
                    _hidden.BeginInvoke((MethodInvoker)delegate { action(); });
            }
            catch { }
        }

        private void OnDeviceArrival()
        {
            DateTime now = DateTime.Now;
            if ((now - _lastArrivalQuery).TotalSeconds < 2.0) return;
            _lastArrivalQuery = now;
            QueryAsync(true);
        }

        private void OnTaskbarCreated()
        {
            _icon.Visible = false;
            _icon.Visible = true;
            ReplaceIcon(DrawIcon(_last, _boltAlt));
        }

        private void OnPowerResumed()
        {
            DateTime now = DateTime.Now;
            if ((now - _lastResumeEvent).TotalSeconds < 5.0) return;
            _lastResumeEvent = now;
            _resumeTimer.Change(1500, Timeout.Infinite);
        }

        private void OnThemeChanged()
        {
            ReplaceIcon(DrawIcon(_last, _boltAlt));
            if (_flyout.Visible) _flyout.ShowAtTray(TryGetTrayIconRect());
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left, Top, Right, Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NOTIFYICONIDENTIFIER
        {
            public uint cbSize;
            public IntPtr hWnd;
            public uint uID;
            public Guid guidItem;
        }

        [DllImport("shell32.dll")]
        private static extern int Shell_NotifyIconGetRect(ref NOTIFYICONIDENTIFIER identifier, out RECT iconLocation);

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr h);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern uint RegisterWindowMessage(string message);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr RegisterDeviceNotification(IntPtr recipient, IntPtr filter, int flags);

        [DllImport("user32.dll")]
        private static extern bool UnregisterDeviceNotification(IntPtr handle);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private class DEV_BROADCAST_DEVICEINTERFACE
        {
            public int dbcc_size;
            public int dbcc_devicetype;
            public int dbcc_reserved;
            public Guid dbcc_classguid;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string dbcc_name;
        }

        private void RegisterHidNotifications()
        {
            try
            {
                var dev = new DEV_BROADCAST_DEVICEINTERFACE();
                dev.dbcc_devicetype = 5;
                dev.dbcc_classguid = new Guid("4d1e55b2-f16f-11cf-88cb-001111000030");
                dev.dbcc_size = Marshal.SizeOf(dev);
                _devFilter = Marshal.AllocHGlobal(dev.dbcc_size);
                Marshal.StructureToPtr(dev, _devFilter, false);
                _devNotify = RegisterDeviceNotification(_hidden.Handle, _devFilter, 0);
            }
            catch (Exception ex) { ErrorLog.Write(ex, "RegisterDeviceNotification"); }
        }

        private void ExitApp()
        {
            if (_devNotify != IntPtr.Zero) { try { UnregisterDeviceNotification(_devNotify); } catch { } }
            if (_devFilter != IntPtr.Zero) { try { Marshal.FreeHGlobal(_devFilter); } catch { } }
            _devNotify = IntPtr.Zero;
            _devFilter = IntPtr.Zero;

            if (_resumeTimer != null) { try { _resumeTimer.Dispose(); } catch { } }
            if (_updateTimer != null) { try { _updateTimer.Dispose(); } catch { } }

            _watcher.Stop();
            _watcher.History.Save();
            _flyout.Close();
            _icon.Visible = false;
            _icon.Dispose();
            _timer.Stop();
            _chargeTimer.Stop();
            _chargeTimer.Dispose();
            _hidden.Close();
            Environment.Exit(0);
        }

        private class HiddenForm : Form
        {
            private const int WM_SETTINGCHANGE = 0x001A;
            private const int WM_DWMCOLORIZATIONCOLORCHANGED = 0x0320;
            private const int WM_POWERBROADCAST = 0x0218;
            private const int WM_DEVICECHANGE = 0x0219;
            private const int DBT_DEVICEARRIVAL = 0x8000;
            private const int DBT_DEVTYP_DEVICEINTERFACE = 5;
            private const int PBT_APMRESUMESUSPEND = 0x0007;
            private const int PBT_APMRESUMEAUTOMATIC = 0x0012;

            public Action DeviceArrival;
            public Action TaskbarCreated;
            public Action PowerResumed;
            public Action ThemeChanged;

            private static readonly int TaskbarCreatedMessage = (int)RegisterWindowMessage("TaskbarCreated");

            public HiddenForm()
            {
                ShowInTaskbar = false;
                WindowState = FormWindowState.Minimized;
                FormBorderStyle = FormBorderStyle.None;
                Opacity = 0.0;
                CreateHandle();
            }

            protected override void WndProc(ref Message m)
            {
                if (m.Msg == WM_DEVICECHANGE && m.WParam.ToInt64() == DBT_DEVICEARRIVAL
                    && DeviceArrival != null && m.LParam != IntPtr.Zero)
                {
                    var hdr = (DEV_BROADCAST_DEVICEINTERFACE)Marshal.PtrToStructure(m.LParam, typeof(DEV_BROADCAST_DEVICEINTERFACE));
                    if (hdr.dbcc_devicetype == DBT_DEVTYP_DEVICEINTERFACE) DeviceArrival();
                }
                else if (m.Msg == TaskbarCreatedMessage && TaskbarCreated != null)
                {
                    TaskbarCreated();
                }
                else if (m.Msg == WM_POWERBROADCAST && PowerResumed != null)
                {
                    int ev = (int)(m.WParam.ToInt64() & 0xFFFF);
                    if (ev == PBT_APMRESUMEAUTOMATIC || ev == PBT_APMRESUMESUSPEND) PowerResumed();
                }
                else if ((m.Msg == WM_SETTINGCHANGE || m.Msg == WM_DWMCOLORIZATIONCOLORCHANGED) && ThemeChanged != null)
                {
                    if (m.Msg == WM_DWMCOLORIZATIONCOLORCHANGED)
                    {
                        ThemeChanged();
                    }
                    else
                    {
                        string area = m.LParam != IntPtr.Zero ? Marshal.PtrToStringAuto(m.LParam) : null;
                        if (string.IsNullOrEmpty(area) || string.Equals(area, "ImmersiveColorSet", StringComparison.OrdinalIgnoreCase))
                            ThemeChanged();
                    }
                }
                base.WndProc(ref m);
            }
        }
    }
}
