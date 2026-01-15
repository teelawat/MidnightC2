using System.Threading.Tasks;

namespace MidnightAgent.Features
{
    /// <summary>
    /// Interface for all features - implement this to add new commands
    /// </summary>
    public interface IFeature
    {
        /// <summary>
        /// Command name without slash (e.g., "screenshot")
        /// This is what users type: /screenshot
        /// </summary>
        string Command { get; }

        /// <summary>
        /// Description shown in /help command
        /// </summary>
        string Description { get; }

        /// <summary>
        /// Usage example (e.g., "/cmd <command>")
        /// </summary>
        string Usage { get; }

        /// <summary>
        /// Execute the feature with given arguments
        /// </summary>
        /// <param name="args">Arguments provided after the command</param>
        /// <returns>Result containing message and/or file to send</returns>
        Task<FeatureResult> ExecuteAsync(string[] args);
    }
}
