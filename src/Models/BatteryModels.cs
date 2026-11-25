using System;

namespace MaicongTray
{
    public enum BatteryState
    {
        Battery,
        Disconnected,
        Unavailable,
        Error
    }

    public enum TrayIconStyle
    {
        MinimalNumber = 0,  // 极简数字（默认）
        RadialRing = 1,     // 微弧光环
        BatteryPill = 2,    // 经典电池
        NumberWithBar = 3   // 数字底条
    }

    public class BatteryResult
    {
        public BatteryState State;
        public int Percent = -1;
        public bool Charging;
        public bool IsSleeping;         // true when showing preserved last-known battery while mouse sleeps
        public string Device = "";
        public string Firmware = "";    // Parsed from 0x11/0x06 response bytes 4..7
        public string Mode = "";
        public string Message = "";
        public int ConnMode = -1;       // 0=有线, 1=2.4G, 2=蓝牙, 3=蓝牙2
        public int DpiValue = -1;       // >0 时有效
        public DateTime Timestamp = DateTime.Now;

        public string ConnName()
        {
            switch (ConnMode)
            {
                case 0: return "有线";
                case 1: return "2.4G";
                case 2: return "蓝牙";
                case 3: return "蓝牙 2";
                default: return ConnMode > 0 ? "模式 " + ConnMode : null;
            }
        }

        public string DpiName()
        {
            return DpiValue > 0 ? DpiValue + " DPI" : null;
        }

        public string DisplayText()
        {
            if (IsSleeping && Percent >= 0)
            {
                string conn = ConnName();
                return Percent + "%（休眠中" + (!string.IsNullOrEmpty(conn) ? " · " + conn : "") + "）";
            }

            switch (State)
            {
                case BatteryState.Battery:
                    string extra = "";
                    string conn = ConnName();
                    string dpi = DpiName();
                    if (!string.IsNullOrEmpty(conn)) extra = conn;
                    if (!string.IsNullOrEmpty(dpi)) extra += (extra.Length > 0 ? " · " : "") + dpi;

                    string tail = "";
                    if (Charging && Percent >= 100) tail = "（已充满）";
                    else if (Charging) tail = "（充电中）";

                    if (extra.Length > 0) return Percent + "% · " + extra + tail;
                    return Percent + "%" + tail;

                case BatteryState.Disconnected:
                    return "未检测到设备";

                case BatteryState.Unavailable:
                    return string.IsNullOrEmpty(Message) ? "电量不可用（鼠标可能休眠）" : "电量不可用（" + Message + "）";

                default:
                    return "读取失败：" + Message;
            }
        }

        public BatteryResult Clone()
        {
            return new BatteryResult
            {
                State = this.State,
                Percent = this.Percent,
                Charging = this.Charging,
                IsSleeping = this.IsSleeping,
                Device = this.Device,
                Firmware = this.Firmware,
                Mode = this.Mode,
                Message = this.Message,
                ConnMode = this.ConnMode,
                DpiValue = this.DpiValue,
                Timestamp = this.Timestamp
            };
        }
    }

    internal class AppSettings
    {
        public int IntervalSec = 30;
        public bool LowNotify = true;
        public bool FullNotify = true;
        public bool FullscreenDnd = true;
        public int LowThreshold = 20;
        public TrayIconStyle IconStyle = TrayIconStyle.MinimalNumber;
        public int ChartRangeHours = 24; // 24 or 168 (7 days)
    }
}
