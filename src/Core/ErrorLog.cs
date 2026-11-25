using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;

namespace MaicongTray
{
    internal static class ErrorLog
    {
        internal static void Write(Exception ex, string context)
        {
            try
            {
                string dir = BatteryHistory.DataDirectory;
                Directory.CreateDirectory(dir);
                File.AppendAllText(
                    Path.Combine(dir, "error.log"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " [" + context + "] " + ex + Environment.NewLine);
            }
            catch { }
        }

        internal static void LogStartup()
        {
            try
            {
                string dir = BatteryHistory.DataDirectory;
                Directory.CreateDirectory(dir);
                File.AppendAllText(
                    Path.Combine(dir, "startup.log"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                        + " PID=" + Process.GetCurrentProcess().Id
                        + " " + Application.ExecutablePath
                        + Environment.NewLine);
            }
            catch { }
        }
    }
}
