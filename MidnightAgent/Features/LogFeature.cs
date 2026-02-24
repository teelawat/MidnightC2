using System;
using System.Text;
using System.Threading.Tasks;
using MidnightAgent.Core;
using MidnightAgent.Telegram;

namespace MidnightAgent.Features
{
    /// <summary>
    /// View or clear agent activity logs
    /// /log - View logs
    /// /log clear - Delete log file
    /// </summary>
    public class LogFeature : IFeature
    {
        public static TelegramService TelegramInstance { get; set; }

        public string Command => "log";
        public string Description => "View agent activity logs";
        public string Usage => "/log | /log clear";

        public async Task<FeatureResult> ExecuteAsync(string[] args)
        {
            if (args.Length > 0 && args[0].Equals("clear", StringComparison.OrdinalIgnoreCase))
            {
                Logger.Clear();
                return FeatureResult.Ok("🗑️ <b>Agent logs cleared.</b>");
            }

            string logContent = Logger.Read();
            
            if (string.IsNullOrEmpty(logContent) || logContent == "Log file empty or not found.")
            {
                return FeatureResult.Ok("ℹ️ <b>Log file is empty.</b>");
            }

            // If log is too large, send as file
            if (logContent.Length > 3500)
            {
                byte[] logBytes = Encoding.UTF8.GetBytes(logContent);
                string agentId = AgentInfo.Id;
                string fileName = $"log_{agentId}_{DateTime.Now:yyyyMMdd_HHmmss}.txt";
                
                return new FeatureResult 
                { 
                    Success = true, 
                    FileData = logBytes, 
                    FileName = fileName,
                    Message = "📄 <b>Log is too large, sending as file...</b>" 
                };
            }

            return FeatureResult.Ok($"📜 <b>Agent Logs:</b>\n\n<code>{logContent}</code>");
        }
    }
}
