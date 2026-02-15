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
        public static string OS => Environment.OSVersion.ToString();
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
            return $@"🌙 Midnight Agent #{Id}

🖥️ Hostname: {Hostname}
👤 Username: {Username}
🌐 IP: {IP}
💻 OS: {OS}
📊 Bits: {Bits}
🔧 CPU: {CPU}
🔐 Admin: {(IsAdmin ? "Yes ✅" : "No ❌")}
⚡ SYSTEM: {(IsSystem ? "Yes ✅" : "No ❌")}
📦 Version: {Version}";
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
