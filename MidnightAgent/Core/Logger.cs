using System;
using System.IO;

namespace MidnightAgent.Core
{
    /// <summary>
    /// Simple file logger for debugging
    /// </summary>
    public static class Logger
    {
        private static readonly string LogPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Microsoft", "Windows", "Temp", "midnight.log"
        );
        
        private static readonly object _lock = new object();
        private static bool _enabled = true;

        public static void Log(string message)
        {
            if (!_enabled) return;

            try
            {
                lock (_lock)
                {
                    // Ensure directory exists
                    string directory = Path.GetDirectoryName(LogPath);
                    if (!Directory.Exists(directory))
                    {
                        Directory.CreateDirectory(directory);
                    }

                    string logMessage = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}";
                    File.AppendAllText(LogPath, logMessage + Environment.NewLine);
                    
                    // Keep log file size under 1MB
                    var fileInfo = new FileInfo(LogPath);
                    if (fileInfo.Exists && fileInfo.Length > 1024 * 1024)
                    {
                        // Rotate log - keep old one as .old
                        string oldPath = LogPath + ".old";
                        if (File.Exists(oldPath))
                        {
                            File.Delete(oldPath);
                        }
                        File.Move(LogPath, oldPath);
                    }
                }
            }
            catch (Exception ex)
            {
                // Last resort - write to Debug
                System.Diagnostics.Debug.WriteLine($"Logger error: {ex.Message}");
            }
        }

        public static void Disable()
        {
            _enabled = false;
        }

        public static string GetLogPath() => LogPath;
    }
}
