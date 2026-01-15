using System;
using MidnightAgent.Core;

namespace MidnightAgent.Telegram
{
    /// <summary>
    /// Helper class for sending messages to Telegram
    /// </summary>
    public static class MessageHelper
    {
        /// <summary>
        /// Format system info message
        /// </summary>
        public static string FormatSystemInfo()
        {
            return AgentInfo.GetFullInfo();
        }

        /// <summary>
        /// Format success message
        /// </summary>
        public static string FormatSuccess(string action)
        {
            return $"✅ {action}";
        }

        /// <summary>
        /// Format error message
        /// </summary>
        public static string FormatError(string error)
        {
            return $"❌ Error: {error}";
        }

        /// <summary>
        /// Truncate long text for Telegram (max 4096 chars)
        /// </summary>
        public static string Truncate(string text, int maxLength = 4000)
        {
            if (string.IsNullOrEmpty(text))
                return text;

            if (text.Length <= maxLength)
                return text;

            return text.Substring(0, maxLength) + "\n\n... (truncated)";
        }

        /// <summary>
        /// Escape HTML special characters
        /// </summary>
        public static string EscapeHtml(string text)
        {
            if (string.IsNullOrEmpty(text))
                return text;

            return text
                .Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;");
        }

        /// <summary>
        /// Format code block
        /// </summary>
        public static string FormatCode(string code)
        {
            return $"<pre>{EscapeHtml(code)}</pre>";
        }
    }
}
