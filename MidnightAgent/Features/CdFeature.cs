using System;
using System.IO;
using System.Threading.Tasks;

namespace MidnightAgent.Features
{
    /// <summary>
    /// /cd - Change current directory
    /// </summary>
    public class CdFeature : IFeature
    {
        public string Command => "cd";
        public string Description => "Change current directory";
        public string Usage => "/cd <path>";

        public Task<FeatureResult> ExecuteAsync(string[] args)
        {
            if (args.Length == 0)
            {
                string current = Directory.GetCurrentDirectory();
                return Task.FromResult(FeatureResult.Ok($"📁 Current: {current}"));
            }

            string path = string.Join(" ", args);
            path = Environment.ExpandEnvironmentVariables(path);

            try
            {
                if (Directory.Exists(path))
                {
                    Directory.SetCurrentDirectory(path);
                    return Task.FromResult(FeatureResult.Ok($"✅ Changed to: {path}"));
                }
                else
                {
                    return Task.FromResult(FeatureResult.Fail($"Directory not found: {path}"));
                }
            }
            catch (Exception ex)
            {
                return Task.FromResult(FeatureResult.Fail($"Error: {ex.Message}"));
            }
        }
    }
}
