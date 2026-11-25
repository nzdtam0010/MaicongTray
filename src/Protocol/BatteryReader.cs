using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Win32.SafeHandles;

namespace MaicongTray
{
    internal static class BatteryReader
    {
        internal static BatteryResult Read(List<string> log)
        {
            var devices = NativeHid.Enumerate();
            if (devices.Count == 0)
            {
                log.Add("未找到 VID 3837/5253 的 HID 设备（未插接收器/USB 线？）");
                return new BatteryResult { State = BatteryState.Disconnected, Timestamp = DateTime.Now };
            }

            var ordered = devices
                .OrderByDescending(d => d.UsagePage >= 0xFF00)
                .ThenByDescending(d => (d.Product ?? "").IndexOf("A7", StringComparison.OrdinalIgnoreCase) >= 0)
                .ThenByDescending(d => (d.Product ?? "").IndexOf("MCHOSE", StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList();

            foreach (var d in ordered)
            {
                log.Add("尝试: " + d.ToString());
                SafeFileHandle h = null;
                try
                {
                    h = NativeHid.Open(d.Path);
                    if (h.IsInvalid)
                    {
                        log.Add("    打开失败，Win32Error=" + NativeHid.LastError());
                        continue;
                    }
                    BatteryResult r = d.FeatureLen > 0 ? NativeHid.ClassicBatteryQuery(h, d.FeatureLen, d.InputLen, log) : null;
                    if (r == null && d.UsagePage == 0xFF01 && d.Usage == 1)
                        r = NativeHid.V2BatteryQuery(h, d.InputLen, d.OutputLen, log);

                    if (r != null)
                    {
                        if (d.FeatureLen > 0 && r.State == BatteryState.Battery)
                        {
                            int dpi = NativeHid.QueryMouseAll(h, d.FeatureLen, log);
                            if (dpi > 0) r.DpiValue = dpi;
                        }
                        r.Device = d.ToString();
                        log.Add("    => " + r.DisplayText() + " [" + r.Mode + "]");
                        return r;
                    }
                    log.Add("    => 无有效应答");
                }
                catch (Exception ex)
                {
                    log.Add("    异常: " + ex.Message);
                }
                finally
                {
                    if (h != null) h.Close();
                }
            }
            return new BatteryResult { State = BatteryState.Unavailable, Message = "所有接口均无应答", Timestamp = DateTime.Now };
        }
    }
}
