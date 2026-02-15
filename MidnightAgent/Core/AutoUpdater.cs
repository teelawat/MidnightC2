using System;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace MidnightAgent.Core
{
    /// <summary>
    /// Auto updater - periodically checks for updates from a remote URL
    /// </summary>
    public static class AutoUpdater
    {
        // GitHub Version URL
        private const string VersionUrl = "https://raw.githubusercontent.com/teelawat/MidnightC2/refs/heads/main/version.txt";
        private static Timer _timer;

        /// <summary>
        /// Start the auto updater background task
        /// </summary>
        public static void Start(CancellationToken token)
        {
            Logger.Log("AutoUpdater started");
            
            // Initial check after 1 minute, then check every 60 minutes
            _timer = new Timer(CheckForUpdateCallback, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(60));
            
            // Register cancellation
            token.Register(() =>
            {
                _timer?.Dispose();
                Logger.Log("AutoUpdater stopped");
            });
        }

        private static void CheckForUpdateCallback(object state)
        {
            try
            {
                using (var client = new WebClient())
                {
                    // Fetch version file
                    string content = client.DownloadString(VersionUrl);
                    var lines = content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    
                    string remoteVersion = "";
                    string downloadUrl = "";

                    foreach (var line in lines)
                    {
                        if (line.StartsWith("v = ")) remoteVersion = line.Substring(4).Trim();
                        if (line.StartsWith("d = ")) downloadUrl = line.Substring(4).Trim();
                    }

                    if (!string.IsNullOrEmpty(remoteVersion) && !string.IsNullOrEmpty(downloadUrl))
                    {
                        // Compare versions
                        if (IsNewerVersion(remoteVersion, Config.Version))
                        {
                            Logger.Log($"New update found: {remoteVersion} (Current: {Config.Version})");
                            Logger.Log($"Downloading from: {downloadUrl}");
                            
                            // Trigger update using UpdateFeature logic
                            var updater = new Features.UpdateFeature();
                            updater.PerformUpdate(downloadUrl).Wait();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // Silent fail on check error
                // System.Diagnostics.Debug.WriteLine($"Update check failed: {ex.Message}");
            }
        }

        private static bool IsNewerVersion(string remote, string current)
        {
            try
            {
                var v1 = new Version(remote);
                var v2 = new Version(current);
                return v1 > v2;
            }
            catch
            {
                return false; // Version parsing failed
            }
        }
    }
}
