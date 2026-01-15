namespace MidnightAgent.Features
{
    /// <summary>
    /// Result of a feature execution
    /// </summary>
    public class FeatureResult
    {
        /// <summary>
        /// Whether the execution was successful
        /// </summary>
        public bool Success { get; set; }

        /// <summary>
        /// Message to send to user (text response)
        /// </summary>
        public string Message { get; set; }

        /// <summary>
        /// Path to file to send (optional)
        /// </summary>
        public string FilePath { get; set; }

        /// <summary>
        /// Raw file data to send (optional, alternative to FilePath)
        /// </summary>
        public byte[] FileData { get; set; }

        /// <summary>
        /// Filename for FileData (required if FileData is set)
        /// </summary>
        public string FileName { get; set; }

        /// <summary>
        /// Whether to delete the file after sending
        /// </summary>
        public bool DeleteFileAfterSend { get; set; } = true;

        // Static factory methods for convenience

        public static FeatureResult Ok(string message = null)
        {
            return new FeatureResult { Success = true, Message = message };
        }

        public static FeatureResult Fail(string message)
        {
            return new FeatureResult { Success = false, Message = message };
        }

        public static FeatureResult File(string path, string caption = null)
        {
            return new FeatureResult 
            { 
                Success = true, 
                FilePath = path, 
                Message = caption,
                DeleteFileAfterSend = true
            };
        }

        public static FeatureResult WithFileData(byte[] data, string fileName, string caption = null)
        {
            return new FeatureResult
            {
                Success = true,
                FileData = data,
                FileName = fileName,
                Message = caption
            };
        }
    }
}
