using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace MidnightAgent.Security
{
    /// <summary>
    /// Detect if running in a Virtual Machine
    /// </summary>
    public static class VMDetection
    {
        private static readonly string[] VmProcesses = new[]
        {
            "vmtoolsd", "vmwaretray", "vmwareuser",     // VMware
            "vboxservice", "vboxtray",                   // VirtualBox
            "vmsrvc", "vmusrvc",                         // Virtual PC
            "joeboxcontrol", "joeboxserver"              // JoeBox
        };

        private static readonly string[] VmMacPrefixes = new[]
        {
            "00:0C:29",  // VMware
            "00:50:56",  // VMware
            "00:05:69",  // VMware
            "08:00:27",  // VirtualBox
            "00:1C:42",  // Parallels
            "00:03:FF",  // Microsoft Hyper-V
            "00:15:5D"   // Microsoft Hyper-V
        };

        private static readonly string[] VmFiles = new[]
        {
            @"C:\Windows\System32\drivers\vmmouse.sys",
            @"C:\Windows\System32\drivers\vmhgfs.sys",
            @"C:\Windows\System32\drivers\VBoxMouse.sys",
            @"C:\Windows\System32\drivers\VBoxGuest.sys",
            @"C:\Windows\System32\drivers\VBoxSF.sys"
        };

        /// <summary>
        /// Check if running in a VM
        /// </summary>
        public static bool IsVM()
        {
            try
            {
                // Check processes
                if (CheckVmProcesses())
                    return true;

                // Check registry
                if (CheckVmRegistry())
                    return true;

                // Check files
                if (CheckVmFiles())
                    return true;

                // Check MAC address
                if (CheckVmMac())
                    return true;

                return false;
            }
            catch
            {
                return false; // Fail open
            }
        }

        private static bool CheckVmProcesses()
        {
            try
            {
                var runningProcesses = Process.GetProcesses()
                    .Select(p => p.ProcessName.ToLower())
                    .ToArray();

                return VmProcesses.Any(vm => 
                    runningProcesses.Contains(vm.ToLower()));
            }
            catch
            {
                return false;
            }
        }

        private static bool CheckVmRegistry()
        {
            try
            {
                // Check for VMware
                using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\VMware, Inc.\VMware Tools"))
                {
                    if (key != null) return true;
                }

                // Check for VirtualBox
                using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Oracle\VirtualBox Guest Additions"))
                {
                    if (key != null) return true;
                }

                // Check system BIOS
                using (var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\Description\System"))
                {
                    if (key != null)
                    {
                        string biosVersion = key.GetValue("SystemBiosVersion")?.ToString() ?? "";
                        if (biosVersion.ToUpper().Contains("VMWARE") ||
                            biosVersion.ToUpper().Contains("VBOX") ||
                            biosVersion.ToUpper().Contains("VIRTUAL"))
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

        private static bool CheckVmFiles()
        {
            return VmFiles.Any(f => File.Exists(f));
        }

        private static bool CheckVmMac()
        {
            try
            {
                var networkInterfaces = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces();
                
                foreach (var nic in networkInterfaces)
                {
                    string mac = nic.GetPhysicalAddress().ToString();
                    if (mac.Length >= 6)
                    {
                        string macPrefix = $"{mac.Substring(0, 2)}:{mac.Substring(2, 2)}:{mac.Substring(4, 2)}";
                        
                        if (VmMacPrefixes.Any(p => p.Equals(macPrefix, StringComparison.OrdinalIgnoreCase)))
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
    }
}
