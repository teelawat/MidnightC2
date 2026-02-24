using System;
using System.Runtime.InteropServices;

namespace MidnightAgent.Utils
{
    /// <summary>
    /// Windows API helper for window manipulation
    /// </summary>
    public static class WindowHelper
    {
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        private const int SW_HIDE = 0;
        private const int SW_SHOW = 5;
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOZORDER = 0x0004;

        /// <summary>
        /// Hide a window by its handle
        /// </summary>
        public static bool HideWindow(IntPtr windowHandle)
        {
            try
            {
                // Move off-screen first for extra stealth
                MoveOffScreen(windowHandle);
                return ShowWindow(windowHandle, SW_HIDE);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Move window to a coordinate far outside the visible area
        /// </summary>
        public static bool MoveOffScreen(IntPtr windowHandle)
        {
            try
            {
                return SetWindowPos(windowHandle, IntPtr.Zero, -32000, -32000, 0, 0, SWP_NOSIZE | SWP_NOZORDER);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Show a window by its handle
        /// </summary>
        public static bool ShowWindowHandle(IntPtr windowHandle)
        {
            try
            {
                return ShowWindow(windowHandle, SW_SHOW);
            }
            catch
            {
                return false;
            }
        }
    }
}
