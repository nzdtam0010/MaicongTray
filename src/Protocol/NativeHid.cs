using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace MaicongTray
{
    // Native Windows HID layer for MCHOSE mice (A7 V2 Ultra tested).
    // All overlapped I/O uses pinned unmanaged heap memory (UnmanagedOverlapped)
    // and drains cancelled IRPs before freeing buffers, completely preventing
    // GC-compaction heap corruption (0x80131506).
    internal static class NativeHid
    {
        private const uint GENERIC_READ = 0x80000000;
        private const uint GENERIC_WRITE = 0x40000000;
        private const uint FILE_SHARE_READ = 0x00000001;
        private const uint FILE_SHARE_WRITE = 0x00000002;
        private const uint OPEN_EXISTING = 3;
        private const uint FILE_FLAG_OVERLAPPED = 0x40000000;
        private const int DIGCF_PRESENT = 0x02;
        private const int DIGCF_DEVICEINTERFACE = 0x10;
        internal const int ERROR_IO_PENDING = 997;

        [StructLayout(LayoutKind.Sequential)]
        private struct SP_DEVICE_INTERFACE_DATA
        {
            public int cbSize;
            public Guid InterfaceClassGuid;
            public int Flags;
            public IntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct HIDD_ATTRIBUTES
        {
            public int Size;
            public ushort VendorID;
            public ushort ProductID;
            public ushort VersionNumber;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct HIDP_CAPS
        {
            public ushort Usage;
            public ushort UsagePage;
            public ushort InputReportByteLength;
            public ushort OutputReportByteLength;
            public ushort FeatureReportByteLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
            public ushort[] Reserved;
            public ushort NumberLinkCollectionNodes;
            public ushort NumberInputButtonCaps;
            public ushort NumberInputValueCaps;
            public ushort NumberInputDataIndices;
            public ushort NumberOutputButtonCaps;
            public ushort NumberOutputValueCaps;
            public ushort NumberOutputDataIndices;
            public ushort NumberFeatureButtonCaps;
            public ushort NumberFeatureValueCaps;
            public ushort NumberFeatureDataIndices;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct OVERLAPPED
        {
            public IntPtr Internal;
            public IntPtr InternalHigh;
            public int OffsetLow;
            public int OffsetHigh;
            public IntPtr EventHandle;
        }

        // Unmanaged heap holder for OVERLAPPED + I/O buffer so GC compaction can NEVER relocate
        // memory referenced by a pending kernel IRP.
        internal sealed class UnmanagedOverlapped : IDisposable
        {
            public IntPtr Buffer;
            public int BufferLength;
            public IntPtr OvPtr;
            public IntPtr EventHandle;
            private readonly int _ovSize;
            private readonly int _evOffset;

            public UnmanagedOverlapped(int bufferLength)
            {
                BufferLength = Math.Max(1, bufferLength);
                Buffer = Marshal.AllocHGlobal(BufferLength);
                Zero(Buffer, BufferLength);

                _ovSize = Marshal.SizeOf(typeof(OVERLAPPED));
                _evOffset = Marshal.OffsetOf(typeof(OVERLAPPED), "EventHandle").ToInt32();
                OvPtr = Marshal.AllocHGlobal(_ovSize);
                Zero(OvPtr, _ovSize);

                EventHandle = CreateEvent(IntPtr.Zero, true, false, null);
                Marshal.WriteIntPtr(OvPtr, _evOffset, EventHandle);
            }

            public void ResetForIo()
            {
                Zero(OvPtr, _ovSize);
                Marshal.WriteIntPtr(OvPtr, _evOffset, EventHandle);
                if (EventHandle != IntPtr.Zero) ResetEvent(EventHandle);
            }

            public byte[] ReadManaged(int count)
            {
                int n = Math.Max(0, Math.Min(count, BufferLength));
                byte[] dst = new byte[n];
                if (n > 0) Marshal.Copy(Buffer, dst, 0, n);
                return dst;
            }

            public void WriteManaged(byte[] src)
            {
                int n = Math.Min(src.Length, BufferLength);
                if (n > 0) Marshal.Copy(src, 0, Buffer, n);
            }

            public void CancelAndDrain(SafeFileHandle h)
            {
                if (h == null || h.IsInvalid || h.IsClosed || OvPtr == IntPtr.Zero) return;
                CancelIoEx(h, OvPtr);
                uint dummy;
                GetOverlappedResult(h, OvPtr, out dummy, true);
            }

            private static void Zero(IntPtr ptr, int len)
            {
                for (int i = 0; i < len; i++) Marshal.WriteByte(ptr, i, 0);
            }

            public void Dispose()
            {
                if (EventHandle != IntPtr.Zero) { CloseHandle(EventHandle); EventHandle = IntPtr.Zero; }
                if (OvPtr != IntPtr.Zero) { Marshal.FreeHGlobal(OvPtr); OvPtr = IntPtr.Zero; }
                if (Buffer != IntPtr.Zero) { Marshal.FreeHGlobal(Buffer); Buffer = IntPtr.Zero; }
            }
        }

        internal class HidEntry
        {
            public string Path;
            public string Product;
            public ushort Vid;
            public ushort Pid;
            public ushort UsagePage;
            public ushort Usage;
            public ushort FeatureLen;
            public ushort InputLen;
            public ushort OutputLen;

            public override string ToString()
            {
                return string.Format("{0}  VID:PID {1:X4}:{2:X4}  Usage {3:X4}:{4:X4}  Feature:{5} In:{6} Out:{7}",
                    Product, Vid, Pid, UsagePage, Usage, FeatureLen, InputLen, OutputLen);
            }
        }

        internal static readonly ushort[] KnownVids = new ushort[] { 0x3837, 0x5253 };

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern IntPtr SetupDiGetClassDevs(ref Guid ClassGuid, IntPtr Enumerator, IntPtr hwndParent, int Flags);
        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr data, ref Guid guid, int index, ref SP_DEVICE_INTERFACE_DATA ifData);
        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SP_DEVICE_INTERFACE_DATA ifData, IntPtr detail, int detailSize, ref int requiredSize, IntPtr deviceInfoData);
        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr template);
        [DllImport("hid.dll", SetLastError = true)]
        private static extern void HidD_GetHidGuid(out Guid hidGuid);
        [DllImport("hid.dll", SetLastError = true)]
        private static extern bool HidD_GetAttributes(SafeFileHandle h, ref HIDD_ATTRIBUTES attr);
        [DllImport("hid.dll", SetLastError = true)]
        private static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr preparsed);
        [DllImport("hid.dll", SetLastError = true)]
        private static extern bool HidD_FreePreparsedData(IntPtr preparsed);
        [DllImport("hid.dll", SetLastError = true)]
        private static extern int HidP_GetCaps(IntPtr preparsed, ref HIDP_CAPS caps);
        [DllImport("hid.dll", SetLastError = true)]
        internal static extern bool HidD_SetFeature(SafeFileHandle h, byte[] buffer, int length);
        [DllImport("hid.dll", SetLastError = true)]
        internal static extern bool HidD_GetFeature(SafeFileHandle h, byte[] buffer, int length);
        [DllImport("hid.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool HidD_GetProductString(SafeFileHandle h, byte[] buffer, int length);
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool WriteFile(SafeFileHandle h, IntPtr buffer, uint toWrite, out uint written, IntPtr ov);
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool ReadFile(SafeFileHandle h, IntPtr buffer, uint toRead, out uint read, IntPtr ov);
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool GetOverlappedResult(SafeFileHandle h, IntPtr ov, out uint transferred, bool wait);
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool CancelIoEx(SafeFileHandle h, IntPtr ov);
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern IntPtr CreateEvent(IntPtr sa, bool manualReset, bool initialState, string name);
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern int WaitForSingleObject(IntPtr handle, int ms);
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool ResetEvent(IntPtr handle);

        internal static List<HidEntry> Enumerate()
        {
            var result = new List<HidEntry>();
            Guid hidGuid;
            HidD_GetHidGuid(out hidGuid);
            IntPtr setup = SetupDiGetClassDevs(ref hidGuid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
            if (setup == IntPtr.Zero || setup == new IntPtr(-1)) return result;
            try
            {
                var ifData = new SP_DEVICE_INTERFACE_DATA();
                ifData.cbSize = Marshal.SizeOf(typeof(SP_DEVICE_INTERFACE_DATA));
                int index = 0;
                while (SetupDiEnumDeviceInterfaces(setup, IntPtr.Zero, ref hidGuid, index, ref ifData))
                {
                    index++;
                    int needed = 0;
                    SetupDiGetDeviceInterfaceDetail(setup, ref ifData, IntPtr.Zero, 0, ref needed, IntPtr.Zero);
                    if (needed <= 0) continue;
                    IntPtr detail = Marshal.AllocHGlobal(needed);
                    try
                    {
                        Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                        if (!SetupDiGetDeviceInterfaceDetail(setup, ref ifData, detail, needed, ref needed, IntPtr.Zero)) continue;
                        string path = Marshal.PtrToStringUni(new IntPtr(detail.ToInt64() + 4));
                        var entry = Inspect(path);
                        if (entry != null) result.Add(entry);
                    }
                    finally { Marshal.FreeHGlobal(detail); }
                }
            }
            finally { SetupDiDestroyDeviceInfoList(setup); }
            return result;
        }

        private static HidEntry Inspect(string path)
        {
            SafeFileHandle h = null;
            try
            {
                h = CreateFile(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, IntPtr.Zero);
                if (h.IsInvalid) return null;
                var attr = new HIDD_ATTRIBUTES();
                attr.Size = Marshal.SizeOf(typeof(HIDD_ATTRIBUTES));
                if (!HidD_GetAttributes(h, ref attr)) return null;
                if (Array.IndexOf(KnownVids, attr.VendorID) < 0) return null;

                IntPtr pre;
                if (!HidD_GetPreparsedData(h, out pre)) return null;
                var caps = new HIDP_CAPS();
                int capsResult = HidP_GetCaps(pre, ref caps);
                HidD_FreePreparsedData(pre);
                if (capsResult <= 0) return null;

                var e = new HidEntry();
                e.Path = path;
                e.Vid = attr.VendorID;
                e.Pid = attr.ProductID;
                e.UsagePage = caps.UsagePage;
                e.Usage = caps.Usage;
                e.FeatureLen = caps.FeatureReportByteLength;
                e.InputLen = caps.InputReportByteLength;
                e.OutputLen = caps.OutputReportByteLength;

                var buf = new byte[512];
                if (HidD_GetProductString(h, buf, buf.Length))
                {
                    e.Product = Encoding.Unicode.GetString(buf).TrimEnd('\0');
                }
                return e;
            }
            catch { return null; }
            finally { if (h != null) h.Close(); }
        }

        internal static SafeFileHandle Open(string path)
        {
            return CreateFile(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, IntPtr.Zero);
        }

        internal static int LastError()
        {
            return Marshal.GetLastWin32Error();
        }

        internal static BatteryResult ClassicBatteryQuery(SafeFileHandle h, ushort featureLen, ushort inputLen, List<string> log)
        {
            if (featureLen < 20)
            {
                log.Add("    该接口无 Feature 报文（长度 0），跳过经典协议");
                return null;
            }
            int len = featureLen;
            byte[] req = new byte[len];
            req[0] = 0x11;
            req[1] = 0x06 ^ 0xFF;
            for (int i = 2; i < len; i++) req[i] = 0xFF;

            int inLen = inputLen >= 8 ? inputLen : 0;
            bool readPosted = false;
            using (var uo = new UnmanagedOverlapped(inLen > 0 ? inLen : 8))
            {
                if (uo.EventHandle == IntPtr.Zero) return null;
                try
                {
                    if (inLen > 0)
                    {
                        uo.ResetForIo();
                        uint tmp;
                        bool okP = ReadFile(h, uo.Buffer, (uint)inLen, out tmp, uo.OvPtr);
                        readPosted = okP || LastError() == ERROR_IO_PENDING;
                        if (!readPosted) log.Add("    挂起输入读取失败，Win32Error=" + LastError());
                    }

                    for (int attempt = 1; attempt <= 2; attempt++)
                    {
                        if (!HidD_SetFeature(h, req, req.Length))
                        {
                            log.Add("    发送 0x11 查询失败，Win32Error=" + LastError());
                            return null;
                        }

                        Stopwatch sw = Stopwatch.StartNew();
                        while (sw.ElapsedMilliseconds < 7000)
                        {
                            if (readPosted)
                            {
                                uint got;
                                if (WaitForSingleObject(uo.EventHandle, 0) == 0 && GetOverlappedResult(h, uo.OvPtr, out got, false))
                                {
                                    readPosted = false;
                                    if (got >= 8)
                                    {
                                        byte[] inBuf = uo.ReadManaged((int)got);
                                        BatteryResult fromInput = ParseE2(inBuf, inBuf.Length, log);
                                        if (fromInput != null) return fromInput;
                                    }
                                    uo.ResetForIo();
                                    uint tmp2;
                                    bool okP2 = ReadFile(h, uo.Buffer, (uint)inLen, out tmp2, uo.OvPtr);
                                    readPosted = okP2 || LastError() == ERROR_IO_PENDING;
                                }
                            }

                            byte[] raw = new byte[len];
                            raw[0] = 0x11;
                            if (HidD_GetFeature(h, raw, raw.Length))
                            {
                                bool invertedPhase = raw[1] == (0x06 ^ 0xFF);
                                bool plainPhase = raw[1] == 0x06;
                                if (invertedPhase || plainPhase)
                                {
                                    byte[] d = new byte[12];
                                    for (int i = 0; i < 12; i++) d[i] = invertedPhase ? (byte)(raw[2 + i] ^ 0xFF) : raw[2 + i];
                                    int vid = d[0] | (d[1] << 8);
                                    int flags = d[8];
                                    int battery = d[9];
                                    int charge = d[10];
                                    if (battery > 0 && battery <= 10) battery *= 10;
                                    if (vid == 0x3837 && battery >= 0 && battery <= 100)
                                    {
                                        string fw = FormatFirmware(d[4], d[5], d[6], d[7]);
                                        log.Add("    应答(原始): " + Hex(raw.Take(Math.Min(len, 16)).ToArray()) + " FW=" + fw);
                                        return new BatteryResult
                                        {
                                            State = BatteryState.Battery,
                                            Percent = battery,
                                            Charging = (charge == 1 || charge == 3 || charge == 4),
                                            ConnMode = flags & 0x07,
                                            Firmware = fw,
                                            Mode = "官方协议 0x11/0x06",
                                            Timestamp = DateTime.Now
                                        };
                                    }
                                }
                            }
                            Thread.Sleep(100);
                        }
                        Thread.Sleep(200);
                    }
                    return null;
                }
                finally
                {
                    if (readPosted) uo.CancelAndDrain(h);
                }
            }
        }

        internal static BatteryResult ParseE2(byte[] inBuf, int n, List<string> log)
        {
            if (n < 8 || inBuf[1] != (0xE2 ^ 0xFF)) return null;
            int conn = inBuf[2] ^ 0xFF;
            int charge = inBuf[4] ^ 0xFF;
            int battery = inBuf[5] ^ 0xFF;
            log.Add("    E2 主动上报: 连接=" + conn + " 充电=" + charge + " 电量=" + battery);
            if (battery > 100) return null;
            if (battery == 0)
            {
                return new BatteryResult
                {
                    State = BatteryState.Unavailable,
                    Mode = "主动上报 E2",
                    Timestamp = DateTime.Now
                };
            }
            return new BatteryResult
            {
                State = BatteryState.Battery,
                Percent = battery,
                Charging = (charge == 1 || charge == 3 || charge == 4),
                ConnMode = conn,
                Mode = "主动上报 E2",
                Timestamp = DateTime.Now
            };
        }

        internal static int QueryMouseAll(SafeFileHandle h, ushort featureLen, List<string> log)
        {
            if (featureLen < 22) return -1;
            int len = featureLen;
            byte[] req = new byte[len];
            req[0] = 0x12;
            req[1] = 0x67 ^ 0xFF;
            for (int i = 2; i < len; i++) req[i] = 0xFF;
            if (!HidD_SetFeature(h, req, len))
            {
                log.Add("    0x12/0x67 发送失败，Win32Error=" + LastError());
                return -1;
            }
            byte[] raw = new byte[len];
            for (int attempt = 0; attempt < 4; attempt++)
            {
                for (int i = 0; i < len; i++) raw[i] = 0;
                raw[0] = 0x12;
                if (!HidD_GetFeature(h, raw, len))
                {
                    log.Add("    0x12/0x67 回读失败，Win32Error=" + LastError());
                    return -1;
                }
                bool inv = raw[1] == (0x67 ^ 0xFF);
                bool plain = raw[1] == 0x67;
                bool dataReady = inv && (raw[3] != 0xFF || raw[4] != 0xFF);
                if (attempt == 0)
                {
                    string phase = inv ? "取反" : (plain ? "明文" : "相位" + raw[1].ToString("X2"));
                    log.Add("    0x12/0x67 回读(" + phase + "): " + Hex(raw.Take(Math.Min(len, 24)).ToArray()));
                }
                if ((inv || plain) && dataReady)
                {
                    byte[] d = new byte[20];
                    for (int i = 0; i < d.Length; i++) d[i] = inv ? (byte)(raw[2 + i] ^ 0xFF) : raw[2 + i];
                    int usbDpiIndex = d[2] & 0x0F;
                    int dpi = -1;
                    if (usbDpiIndex < 6)
                    {
                        int off = 4 + usbDpiIndex * 2;
                        dpi = d[off] | (d[off + 1] << 8);
                    }
                    log.Add("    0x12/0x67: profile=" + d[0] + " usbDpiIndex=" + usbDpiIndex + " dpiSum=" + d[16] + " 当前DPI=" + dpi);
                    return (dpi > 0 && dpi <= 60000) ? dpi : -1;
                }
                Thread.Sleep(120);
            }
            log.Add("    0x12/0x67 无有效数据（本固件不支持 DPI 回读）");
            return -1;
        }

        internal static BatteryResult V2BatteryQuery(SafeFileHandle h, ushort inLen, ushort outLen, List<string> log)
        {
            if (outLen < 64 || inLen < 16)
            {
                log.Add("    报文长度不足 (In:" + inLen + " Out:" + outLen + ")，跳过 V2 协议");
                return null;
            }
            byte[] req = new byte[outLen];
            req[0] = 0x4D; req[1] = 1; req[2] = 1; req[3] = 0; req[4] = 0; req[5] = 9; req[6] = 0; req[7] = 0; req[8] = 8;

            using (var uoWrite = new UnmanagedOverlapped(outLen))
            using (var uoRead = new UnmanagedOverlapped(inLen))
            {
                if (uoWrite.EventHandle == IntPtr.Zero || uoRead.EventHandle == IntPtr.Zero) return null;

                uoWrite.WriteManaged(req);
                uoWrite.ResetForIo();
                uint written;
                bool okW = WriteFile(h, uoWrite.Buffer, (uint)outLen, out written, uoWrite.OvPtr);
                if (!okW && LastError() != ERROR_IO_PENDING)
                {
                    log.Add("    V2 写入失败，Win32Error=" + LastError());
                    return null;
                }
                if (!okW)
                {
                    if (WaitForSingleObject(uoWrite.EventHandle, 500) != 0 || !GetOverlappedResult(h, uoWrite.OvPtr, out written, false) || written < 9)
                    {
                        uoWrite.CancelAndDrain(h);
                        log.Add("    V2 写入未完成");
                        return null;
                    }
                }

                Stopwatch sw = Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < 800)
                {
                    uoRead.ResetForIo();
                    uint got;
                    bool okR = ReadFile(h, uoRead.Buffer, (uint)inLen, out got, uoRead.OvPtr);
                    int err = LastError();
                    if (!okR && err != ERROR_IO_PENDING)
                    {
                        log.Add("    V2 读取失败，Win32Error=" + err);
                        return null;
                    }
                    if (!okR)
                    {
                        int wait = (int)Math.Max(1L, 800 - sw.ElapsedMilliseconds);
                        if (WaitForSingleObject(uoRead.EventHandle, wait) != 0 || !GetOverlappedResult(h, uoRead.OvPtr, out got, false))
                        {
                            uoRead.CancelAndDrain(h);
                            break;
                        }
                    }
                    if (got == 0) continue;
                    byte[] buf = uoRead.ReadManaged((int)got);
                    int shown = Math.Min(buf.Length, 32);
                    log.Add("    V2 应答: " + Hex(buf.Take(shown).ToArray()));
                    BatteryResult r = ParseV2(buf, buf.Length);
                    if (r != null) return r;
                }
                return null;
            }
        }

#if CHECK_MODE
        internal static void ClassicDiag(SafeFileHandle h, ushort featureLen, ushort inputLen, List<string> log)
        {
            int len = featureLen;
            byte[] req = new byte[len];
            req[0] = 0x11;
            req[1] = 0x06 ^ 0xFF;
            for (int i = 2; i < len; i++) req[i] = 0xFF;
            log.Add("[diag] SetFeature(0x11, F9 FF...): " + (HidD_SetFeature(h, req, len) ? "OK" : "FAIL " + LastError()));

            using (var uo = new UnmanagedOverlapped(Math.Max(8, (int)inputLen)))
            {
                for (int round = 0; round < 10; round++)
                {
                    if (inputLen > 0)
                    {
                        uo.ResetForIo();
                        uint got;
                        bool okR = ReadFile(h, uo.Buffer, (uint)inputLen, out got, uo.OvPtr);
                        int err = LastError();
                        if (!okR && err == ERROR_IO_PENDING)
                        {
                            if (WaitForSingleObject(uo.EventHandle, 40) == 0 && GetOverlappedResult(h, uo.OvPtr, out got, false) && got > 0)
                            {
                                byte[] inBuf = uo.ReadManaged((int)got);
                                log.Add("[diag] IN  r" + round + ": " + Hex(inBuf.Take(Math.Min(inBuf.Length, 24)).ToArray()));
                            }
                            else
                            {
                                uo.CancelAndDrain(h);
                            }
                        }
                        else if (okR && got > 0)
                        {
                            byte[] inBuf = uo.ReadManaged((int)got);
                            log.Add("[diag] IN  r" + round + ": " + Hex(inBuf.Take(Math.Min(inBuf.Length, 24)).ToArray()));
                        }
                    }
                    byte[] raw = new byte[len];
                    raw[0] = 0x11;
                    if (HidD_GetFeature(h, raw, raw.Length))
                        log.Add("[diag] FEAT r" + round + ": " + Hex(raw.Take(Math.Min(len, 24)).ToArray()));
                    else
                        log.Add("[diag] FEAT r" + round + ": FAIL " + LastError());
                    Thread.Sleep(50);
                }
            }
        }
#endif

        private static BatteryResult ParseV2(byte[] b, int n)
        {
            if (n < 9 || b[0] != 0x4D || b[1] != 1 || b[2] != 1 || b[4] != 0 || b[5] != 9 || b[6] != 0) return null;
            int length = b[3];
            if (length < 14 || 9 + length > n) return null;
            byte chk = 0;
            for (int i = 2; i <= 7 + length; i++) chk ^= b[i];
            if (chk != b[8 + length]) return null;
            int p0 = 8;
            if (b[p0] != 0x37 || b[p0 + 1] != 0x38) return null;
            int conn = b[p0 + 10];
            if (conn == 0) return new BatteryResult { State = BatteryState.Unavailable, Mode = "V2 协议 0x4D/0x0900", Timestamp = DateTime.Now };
            if (conn != 2) return null;
            int pct = b[p0 + 12];
            int chg = b[p0 + 11];
            if (pct > 100 || chg > 1) return null;
            return new BatteryResult
            {
                State = BatteryState.Battery,
                Percent = pct,
                Charging = (chg == 1),
                Mode = "V2 协议 0x4D/0x0900",
                Timestamp = DateTime.Now
            };
        }

        internal static string FormatFirmware(byte b0, byte b1, byte b2, byte b3)
        {
            if (b0 == 0 && b1 == 0 && b2 == 0 && b3 == 0) return "";
            if (b0 == 0xFF && b1 == 0xFF && b2 == 0xFF && b3 == 0xFF) return "";
            return string.Format("{0:X2}{1:X2}.{2:X2}{3:X2}", b0, b1, b2, b3);
        }

        internal static string TryReadFirmware(SafeFileHandle h, ushort featureLen, List<string> log)
        {
            if (featureLen < 20) return "";
            try
            {
                byte[] req = new byte[featureLen];
                req[0] = 0x11;
                req[1] = 0x06 ^ 0xFF;
                for (int i = 2; i < featureLen; i++) req[i] = 0xFF;
                if (!HidD_SetFeature(h, req, req.Length)) return "";

                for (int i = 0; i < 4; i++)
                {
                    Thread.Sleep(60);
                    byte[] raw = new byte[featureLen];
                    raw[0] = 0x11;
                    if (!HidD_GetFeature(h, raw, raw.Length)) continue;
                    bool inv = raw[1] == (0x06 ^ 0xFF);
                    bool plain = raw[1] == 0x06;
                    if (!inv && !plain) continue;
                    byte[] d = new byte[12];
                    for (int k = 0; k < 12; k++) d[k] = inv ? (byte)(raw[2 + k] ^ 0xFF) : raw[2 + k];
                    int vid = d[0] | (d[1] << 8);
                    if (vid == 0x3837)
                    {
                        string fw = FormatFirmware(d[4], d[5], d[6], d[7]);
                        if (!string.IsNullOrEmpty(fw) && log != null) log.Add("    固件版本: " + fw);
                        return fw;
                    }
                }
            }
            catch { }
            return "";
        }

        internal static string Hex(byte[] data)
        {
            var sb = new StringBuilder();
            foreach (byte x in data) sb.Append(x.ToString("X2")).Append(' ');
            return sb.ToString().TrimEnd();
        }
    }
}
