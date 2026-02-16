using Microsoft.Win32;
using System;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using MidnightAgent.Telegram;

namespace MidnightAgent.Core
{
    /// <summary>
    /// Monitors Power State (Sleep/Wake) and Session State (Lock/Unlock/Logon).
    /// Uses WTS API to monitor session changes across all sessions (works for SYSTEM).
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
            
            _monitorThread = new Thread(MonitorLoop);
            _monitorThread.SetApartmentState(ApartmentState.STA);
            _monitorThread.IsBackground = true;
            _monitorThread.Name = "PowerWatchdogThread";
            _monitorThread.Start();
            
            Logger.Log("PowerWatchdog (SYSTEM-Compatible) started.");
        }

        public static void Stop()
        {
            if (_hiddenForm != null && !_hiddenForm.IsDisposed)
            {
                try { _hiddenForm.Invoke(new Action(() => _hiddenForm.Close())); } catch {}
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
    /// Invisible Form that registers for WTS Session Notifications
    /// </summary>
    internal class HiddenMonitorForm : Form
    {
        private readonly TelegramService _telegram;
        private DateTime _lastSleepTime = DateTime.MinValue;

        // WTS Constants
        private const int NOTIFY_FOR_ALL_SESSIONS = 1;
        private const int WM_WTSSESSION_CHANGE = 0x02B1;
        
        // Session Event Codes
        private const int WTS_CONSOLE_CONNECT = 0x1;
        private const int WTS_CONSOLE_DISCONNECT = 0x2;
        private const int WTS_REMOTE_CONNECT = 0x3;
        private const int WTS_REMOTE_DISCONNECT = 0x4;
        private const int WTS_SESSION_LOGON = 0x5;
        private const int WTS_SESSION_LOGOFF = 0x6;
        private const int WTS_SESSION_LOCK = 0x7;
        private const int WTS_SESSION_UNLOCK = 0x8;

        [DllImport("wtsapi32.dll")]
        private static extern bool WTSRegisterSessionNotification(IntPtr hWnd, int dwFlags);

        [DllImport("wtsapi32.dll")]
        private static extern bool WTSUnRegisterSessionNotification(IntPtr hWnd);

        public HiddenMonitorForm(TelegramService telegram)
        {
            _telegram = telegram;

            this.ShowInTaskbar = false;
            this.WindowState = FormWindowState.Minimized;
            this.FormBorderStyle = FormBorderStyle.None;
            this.Opacity = 0;
            this.Size = new System.Drawing.Size(0, 0);

            // Subscribe to System-wide Power Events
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            SystemEvents.SessionEnding += OnSessionEnding;
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            this.Visible = false;      // Hide completely
            this.Hide();               // Double ensure hidden
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // Register for ALL session notifications (vital for SYSTEM service)
            WTSRegisterSessionNotification(this.Handle, NOTIFY_FOR_ALL_SESSIONS);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            WTSUnRegisterSessionNotification(this.Handle);
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            SystemEvents.SessionEnding -= OnSessionEnding;
            base.OnFormClosing(e);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_WTSSESSION_CHANGE)
            {
                int eventCode = m.WParam.ToInt32();
                // int sessionId = m.LParam.ToInt32(); // Can log session ID if needed

                HandleSessionEvent(eventCode);
            }
            
            base.WndProc(ref m);
        }

        private void HandleSessionEvent(int eventCode)
        {
            try
            {
                string action = "";
                switch (eventCode)
                {
                    case WTS_SESSION_UNLOCK:
                        action = "� User Unlocked Screen";
                        break;
                    case WTS_SESSION_LOCK:
                        action = "🔒 User Locked Screen";
                        break;
                    case WTS_CONSOLE_CONNECT:
                        action = "🖥️ Monitor/Console Connected";
                        break;
                    case WTS_CONSOLE_DISCONNECT:
                        action = "⬛ Monitor/Console Disconnected";
                        break;
                    case WTS_SESSION_LOGON:
                        action = "👤 User Logged On";
                        break;
                    case WTS_SESSION_LOGOFF: // Often redundant with SessionEnding, but good to have
                        // action = "👋 User Logged Off"; 
                        break;
                    case WTS_REMOTE_CONNECT:
                        action = "🌐 RDP Connected";
                        break;
                }

                if (!string.IsNullOrEmpty(action))
                {
                    string msg = $"🔔 <b>Activity Detected (WTS)</b>\n" +
                                 $"Action: <b>{action}</b>\n" +
                                 $"Hostname: <code>{Environment.MachineName}</code>\n" +
                                 $"Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}";

                    // WTS events come from OS, so network should be fine usually
                    _telegram?.SendMessage(msg);
                }
            }
            catch {}
        }

        private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            try
            {
                if (e.Mode == PowerModes.Suspend)
                {
                    _lastSleepTime = DateTime.Now;
                    Debug.WriteLine("PowerWatchdog: System Suspending");
                    
                    // Fire-and-forget notification (must be fast!)
                    string msg = $"� <b>System Sleeping...</b>\n" +
                                 $"Hostname: <code>{Environment.MachineName}</code>\n" +
                                 $"Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}";
                    
                    try { _telegram?.SendMessage(msg); } catch {}
                }
                else if (e.Mode == PowerModes.Resume)
                {
                    // Machine just woke up
                    TimeSpan sleepDuration = DateTime.Now - _lastSleepTime;
                    string durationStr = _lastSleepTime == DateTime.MinValue 
                        ? "unknown" 
                        : $"{sleepDuration.Hours}h {sleepDuration.Minutes}m";

                    string msg = $"☀️ <b>System Woke Up!</b>\n" +
                                 $"Hostname: <code>{Environment.MachineName}</code>\n" +
                                 $"Asleep for: {durationStr}\n" +
                                 $"Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}";

                    WaitForNetworkAndSend(msg);
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
                // Wait up to 30 seconds for network (check every 2s)
                for (int i = 0; i < 15; i++)
                {
                    if (NetworkInterface.GetIsNetworkAvailable())
                    {
                        await Task.Delay(3000); // Stabilize
                        _telegram?.SendMessage(message);
                        return;
                    }
                    await Task.Delay(2000);
                }
            });
        }
    }
}
