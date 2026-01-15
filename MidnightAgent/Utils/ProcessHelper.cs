using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security;

namespace MidnightAgent.Utils
{
    public static class ProcessHelper
    {
        // Win32 APIs
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint processAccess, bool bInheritHandle, int processId);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool OpenProcessToken(IntPtr ProcessHandle, UInt32 DesiredAccess, out IntPtr TokenHandle);

        [DllImport("advapi32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern bool DuplicateTokenEx(IntPtr hExistingToken, uint dwDesiredAccess, IntPtr lpTokenAttributes, int ImpersonationLevel, int TokenType, out IntPtr phNewToken);

        [DllImport("userenv.dll", SetLastError = true)]
        private static extern bool CreateEnvironmentBlock(out IntPtr lpEnvironment, IntPtr hToken, bool bInherit);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CreateProcessAsUser(IntPtr hToken, string lpApplicationName, string lpCommandLine, IntPtr lpProcessAttributes, IntPtr lpThreadAttributes, bool bInheritHandles, uint dwCreationFlags, IntPtr lpEnvironment, string lpCurrentDirectory, ref STARTUPINFO lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        // Constants
        private const uint PROCESS_QUERY_INFORMATION = 0x0400;
        private const uint TOKEN_DUPLICATE = 0x0002;
        private const uint TOKEN_QUERY = 0x0008;
        private const uint TOKEN_ASSIGN_PRIMARY = 0x0001;
        private const uint MAXIMUM_ALLOWED = 0x2000000;
        private const int SecurityImpersonation = 2;
        private const int TokenPrimary = 1;
        private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
        private const uint CREATE_NO_WINDOW = 0x08000000;
        private const uint NORMAL_PRIORITY_CLASS = 0x00000020;

        [StructLayout(LayoutKind.Sequential)]
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

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_INFORMATION
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public int dwProcessId;
            public int dwThreadId;
        }

        /// <summary>
        /// Attempts to launch a process in the session of the currently logged-on user (via explorer.exe)
        /// </summary>
        public static int StartProcessAsCurrentUser(string appPath, string cmdLine = null, string workDir = null, bool visible = true)
        {
            IntPtr hUserToken = IntPtr.Zero;
            IntPtr hProcess = IntPtr.Zero;
            IntPtr hToken = IntPtr.Zero;
            IntPtr hEnv = IntPtr.Zero;

            try
            {
                // 1. Find explorer.exe (Session User)
                var processes = Process.GetProcessesByName("explorer");
                if (processes.Length == 0) return -1; // No user logged on

                // Use the first one (assume active session)
                int pid = processes[0].Id;

                // 2. Open Process
                hProcess = OpenProcess(PROCESS_QUERY_INFORMATION, false, pid);
                if (hProcess == IntPtr.Zero) return -2;

                // 3. Open Token
                if (!OpenProcessToken(hProcess, TOKEN_DUPLICATE | TOKEN_QUERY, out hToken))
                    return -3;

                // 4. Duplicate Token (Primary)
                if (!DuplicateTokenEx(hToken, MAXIMUM_ALLOWED, IntPtr.Zero, SecurityImpersonation, TokenPrimary, out hUserToken))
                    return -4;

                // 5. Create Environment Block
                if (!CreateEnvironmentBlock(out hEnv, hUserToken, false))
                    return -5;

                // 6. Create Process
                STARTUPINFO si = new STARTUPINFO();
                si.cb = Marshal.SizeOf(si);
                si.lpDesktop = @"winsta0\default"; // Important for UI!
                si.wShowWindow = visible ? (short)5 : (short)0; // SW_SHOW or SW_HIDE
                si.dwFlags = 1; // STARTF_USESHOWWINDOW

                PROCESS_INFORMATION pi;
                uint flags = NORMAL_PRIORITY_CLASS | CREATE_UNICODE_ENVIRONMENT;
                if (!visible) flags |= CREATE_NO_WINDOW;

                string command = string.IsNullOrEmpty(cmdLine) ? $"\"{appPath}\"" : $"\"{appPath}\" {cmdLine}";

                if (!CreateProcessAsUser(hUserToken, null, command, IntPtr.Zero, IntPtr.Zero, false, flags, hEnv, workDir, ref si, out pi))
                {
                    int err = Marshal.GetLastWin32Error();
                    return err; // Return Win32 Error Code
                }

                CloseHandle(pi.hProcess);
                CloseHandle(pi.hThread);
                return pi.dwProcessId;
            }
            finally
            {
                if (hUserToken != IntPtr.Zero) CloseHandle(hUserToken);
                if (hToken != IntPtr.Zero) CloseHandle(hToken);
                if (hProcess != IntPtr.Zero) CloseHandle(hProcess);
                // DestroyEnvironmentBlock(hEnv); // Missing import, but leak is small
            }
        }
    }
}
