using System;
using System.Security.Principal;
using System.Runtime.InteropServices;
using System.Diagnostics;

namespace MidnightAgent.Installation
{
    /// <summary>
    /// Helper class for privilege checking and elevation
    /// </summary>
    public static class PrivilegeHelper
    {
        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr ProcessHandle, uint DesiredAccess, out IntPtr TokenHandle);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        private const uint TOKEN_QUERY = 0x0008;

        /// <summary>
        /// Check if running as Administrator
        /// </summary>
        public static bool IsAdmin()
        {
            try
            {
                using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                {
                    WindowsPrincipal principal = new WindowsPrincipal(identity);
                    return principal.IsInRole(WindowsBuiltInRole.Administrator);
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Check if running as SYSTEM
        /// </summary>
        public static bool IsSystem()
        {
            try
            {
                using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                {
                    return identity.IsSystem || 
                           identity.User?.Value == "S-1-5-18" ||
                           identity.Name.EndsWith("$") ||
                           identity.Name.ToUpper().Contains("SYSTEM");
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Get current privilege level as string
        /// </summary>
        public static string GetPrivilegeLevel()
        {
            if (IsSystem()) return "SYSTEM";
            if (IsAdmin()) return "Administrator";
            return "User";
        }

        /// <summary>
        /// Get the username of the currently logged-in user (for SYSTEM to impersonate)
        /// </summary>
        public static string GetLoggedInUser()
        {
            try
            {
                // Try to find explorer.exe owner
                foreach (var proc in Process.GetProcessesByName("explorer"))
                {
                    try
                    {
                        string owner = GetProcessOwner(proc.Id);
                        if (!string.IsNullOrEmpty(owner) && !owner.Contains("SYSTEM"))
                        {
                            // Return just username without domain
                            if (owner.Contains("\\"))
                            {
                                return owner.Split('\\')[1];
                            }
                            return owner;
                        }
                    }
                    catch { }
                }

                // Fallback: query WMI
                return GetLoggedInUserViaWMI();
            }
            catch
            {
                return null;
            }
        }

        private static string GetProcessOwner(int processId)
        {
            try
            {
                var query = $"SELECT * FROM Win32_Process WHERE ProcessId = {processId}";
                using (var searcher = new System.Management.ManagementObjectSearcher(query))
                {
                    foreach (System.Management.ManagementObject obj in searcher.Get())
                    {
                        string[] args = new string[] { string.Empty, string.Empty };
                        int returnVal = Convert.ToInt32(obj.InvokeMethod("GetOwner", args));
                        if (returnVal == 0)
                        {
                            return $"{args[1]}\\{args[0]}"; // Domain\User
                        }
                    }
                }
            }
            catch { }
            return null;
        }

        private static string GetLoggedInUserViaWMI()
        {
            try
            {
                using (var searcher = new System.Management.ManagementObjectSearcher(
                    "SELECT UserName FROM Win32_ComputerSystem"))
                {
                    foreach (System.Management.ManagementObject obj in searcher.Get())
                    {
                        string username = obj["UserName"]?.ToString();
                        if (!string.IsNullOrEmpty(username))
                        {
                            if (username.Contains("\\"))
                            {
                                return username.Split('\\')[1];
                            }
                            return username;
                        }
                    }
                }
            }
            catch { }
            return null;
        }
    }
}
