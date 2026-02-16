using Microsoft.Win32;
using System;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using MidnightAgent.Telegram;

namespace MidnightAgent.Core
{
    /// <summary>
    /// Monitors Power State (Sleep/Wake) and Session State (Lock/Unlock).
    /// Uses a hidden Form running on a separate STA thread to ensure Message Loop exists for SystemEvents.
    /// </summary>
    public static class PowerWatchdog
    {
        private static TelegramService _telegram;
        private static Thread _monitorThread;
        private static HiddenMonitorForm _hiddenForm;

        public static void Start(TelegramService telegram)
        {
            if (_monitorThread != null && _monitorThread.IsAlive) return;
            
            _telegram = telegram;
            
            // Start monitoring on a separate STA thread with Message Loop
            _monitorThread = new Thread(MonitorLoop);
            _monitorThread.SetApartmentState(ApartmentState.STA);
            _monitorThread.IsBackground = true;
            _monitorThread.Name = "PowerWatchdogThread";
            _monitorThread.Start();
            
            Logger.Log("PowerWatchdog started monitoring.");
        }

        public static void Stop()
        {
            if (_hiddenForm != null && !_hiddenForm.IsDisposed)
            {
                _hiddenForm.Invoke(new Action(() => _hiddenForm.Close()));
            }
        }

        private static void MonitorLoop()
        {
            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                
                _hiddenForm = new HiddenMonitorForm(_telegram);
                Application.Run(_hiddenForm);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"PowerWatchdog Thread Error: {ex}");
            }
        }
    }

    /// <summary>
    /// Invisible Form to receive SystemEvents
    /// </summary>
    internal class HiddenMonitorForm : Form
    {
        private readonly TelegramService _telegram;
        private DateTime _lastSleepTime = DateTime.MinValue;

        public HiddenMonitorForm(TelegramService telegram)
        {
            _telegram = telegram;

            // Make form invisible
            this.ShowInTaskbar = false;
            this.WindowState = FormWindowState.Minimized;
            this.FormBorderStyle = FormBorderStyle.None;
            this.Opacity = 0;
            this.Size = new System.Drawing.Size(0, 0);

            // Subscribe to events
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            SystemEvents.SessionSwitch += OnSessionSwitch;
            SystemEvents.SessionEnding += OnSessionEnding;
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            this.Visible = false;      // Hide completely
            this.Hide();               // Double ensure hidden
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            SystemEvents.SessionEnding -= OnSessionEnding;
            base.OnFormClosing(e);
        }

        private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            try
            {
                if (e.Mode == PowerModes.Suspend)
                {
                    // Machine is going to sleep
                    _lastSleepTime = DateTime.Now;
                    Debug.WriteLine("PowerWatchdog: System Suspending");
                    
                    // Fire-and-forget notification (must be fast!)
                    string msg = $"💤 <b>System Sleeping...</b>\n" +
                                 $"Hostname: <code>{Environment.MachineName}</code>\n" +
                                 $"User: <code>{Environment.UserName}</code>\n" +
                                 $"Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}";
                    
                    try { _telegram?.SendMessage(msg); } catch {}
                }
                else if (e.Mode == PowerModes.Resume)
                {
                    // Machine just woke up
                    TimeSpan sleepDuration = DateTime.Now - _lastSleepTime;
                    string durationStr = _lastSleepTime == DateTime.MinValue 
                        ? "unknown duration" 
                        : $"{sleepDuration.Hours}h {sleepDuration.Minutes}m";

                    string msg = $"☀️ <b>System Woke Up!</b>\n" +
                                 $"Hostname: <code>{Environment.MachineName}</code>\n" +
                                 $"User: <code>{Environment.UserName}</code>\n" +
                                 $"Asleep for: {durationStr}\n" +
                                 $"Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}";

                    WaitForNetworkAndSend(msg);
                }
            }
            catch { }
        }

        private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
        {
            try
            {
                string action = "";
                switch (e.Reason)
                {
                    case SessionSwitchReason.SessionUnlock:
                        action = "🔓 User Unlocked Screen";
                        break;
                    case SessionSwitchReason.ConsoleConnect:
                        action = "🖥️ Console Connected (Monitor On)";
                        break;
                    case SessionSwitchReason.RemoteConnect:
                        action = "🌐 Remote RDP Connected";
                        break;
                    case SessionSwitchReason.SessionLogon:
                        action = "👤 User Logged On";
                        break;
                    case SessionSwitchReason.SessionLock:
                        action = "🔒 User Locked Screen";
                        break;
                    case SessionSwitchReason.ConsoleDisconnect:
                        action = "⬛ Console Disconnected (Monitor Off)";
                        break;
                    case SessionSwitchReason.RemoteDisconnect:
                        action = "🔌 Remote RDP Disconnected";
                        break;
                }

                if (!string.IsNullOrEmpty(action))
                {
                    string msg = $"🔔 <b>User Activity Detected!</b>\n" +
                                 $"Action: <b>{action}</b>\n" +
                                 $"Hostname: <code>{Environment.MachineName}</code>\n" +
                                 $"User: <code>{Environment.UserName}</code>\n" +
                                 $"Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}";

                    _telegram?.SendMessage(msg);
                }
            }
            catch { }
        }

        private void OnSessionEnding(object sender, SessionEndingEventArgs e)
        {
            try
            {
                string reason = e.Reason == SessionEndReasons.SystemShutdown ? "🛑 System Shutdown" : "👋 User Logoff";
                
                string msg = $"<b>{reason} Initiated!</b>\n" +
                             $"Hostname: <code>{Environment.MachineName}</code>\n" +
                             $"User: <code>{Environment.UserName}</code>\n" +
                             $"Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}";

                // Fire-and-forget (network dies fast during shutdown)
                try { _telegram?.SendMessage(msg); } catch {}
            }
            catch { }
        }

        private void WaitForNetworkAndSend(string message)
        {
            Task.Run(async () =>
            {
                // Wait up to 30 seconds for network (check every 3s)
                for (int i = 0; i < 10; i++)
                {
                    if (NetworkInterface.GetIsNetworkAvailable())
                    {
                        await Task.Delay(3000); // Stabilize
                        _telegram?.SendMessage(message);
                        return;
                    }
                    await Task.Delay(3000);
                }
            });
        }
    }
}

