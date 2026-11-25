using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace MaicongTray
{
    internal static class SettingsStore
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunValue = "MaicongTray";
        private const string CfgKey = @"Software\MaicongTray";

        internal static AppSettings Load()
        {
            var s = new AppSettings();
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(CfgKey))
                {
                    if (k == null) return s;
                    int iv;
                    if (int.TryParse(Convert.ToString(k.GetValue("IntervalSec", 30)), out iv) && iv >= 5)
                        s.IntervalSec = iv;
                    s.LowNotify = Convert.ToString(k.GetValue("LowNotify", 1)) != "0";
                    s.FullNotify = Convert.ToString(k.GetValue("FullNotify", 1)) != "0";
                    s.FullscreenDnd = Convert.ToString(k.GetValue("FullscreenDnd", 1)) != "0";
                    int th;
                    if (int.TryParse(Convert.ToString(k.GetValue("LowThreshold", 20)), out th)
                        && (th == 10 || th == 15 || th == 20 || th == 30))
                        s.LowThreshold = th;
                    int st;
                    if (int.TryParse(Convert.ToString(k.GetValue("IconStyle", 0)), out st)
                        && Enum.IsDefined(typeof(TrayIconStyle), st))
                        s.IconStyle = (TrayIconStyle)st;
                    int cr;
                    if (int.TryParse(Convert.ToString(k.GetValue("ChartRangeHours", 24)), out cr)
                        && (cr == 24 || cr == 168))
                        s.ChartRangeHours = cr;
                }
            }
            catch { }
            return s;
        }

        internal static void Save(AppSettings s)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(CfgKey))
                {
                    if (k == null) return;
                    k.SetValue("IntervalSec", s.IntervalSec);
                    k.SetValue("LowNotify", s.LowNotify ? 1 : 0);
                    k.SetValue("FullNotify", s.FullNotify ? 1 : 0);
                    k.SetValue("FullscreenDnd", s.FullscreenDnd ? 1 : 0);
                    k.SetValue("LowThreshold", s.LowThreshold);
                    k.SetValue("IconStyle", (int)s.IconStyle);
                    k.SetValue("ChartRangeHours", s.ChartRangeHours);
                }
            }
            catch { }
        }

        internal static string StartupShortcutPath()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Startup),
                "MaicongTray.lnk");
        }

        internal static string InstalledExePath()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MaicongTray",
                "MaicongTray.exe");
        }

        internal static bool IsAutoStartEnabled()
        {
            string lnk = StartupShortcutPath();
            if (!File.Exists(lnk)) return false;
            object shell = null;
            object shortcut = null;
            try
            {
                Type t = Type.GetTypeFromProgID("WScript.Shell");
                shell = Activator.CreateInstance(t);
                shortcut = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnk });
                string target = shortcut.GetType().InvokeMember("TargetPath", BindingFlags.GetProperty, null, shortcut, null) as string;
                if (string.IsNullOrEmpty(target) || !File.Exists(target)) return false;
                return string.Equals(target, Application.ExecutablePath, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(target, InstalledExePath(), StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
            finally
            {
                if (shortcut != null) Marshal.ReleaseComObject(shortcut);
                if (shell != null) Marshal.ReleaseComObject(shell);
            }
        }

        internal static void CreateStartupShortcut(string exePath)
        {
            object shell = null;
            object shortcut = null;
            try
            {
                Type t = Type.GetTypeFromProgID("WScript.Shell");
                shell = Activator.CreateInstance(t);
                shortcut = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { StartupShortcutPath() });
                Type st = shortcut.GetType();
                st.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, new object[] { exePath });
                st.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut, new object[] { Path.GetDirectoryName(exePath) });
                st.InvokeMember("Arguments", BindingFlags.SetProperty, null, shortcut, new object[] { "--autostart" });
                st.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
            }
            finally
            {
                if (shortcut != null) Marshal.ReleaseComObject(shortcut);
                if (shell != null) Marshal.ReleaseComObject(shell);
            }
        }

        internal static void CleanupLegacyAutoStart()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (k != null) k.DeleteValue(RunValue, false);
                }
            }
            catch { }
            try
            {
                var psi = new ProcessStartInfo("schtasks.exe", "/Delete /TN \"MaicongTray\" /F");
                psi.CreateNoWindow = true;
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                using (Process p = Process.Start(psi))
                {
                    if (p != null)
                    {
                        p.StandardOutput.ReadToEnd();
                        p.WaitForExit(4000);
                    }
                }
            }
            catch { }
        }

        internal static void SetAutoStart(bool enable)
        {
            if (enable)
            {
                CreateStartupShortcut(Application.ExecutablePath);
                if (!IsAutoStartEnabled())
                    throw new InvalidOperationException("启动快捷方式验证失败");
            }
            else
            {
                string lnk = StartupShortcutPath();
                if (File.Exists(lnk)) File.Delete(lnk);
            }
            CleanupLegacyAutoStart();
        }

        internal static string InstallStableCopy()
        {
            string dst = InstalledExePath();
            if (!string.Equals(Application.ExecutablePath, dst, StringComparison.OrdinalIgnoreCase))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(dst));
                File.Copy(Application.ExecutablePath, dst, true);
            }
            CreateStartupShortcut(dst);
            if (!IsAutoStartEnabled())
                throw new InvalidOperationException("启动快捷方式验证失败");
            CleanupLegacyAutoStart();
            return dst;
        }
    }
}
