using System;
using System.Threading.Tasks;

namespace MidnightAgent.Features
{
    public class MenuFeature : IFeature
    {
        public string Command => "menu";
        public string Description => "Show interactive buttons";
        public string Usage => "/menu";

        public Task<FeatureResult> ExecuteAsync(string[] args)
        {
            // Keyword "OPEN_MENU" handles by CommandRouter
            return Task.FromResult(FeatureResult.Ok("OPEN_MENU"));
        }
    }
}
