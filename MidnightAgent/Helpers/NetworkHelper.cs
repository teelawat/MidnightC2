using System.Net;

namespace MidnightAgent.Helpers
{
    /// <summary>
    /// Network utilities
    /// </summary>
    public static class NetworkHelper
    {
        /// <summary>
        /// Get public IP address
        /// </summary>
        public static string GetPublicIP()
        {
            try
            {
                using (var client = new WebClient())
                {
                    // Try multiple services
                    string[] services = new[]
                    {
                        "https://api.ipify.org",
                        "https://icanhazip.com",
                        "https://ifconfig.me/ip"
                    };

                    foreach (var service in services)
                    {
                        try
                        {
                            string ip = client.DownloadString(service).Trim();
                            if (!string.IsNullOrEmpty(ip) && ip.Length < 50)
                            {
                                return ip;
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }

            return "Unknown";
        }

        /// <summary>
        /// Check if internet is available
        /// </summary>
        public static bool IsInternetAvailable()
        {
            try
            {
                using (var client = new WebClient())
                {
                    using (client.OpenRead("https://www.google.com"))
                    {
                        return true;
                    }
                }
            }
            catch
            {
                return false;
            }
        }
    }
}
