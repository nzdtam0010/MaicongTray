using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace MaicongTray
{
    // Persistent vendor-channel watcher: keeps a single overlapped input read
    // pending without churning CancelIoEx on every tick, caches E2 pushes,
    // and triggers live callbacks when battery state updates.
    internal class DeviceWatcher
    {
        private Thread _thread;
        private volatile bool _stop;
        private readonly AutoResetEvent _queryNow = new AutoResetEvent(false);
        private BatteryResult _latest = new BatteryResult { State = BatteryState.Unavailable, Message = "等待首次读取" };
        private BatteryResult _lastKnownBattery;
        private DateTime _latestAt = DateTime.MinValue;
        private readonly object _lock = new object();
        private List<string> _lastLog = new List<string>();
        private int _dpiValue = -1;
        private string _firmware = "";
        private volatile bool _dpiRefresh;
        private volatile bool _manualQuery;
        private string _deviceName = "迈从 A7 V2 Ultra";

        internal readonly BatteryHistory History = new BatteryHistory();
        internal event Action<BatteryResult> Updated;

        internal DateTime LatestAt
        {
            get { lock (_lock) return _latestAt; }
        }

        internal string DeviceName
        {
            get { lock (_lock) return _deviceName; }
        }

        internal string Firmware
        {
            get { lock (_lock) return _firmware; }
        }

        internal void Start()
        {
            History.Load();
            _thread = new Thread(Run);
            _thread.IsBackground = true;
            _thread.Name = "MaicongHidWatcher";
            _thread.Start();
        }

        internal void Stop()
        {
            _stop = true;
            _queryNow.Set();
        }

        internal void RequestQuery()
        {
            RequestQuery(false);
        }

        internal void RequestQuery(bool manual)
        {
            if (manual) _manualQuery = true;
            _queryNow.Set();
        }

        internal void RequestDpiRefresh()
        {
            _dpiRefresh = true;
            _manualQuery = true;
            _queryNow.Set();
        }

        internal BatteryResult Snapshot()
        {
            lock (_lock)
            {
                BatteryResult r = _latest;
                if (r.State == BatteryState.Battery && (DateTime.Now - _latestAt).TotalSeconds > 45.0)
                {
                    if (_lastKnownBattery != null)
                        return MakeSleepResult(_lastKnownBattery);
                    return new BatteryResult
                    {
                        State = BatteryState.Unavailable,
                        Message = "链路无响应",
                        Firmware = _firmware,
                        Timestamp = DateTime.Now
                    };
                }
                if (r.State == BatteryState.Unavailable && _lastKnownBattery != null)
                {
                    return MakeSleepResult(_lastKnownBattery);
                }
                return r.Clone();
            }
        }

        private BatteryResult MakeSleepResult(BatteryResult lastValid)
        {
            BatteryResult sleep = lastValid.Clone();
            sleep.IsSleeping = true;
            sleep.Charging = false;
            sleep.Message = "鼠标休眠中";
            if (string.IsNullOrEmpty(sleep.Firmware) && !string.IsNullOrEmpty(_firmware))
                sleep.Firmware = _firmware;
            return sleep;
        }

        internal List<string> GetLog()
        {
            lock (_lock) return new List<string>(_lastLog);
        }

        private void Publish(BatteryResult r, List<string> log)
        {
            BatteryResult toEmit = r;
            if (r != null)
            {
                r.Timestamp = DateTime.Now;
                if (r.State == BatteryState.Battery && _dpiValue > 0 && r.DpiValue <= 0)
                    r.DpiValue = _dpiValue;
                if (string.IsNullOrEmpty(r.Firmware) && !string.IsNullOrEmpty(_firmware))
                    r.Firmware = _firmware;
            }
            lock (_lock)
            {
                _latest = r;
                _latestAt = DateTime.Now;
                if (r != null && r.State == BatteryState.Battery && !r.IsSleeping)
                {
                    _lastKnownBattery = r.Clone();
                }
                else if (r != null && r.State == BatteryState.Unavailable && _lastKnownBattery != null)
                {
                    toEmit = MakeSleepResult(_lastKnownBattery);
                }

                if (log.Count > 160)
                    _lastLog = log.Skip(log.Count - 120).ToList();
                else
                    _lastLog = new List<string>(log);
            }
            if (r != null && r.State == BatteryState.Battery && !r.IsSleeping)
            {
                History.Append(DateTime.Now, r.Percent, r.Charging);
            }
            Action<BatteryResult> handler = Updated;
            if (handler != null && toEmit != null)
            {
                try { handler(toEmit.Clone()); }
                catch { }
            }
        }

        private void Run()
        {
            while (!_stop)
            {
                var log = new List<string>();
                log.Add("时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                NativeHid.HidEntry entry = FindVendorEntry(log);
                if (entry == null)
                {
                    log.Add("未找到接收器 vendor 接口");
                    Publish(new BatteryResult { State = BatteryState.Disconnected }, log);
                    for (int i = 0; i < 20 && !_stop; i++) Thread.Sleep(100);
                    continue;
                }

                if (!string.IsNullOrEmpty(entry.Product))
                {
                    lock (_lock) _deviceName = FormatDeviceName(entry.Product);
                }

                SafeFileHandle h = NativeHid.Open(entry.Path);
                if (h == null || h.IsInvalid)
                {
                    log.Add("打开失败，Win32Error=" + NativeHid.LastError());
                    Publish(new BatteryResult { State = BatteryState.Unavailable, Message = "接口被占用" }, log);
                    for (int i = 0; i < 30 && !_stop; i++) Thread.Sleep(100);
                    continue;
                }

                log.Add("监听: " + entry);
                string fw = NativeHid.TryReadFirmware(h, entry.FeatureLen, log);
                if (!string.IsNullOrEmpty(fw)) { lock (_lock) _firmware = fw; }
                int dpi = NativeHid.QueryMouseAll(h, entry.FeatureLen, log);
                if (dpi > 0) _dpiValue = dpi;

                try { WatchHandle(h, entry, log); }
                catch (Exception ex) { log.Add("监听异常: " + ex.Message); }
                finally { h.Close(); }

                if (!_stop)
                {
                    Publish(new BatteryResult { State = BatteryState.Disconnected }, log);
                    Thread.Sleep(500);
                }
            }
        }

        private static string FormatDeviceName(string product)
        {
            if (string.IsNullOrEmpty(product)) return "迈从 A7 V2 Ultra";
            if (product.IndexOf("A7", StringComparison.OrdinalIgnoreCase) >= 0)
                return "迈从 A7 V2 Ultra";
            return product.Trim();
        }

        private static NativeHid.HidEntry FindVendorEntry(List<string> log)
        {
            List<NativeHid.HidEntry> devices = NativeHid.Enumerate();
            List<NativeHid.HidEntry> candidates = devices
                .Where(d => d.UsagePage == 0xFF01 && d.Usage == 1 && d.FeatureLen >= 20 && d.InputLen >= 8)
                .OrderByDescending(d => (d.Product ?? "").IndexOf("A7", StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList();
            foreach (var d in candidates) log.Add("候选: " + d);
            return candidates.FirstOrDefault();
        }

        private void WatchHandle(SafeFileHandle h, NativeHid.HidEntry entry, List<string> log)
        {
            int len = entry.FeatureLen;
            byte[] req = new byte[len];
            req[0] = 0x11;
            req[1] = 0x06 ^ 0xFF;
            for (int i = 2; i < len; i++) req[i] = 0xFF;

            int inLen = entry.InputLen;
            bool readPosted = false;
            int readFails = 0;
            DateTime lastQuery = DateTime.MinValue;

            // Unmanaged heap buffer + OVERLAPPED struct: 100% immune to GC compaction!
            using (var uo = new NativeHid.UnmanagedOverlapped(inLen))
            {
                if (uo.EventHandle == IntPtr.Zero) return;
                try
                {
                    while (!_stop)
                    {
                        if (!readPosted)
                        {
                            uo.ResetForIo();
                            uint tmp;
                            bool okP = NativeHid.ReadFile(h, uo.Buffer, (uint)inLen, out tmp, uo.OvPtr);
                            int err = NativeHid.LastError();
                            if (okP)
                            {
                                // Completed synchronously
                                readFails = 0;
                                if (tmp >= 8)
                                {
                                    byte[] syncBuf = uo.ReadManaged((int)tmp);
                                    BatteryResult sr = NativeHid.ParseE2(syncBuf, syncBuf.Length, log);
                                    if (sr != null)
                                    {
                                        sr.Device = entry.ToString();
                                        Publish(sr, log);
                                        lastQuery = DateTime.Now;
                                    }
                                }
                            }
                            else if (err == NativeHid.ERROR_IO_PENDING)
                            {
                                readPosted = true;
                            }
                            else if (++readFails >= 3)
                            {
                                log.Add("输入读取连续失败，Win32Error=" + err + "，重新枚举设备");
                                break;
                            }
                        }

                        bool wantQuery = _queryNow.WaitOne(0);
                        if (wantQuery && _dpiRefresh)
                        {
                            _dpiRefresh = false;
                            int dpi = NativeHid.QueryMouseAll(h, (ushort)len, log);
                            if (dpi > 0) _dpiValue = dpi;
                        }
                        if (wantQuery && !_manualQuery && (DateTime.Now - LatestAt).TotalSeconds < 25.0)
                        {
                            wantQuery = false;
                        }
                        if (wantQuery)
                        {
                            _manualQuery = false;
                        }

                        if (wantQuery || (DateTime.Now - lastQuery).TotalSeconds >= 15.0)
                        {
                            if (!NativeHid.HidD_SetFeature(h, req, req.Length))
                            {
                                log.Add("发送查询失败，Win32Error=" + NativeHid.LastError() + "，重新枚举设备");
                                break;
                            }
                            lastQuery = DateTime.Now;
                            if (log.Count > 200) log.RemoveRange(1, 100);
                            log.Add("发送查询 0x11/0x06 [" + lastQuery.ToString("HH:mm:ss") + "]");
                        }

                        if (readPosted)
                        {
                            int waitRes = NativeHid.WaitForSingleObject(uo.EventHandle, 200);
                            if (waitRes == 0)
                            {
                                uint got;
                                bool okRes = NativeHid.GetOverlappedResult(h, uo.OvPtr, out got, false);
                                readPosted = false;
                                if (!okRes)
                                {
                                    int err = NativeHid.LastError();
                                    if (++readFails >= 3)
                                    {
                                        log.Add("读取完成异常，Win32Error=" + err + "，重新枚举设备");
                                        break;
                                    }
                                }
                                else if (got >= 8)
                                {
                                    readFails = 0;
                                    byte[] inBuf = uo.ReadManaged((int)got);
                                    BatteryResult r = NativeHid.ParseE2(inBuf, inBuf.Length, log);
                                    if (r != null)
                                    {
                                        r.Device = entry.ToString();
                                        Publish(r, log);
                                        lastQuery = DateTime.Now;
                                    }
                                }
                            }
                        }
                        else
                        {
                            Thread.Sleep(200);
                        }
                    }
                }
                finally
                {
                    if (readPosted) uo.CancelAndDrain(h);
                }
            }
        }
    }
}
