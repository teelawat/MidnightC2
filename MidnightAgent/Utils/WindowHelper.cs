using System;
using System.Runtime.InteropServices;

namespace MidnightAgent.Utils
{
    /// <summary>
    /// Windows API helper for window manipulation
    /// </summary>
    public static class WindowHelper
    {
        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        private const int SW_HIDE = 0;
        private const int SW_SHOW = 5;

        /// <summary>
        /// Hide a window by its handle
        /// </summary>
        public static bool HideWindow(IntPtr windowHandle)
        {
            try
            {
                return ShowWindow(windowHandle, SW_HIDE);
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
