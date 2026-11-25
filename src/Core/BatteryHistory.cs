using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace MaicongTray
{
    internal class BatteryHistory
    {
        internal struct Sample
        {
            public DateTime T;
            public int Pct;
            public bool Chg;
        }

        // Retain 7 full days of history (~100 KB max) for 24h / 7d trend curves and full-charge tracking.
        private const int MaxHours = 168;
        private readonly object _lock = new object();
        private readonly List<Sample> _pts = new List<Sample>();
        private string _path;
        private bool _dirty;

        internal int SampleCount
        {
            get { lock (_lock) return _pts.Count; }
        }

        internal static string DataDirectory
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "MaicongTray");
            }
        }

        internal Sample[] GetSamples()
        {
            lock (_lock) return _pts.ToArray();
        }

        internal Sample[] GetSamplesWithin(int hours)
        {
            DateTime cutoff = DateTime.Now.AddHours(-Math.Max(1, hours));
            lock (_lock)
            {
                var list = new List<Sample>();
                for (int i = 0; i < _pts.Count; i++)
                {
                    if (_pts[i].T >= cutoff) list.Add(_pts[i]);
                }
                return list.ToArray();
            }
        }

        // Finds the most recent timestamp when battery was full (>=99%) or finished a charging session.
        internal DateTime? GetLastFullChargeTime()
        {
            lock (_lock)
            {
                for (int i = _pts.Count - 1; i >= 0; i--)
                {
                    if (_pts[i].Pct >= 99) return _pts[i].T;
                }
                // Fallback: last charging->discharging transition above 90%
                for (int i = _pts.Count - 1; i >= 1; i--)
                {
                    if (!_pts[i].Chg && _pts[i - 1].Chg && _pts[i - 1].Pct >= 90)
                        return _pts[i - 1].T;
                }
                return null;
            }
        }

        internal static string FormatElapsed(TimeSpan elapsed)
        {
            if (elapsed.TotalMinutes < 2.0) return "刚刚";
            if (elapsed.TotalHours < 1.0) return ((int)Math.Round(elapsed.TotalMinutes)) + " 分钟前";
            if (elapsed.TotalHours < 24.0) return Math.Round(elapsed.TotalHours, 1) + " 小时前";
            int days = (int)elapsed.TotalDays;
            int hrs = elapsed.Hours;
            return hrs > 0 ? (days + " 天 " + hrs + " 小时前") : (days + " 天前");
        }

        internal void Load()
        {
            try
            {
                string dir = DataDirectory;
                Directory.CreateDirectory(dir);
                _path = Path.Combine(dir, "history.csv");
                if (!File.Exists(_path)) return;

                DateTime cutoff = DateTime.Now.AddHours(-MaxHours);
                string[] lines = File.ReadAllLines(_path);
                lock (_lock)
                {
                    _pts.Clear();
                    foreach (string line in lines)
                    {
                        string[] parts = line.Split(',');
                        DateTime t;
                        int pct;
                        int chg;
                        if (parts.Length == 3
                            && DateTime.TryParse(parts[0], CultureInfo.InvariantCulture, DateTimeStyles.None, out t)
                            && t >= cutoff
                            && int.TryParse(parts[1], out pct) && pct >= 1 && pct <= 100
                            && int.TryParse(parts[2], out chg))
                        {
                            _pts.Add(new Sample { T = t, Pct = pct, Chg = (chg != 0) });
                        }
                    }
                }
            }
            catch
            {
                _path = null;
            }
        }

        internal void Append(DateTime t, int pct, bool chg)
        {
            if (pct < 1 || pct > 100) return;
            lock (_lock)
            {
                if (_pts.Count > 0)
                {
                    Sample last = _pts[_pts.Count - 1];
                    if (last.Pct == pct && last.Chg == chg && (t - last.T).TotalSeconds < 300.0)
                        return;
                }
                _pts.Add(new Sample { T = t, Pct = pct, Chg = chg });
                DateTime cutoff = t.AddHours(-MaxHours);
                _pts.RemoveAll(delegate(Sample p) { return p.T < cutoff; });
                _dirty = true;
                SaveNoLock();
            }
        }

        internal void Save()
        {
            lock (_lock) SaveNoLock();
        }

        private void SaveNoLock()
        {
            if (_path == null || !_dirty) return;
            try
            {
                var sb = new StringBuilder();
                foreach (Sample pt in _pts)
                {
                    sb.Append(pt.T.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))
                      .Append(',')
                      .Append(pt.Pct)
                      .Append(',')
                      .Append(pt.Chg ? 1 : 0)
                      .AppendLine();
                }
                File.WriteAllText(_path, sb.ToString());
                _dirty = false;
            }
            catch { }
        }

        internal bool Estimate(int curPct, bool charging, out TimeSpan eta, out double pctPerHour)
        {
            eta = TimeSpan.Zero;
            pctPerHour = 0.0;
            Sample[] arr = GetSamples();
            if (arr.Length < 3) return false;
            if ((DateTime.Now - arr[arr.Length - 1].T).Duration().TotalMinutes > 15.0) return false;
            if (arr[arr.Length - 1].Chg != charging) return false;

            int end = arr.Length - 1;
            int dir = charging ? 1 : -1;
            int start = end;
            while (start > 0 && arr[start - 1].Chg == charging)
            {
                double gapMin = (arr[start].T - arr[start - 1].T).TotalMinutes;
                double delta = (arr[start].Pct - arr[start - 1].Pct) * dir;
                if (gapMin > 30.0 || delta < -3.0) break;
                start--;
            }

            if (end - start + 1 < 3) return false;
            double spanMin = (arr[end].T - arr[start].T).TotalMinutes;
            if (spanMin < (charging ? 5.0 : 15.0)) return false;

            double totalDelta = (arr[end].Pct - arr[start].Pct) * dir;
            if (totalDelta < 1.0) return false;
            if (!charging && totalDelta < 2.0 && spanMin < 360.0) return false;

            double n = end - start + 1;
            double sx = 0, sy = 0, sxy = 0, sxx = 0;
            for (int i = start; i <= end; i++)
            {
                double x = (arr[i].T - arr[start].T).TotalMinutes;
                double y = arr[i].Pct;
                sx += x;
                sy += y;
                sxy += x * y;
                sxx += x * x;
            }
            double denom = n * sxx - sx * sx;
            if (denom <= 0.0) return false;

            double slopePerMin = (n * sxy - sx * sy) / denom;
            double ratePerMin = slopePerMin * dir;
            pctPerHour = ratePerMin * 60.0;

            if (charging)
            {
                if (ratePerMin < 1.0 / 30.0) return false;
                eta = TimeSpan.FromMinutes((100 - curPct) / ratePerMin);
                if (eta.TotalHours > 12.0) return false;
            }
            else
            {
                if (ratePerMin < 1.0 / 600.0) return false;
                eta = TimeSpan.FromMinutes(curPct / ratePerMin);
                if (eta.TotalDays > 14.0) return false;
            }
            return true;
        }

        internal static string FormatEta(TimeSpan eta)
        {
            if (eta.TotalMinutes < 1.0) return "不到 1 分钟";
            if (eta.TotalMinutes < 60.0) return "约 " + Math.Round(eta.TotalMinutes) + " 分钟";
            if (eta.TotalHours < 48.0) return "约 " + Math.Round(eta.TotalHours, 1) + " 小时";
            return "约 " + Math.Round(eta.TotalDays, 1) + " 天";
        }
    }
}
