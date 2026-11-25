using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Cache;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace MaicongTray
{
    internal class UpdateInfo
    {
        public Version Version;
        public string Url;
        public string Notes;
        public string Error;
    }

    internal static class UpdateChecker
    {
        private const string GitHubLatestReleaseUrl = "https://api.github.com/repos/nzdtam0010/MaicongTray/releases/latest";
        private const string FallbackUpdateUrl = "https://071326.xyz/updates/maicong-tray/latest.json";

        internal static UpdateInfo Fetch()
        {
            // Ensure TLS 1.2 is enabled on .NET Framework 4.x.
            try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; }
            catch { }

            UpdateInfo gh = FetchFromGitHub();
            if (gh != null && string.IsNullOrEmpty(gh.Error) && gh.Version != null)
                return gh;

            UpdateInfo fb = FetchFromCustomJson();
            if (fb != null && string.IsNullOrEmpty(fb.Error) && fb.Version != null)
                return fb;

            return gh ?? fb ?? new UpdateInfo { Error = "无法连接更新服务" };
        }

        private static UpdateInfo FetchFromGitHub()
        {
            var info = new UpdateInfo();
            try
            {
                string body = HttpGet(GitHubLatestReleaseUrl, "application/vnd.github+json");
                Match mTag = Regex.Match(body, "\"tag_name\"\\s*:\\s*\"v?([^\"]+)\"", RegexOptions.IgnoreCase);
                Match mUrl = Regex.Match(body, "\"html_url\"\\s*:\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase);
                Match mName = Regex.Match(body, "\"name\"\\s*:\\s*\"([^\"]*)\"", RegexOptions.IgnoreCase);

                Version parsed;
                if (mTag.Success && Version.TryParse(mTag.Groups[1].Value, out parsed))
                    info.Version = parsed;
                if (mUrl.Success)
                    info.Url = mUrl.Groups[1].Value;
                if (mName.Success && !string.IsNullOrEmpty(mName.Groups[1].Value))
                    info.Notes = mName.Groups[1].Value;

                if (info.Version == null || string.IsNullOrEmpty(info.Url))
                    info.Error = "更新信息格式无效";
            }
            catch (Exception ex)
            {
                info.Error = ex.Message;
            }
            return info;
        }

        private static UpdateInfo FetchFromCustomJson()
        {
            var info = new UpdateInfo();
            try
            {
                string body = HttpGet(FallbackUpdateUrl, "application/json");
                Match mVer = Regex.Match(body, "\"version\"\\s*:\\s*\"v?([^\"]+)\"", RegexOptions.IgnoreCase);
                Match mUrl = Regex.Match(body, "\"url\"\\s*:\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase);
                Match mNotes = Regex.Match(body, "\"notes\"\\s*:\\s*\"([^\"]*)\"", RegexOptions.IgnoreCase);

                Version parsed;
                if (mVer.Success && Version.TryParse(mVer.Groups[1].Value, out parsed))
                    info.Version = parsed;
                if (mUrl.Success)
                    info.Url = mUrl.Groups[1].Value;
                if (mNotes.Success)
                    info.Notes = mNotes.Groups[1].Value;

                if (info.Version == null || string.IsNullOrEmpty(info.Url))
                    info.Error = "更新信息格式无效";
            }
            catch (Exception ex)
            {
                info.Error = ex.Message;
            }
            return info;
        }

        private static string HttpGet(string url, string accept)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "GET";
            req.Accept = accept;
            req.Timeout = 8000;
            req.ReadWriteTimeout = 8000;
            req.CachePolicy = new RequestCachePolicy(RequestCacheLevel.NoCacheNoStore);
            req.UserAgent = "MaicongTray/" + Assembly.GetExecutingAssembly().GetName().Version.ToString(3);

            using (WebResponse resp = req.GetResponse())
            using (Stream stream = resp.GetResponseStream())
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            {
                return reader.ReadToEnd();
            }
        }

        internal static void OpenUrl(string url)
        {
            if (string.IsNullOrEmpty(url) || !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return;
            try
            {
                var psi = new ProcessStartInfo();
                psi.FileName = url;
                psi.UseShellExecute = true;
                Process.Start(psi);
            }
            catch { }
        }
    }
}
