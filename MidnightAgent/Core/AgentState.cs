using System;
using System.Security.Cryptography;
using System.Text;

namespace MidnightAgent.Core
{
    public static class AgentState
    {
        public static string InstanceId { get; private set; }
        
        /// <summary>
        /// If true, this agent will execute commands.
        /// If false, it will ignore everything except /job
        /// </summary>
        // Default to false (Standby) - Wait for /job <id> or /job all
        public static bool IsActiveTarget { get; set; } = false;

        static AgentState()
        {
            GenerateInstanceId();
        }

        private static void GenerateInstanceId()
        {
            try
            {
                // Generate unique ID based on MachineGUID or Hardware info
                // For simplicity: MachineName_UserName
                string rawId = $"{Environment.MachineName}_{Environment.UserName}";
                InstanceId = rawId; // Keep it readable for /job command
            }
            catch
            {
                InstanceId = "Unknown_Agent_" + Guid.NewGuid().ToString("N").Substring(0, 4);
            }
        }
    }
}
