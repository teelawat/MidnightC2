using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using MidnightAgent.Core;

namespace MidnightAgent.Features
{
    /// <summary>
    /// /help - Show all available commands
    /// </summary>
    public class HelpFeature : IFeature
    {
        public string Command => "help";
        public string Description => "Show available commands";
        public string Usage => "/help";

        public Task<FeatureResult> ExecuteAsync(string[] args)
        {
            try
            {
                Logger.Log("HelpFeature: Generating help message...");
                
                var features = FeatureRegistry.GetAllFeatures();
                
                var sb = new StringBuilder();
                sb.AppendLine("🌙 <b>Midnight C2</b>");
                sb.AppendLine("");
                
                // Hidden commands: report, selfdestruct
                foreach (var feature in features.OrderBy(f => f.Command))
                {
                    if (feature.Command == "reboot" || feature.Command == "selfdestruct")
                        continue;

                    sb.AppendLine($"/{feature.Command} - {feature.Description}");
                }

                sb.AppendLine("");
                sb.AppendLine("<b>Offline / Special Commands:</b>");
                sb.AppendLine("<code>!update &lt;id&gt;</code> - Offline update (send exe after)");

                string message = sb.ToString();
                Logger.Log($"HelpFeature: Generated message, length={message.Length}");
                
                return Task.FromResult(FeatureResult.Ok(message));
            }
            catch (Exception ex)
            {
                Logger.Log($"HelpFeature ERROR: {ex.Message}");
                return Task.FromResult(FeatureResult.Fail($"Error: {ex.Message}"));
            }
        }
    }
}
