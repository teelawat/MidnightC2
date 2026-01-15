using System;
using System.Runtime.InteropServices;
using System.Diagnostics;

namespace MidnightAgent.Security
{
    /// <summary>
    /// Allows SYSTEM to spawn processes as the logged-in user
    /// </summary>
    public static class TokenImpersonator
    {
        #region P/Invoke

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr ProcessHandle, uint DesiredAccess, out IntPtr TokenHandle);

        [DllImport("advapi32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern bool DuplicateTokenEx(IntPtr hExistingToken, uint dwDesiredAccess, IntPtr lpTokenAttributes, int ImpersonationLevel, int TokenType, out IntPtr phNewToken);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CreateProcessAsUserW(IntPtr hToken, string lpApplicationName, string lpCommandLine, IntPtr lpProcessAttributes, IntPtr lpThreadAttributes, bool bInheritHandles, uint dwCreationFlags, IntPtr lpEnvironment, string lpCurrentDirectory, ref STARTUPINFO lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

        [DllImport("userenv.dll", SetLastError = true)]
        private static extern bool CreateEnvironmentBlock(out IntPtr lpEnvironment, IntPtr hToken, bool bInherit);

        [DllImport("userenv.dll", SetLastError = true)]
        private static extern bool DestroyEnvironmentBlock(IntPtr lpEnvironment);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CreatePipe(out IntPtr hReadPipe, out IntPtr hWritePipe, ref SECURITY_ATTRIBUTES lpPipeAttributes, uint nSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetHandleInformation(IntPtr hObject, uint dwMask, uint dwFlags);

        // For creating restricted (non-admin) token
        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool CreateRestrictedToken(
            IntPtr ExistingTokenHandle,
            uint Flags,
            uint DisableSidCount,
            IntPtr SidsToDisable,
            uint DeletePrivilegeCount,
            IntPtr PrivilegesToDelete,
            uint RestrictedSidCount,
            IntPtr SidsToRestrict,
            out IntPtr NewTokenHandle);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool ConvertStringSidToSidW(
            [MarshalAs(UnmanagedType.LPWStr)] string StringSid,
            out IntPtr Sid);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern IntPtr FreeSid(IntPtr pSid);

        [StructLayout(LayoutKind.Sequential)]
        private struct SID_AND_ATTRIBUTES
        {
            public IntPtr Sid;
            public uint Attributes;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SECURITY_ATTRIBUTES
        {
            public int nLength;
            public IntPtr lpSecurityDescriptor;
            public bool bInheritHandle;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_INFORMATION
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public int dwProcessId;
            public int dwThreadId;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct STARTUPINFO
        {
            public int cb;
            public string lpReserved;
            public string lpDesktop;
            public string lpTitle;
            public int dwX;
            public int dwY;
            public int dwXSize;
            public int dwYSize;
            public int dwXCountChars;
            public int dwYCountChars;
            public int dwFillAttribute;
            public int dwFlags;
            public short wShowWindow;
            public short cbReserved2;
            public IntPtr lpReserved2;
            public IntPtr hStdInput;
            public IntPtr hStdOutput;
            public IntPtr hStdError;
        }

        private const uint TOKEN_DUPLICATE = 0x0002;
        private const uint TOKEN_QUERY = 0x0008;
        private const uint TOKEN_ASSIGN_PRIMARY = 0x0001;
        private const uint TOKEN_ALL_ACCESS = 0xF01FF;
        private const uint MAXIMUM_ALLOWED = 0x2000000;
        private const int SecurityImpersonation = 2;
        private const int TokenPrimary = 1;
        private const uint CREATE_NO_WINDOW = 0x08000000;
        private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
        private const int STARTF_USESTDHANDLES = 0x00000100;
        private const int STARTF_USESHOWWINDOW = 0x00000001;
        private const short SW_HIDE = 0;
        private const uint PROCESS_QUERY_INFORMATION = 0x0400;
        private const uint HANDLE_FLAG_INHERIT = 0x00000001;
        
        // Admin group SID
        private const string DOMAIN_ALIAS_RID_ADMINS = "S-1-5-32-544"; // BUILTIN\Administrators
        private const uint SE_GROUP_USE_FOR_DENY_ONLY = 0x00000010;
        private const uint DISABLE_MAX_PRIVILEGE = 0x1;
        private const uint LUA_TOKEN = 0x4;

        #endregion

        /// <summary>
        /// Gets the duplicated token of the logged-in user (from explorer.exe)
        /// with admin privileges stripped (restricted token)
        /// </summary>
        public static IntPtr GetUserToken()
        {
            IntPtr hToken = IntPtr.Zero;
            IntPtr hDupToken = IntPtr.Zero;
            IntPtr hRestrictedToken = IntPtr.Zero;

            try
            {
                var explorers = Process.GetProcessesByName("explorer");
                if (explorers.Length == 0) return IntPtr.Zero;

                IntPtr hProcess = OpenProcess(PROCESS_QUERY_INFORMATION, false, explorers[0].Id);
                if (hProcess == IntPtr.Zero) return IntPtr.Zero;

                try
                {
                    if (!OpenProcessToken(hProcess, TOKEN_DUPLICATE | TOKEN_QUERY, out hToken))
                        return IntPtr.Zero;

                    if (!DuplicateTokenEx(hToken, TOKEN_ALL_ACCESS, IntPtr.Zero, SecurityImpersonation, TokenPrimary, out hDupToken))
                        return IntPtr.Zero;

                    // Create restricted token (strip admin privileges)
                    // Use LUA_TOKEN flag to create a limited user account token
                    if (CreateRestrictedToken(
                        hDupToken,
                        LUA_TOKEN, // Create a filtered admin token (like UAC does for non-elevated)
                        0, IntPtr.Zero, // No SIDs to disable
                        0, IntPtr.Zero, // No privileges to delete
                        0, IntPtr.Zero, // No restricted SIDs
                        out hRestrictedToken))
                    {
                        CloseHandle(hDupToken); // We don't need the unrestricted one
                        return hRestrictedToken;
                    }
                    
                    // If CreateRestrictedToken failed, try alternate approach
                    // Disable the Administrators SID
                    IntPtr pAdminSid = IntPtr.Zero;
                    if (ConvertStringSidToSidW(DOMAIN_ALIAS_RID_ADMINS, out pAdminSid))
                    {
                        var sidToDisable = new SID_AND_ATTRIBUTES
                        {
                            Sid = pAdminSid,
                            Attributes = SE_GROUP_USE_FOR_DENY_ONLY
                        };

                        IntPtr pSidArray = Marshal.AllocHGlobal(Marshal.SizeOf(sidToDisable));
                        try
                        {
                            Marshal.StructureToPtr(sidToDisable, pSidArray, false);
                            
                            if (CreateRestrictedToken(
                                hDupToken,
                                DISABLE_MAX_PRIVILEGE, // Strip most privileges
                                1, pSidArray, // Disable admin SID
                                0, IntPtr.Zero,
                                0, IntPtr.Zero,
                                out hRestrictedToken))
                            {
                                CloseHandle(hDupToken);
                                return hRestrictedToken;
                            }
                        }
                        finally
                        {
                            Marshal.FreeHGlobal(pSidArray);
                            FreeSid(pAdminSid);
                        }
                    }

                    // Fallback to the duplicated token (still better than nothing)
                    return hDupToken;
                }
                finally
                {
                    CloseHandle(hProcess);
                    if (hToken != IntPtr.Zero) CloseHandle(hToken);
                }
            }
            catch
            {
                return IntPtr.Zero;
            }
        }

        /// <summary>
        /// Creates a process as the logged-in user with redirected I/O
        /// </summary>
        public static bool CreateProcessAsUser(
            string commandLine,
            out IntPtr hProcess,
            out IntPtr hStdinWrite,
            out IntPtr hStdoutRead,
            out IntPtr hStderrRead)
        {
            hProcess = IntPtr.Zero;
            hStdinWrite = IntPtr.Zero;
            hStdoutRead = IntPtr.Zero;
            hStderrRead = IntPtr.Zero;

            IntPtr hToken = GetUserToken();
            if (hToken == IntPtr.Zero) return false;

            IntPtr hStdinReadTmp = IntPtr.Zero;
            IntPtr hStdoutWriteTmp = IntPtr.Zero;
            IntPtr hStderrWriteTmp = IntPtr.Zero;
            IntPtr lpEnvironment = IntPtr.Zero;

            try
            {
                // Create pipes
                var sa = new SECURITY_ATTRIBUTES
                {
                    nLength = Marshal.SizeOf(typeof(SECURITY_ATTRIBUTES)),
                    bInheritHandle = true,
                    lpSecurityDescriptor = IntPtr.Zero
                };

                // Stdin pipe
                if (!CreatePipe(out hStdinReadTmp, out hStdinWrite, ref sa, 0))
                    return false;
                SetHandleInformation(hStdinWrite, HANDLE_FLAG_INHERIT, 0);

                // Stdout pipe
                if (!CreatePipe(out hStdoutRead, out hStdoutWriteTmp, ref sa, 0))
                    return false;
                SetHandleInformation(hStdoutRead, HANDLE_FLAG_INHERIT, 0);

                // Stderr pipe
                if (!CreatePipe(out hStderrRead, out hStderrWriteTmp, ref sa, 0))
                    return false;
                SetHandleInformation(hStderrRead, HANDLE_FLAG_INHERIT, 0);

                // Create environment block
                CreateEnvironmentBlock(out lpEnvironment, hToken, false);

                var si = new STARTUPINFO
                {
                    cb = Marshal.SizeOf(typeof(STARTUPINFO)),
                    dwFlags = STARTF_USESTDHANDLES | STARTF_USESHOWWINDOW,
                    wShowWindow = SW_HIDE,
                    hStdInput = hStdinReadTmp,
                    hStdOutput = hStdoutWriteTmp,
                    hStdError = hStderrWriteTmp,
                    lpDesktop = "winsta0\\default"
                };

                PROCESS_INFORMATION pi;
                bool success = CreateProcessAsUserW(
                    hToken,
                    null,
                    commandLine,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    true,
                    CREATE_NO_WINDOW | CREATE_UNICODE_ENVIRONMENT,
                    lpEnvironment,
                    null,
                    ref si,
                    out pi);

                if (success)
                {
                    hProcess = pi.hProcess;
                    CloseHandle(pi.hThread);

                    // Close child-side handles
                    CloseHandle(hStdinReadTmp);
                    CloseHandle(hStdoutWriteTmp);
                    CloseHandle(hStderrWriteTmp);

                    return true;
                }

                return false;
            }
            catch
            {
                return false;
            }
            finally
            {
                CloseHandle(hToken);
                if (lpEnvironment != IntPtr.Zero) DestroyEnvironmentBlock(lpEnvironment);
            }
        }

        /// <summary>
        /// Check if we can impersonate (are we SYSTEM or ADMIN?)
        /// </summary>
        public static bool CanImpersonate()
        {
            return Installation.PrivilegeHelper.IsSystem() || Installation.PrivilegeHelper.IsAdmin();
        }
    }
}
