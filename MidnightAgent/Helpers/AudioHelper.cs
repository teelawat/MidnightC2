using System;
using NAudio.Wave;
using Concentus.Enums;
using Concentus.Structs;

namespace MidnightAgent.Helpers
{
    public class AudioHelper : IDisposable
    {
        private WaveInEvent _waveIn;
        private OpusEncoder _encoder;
        private bool _isRecording = false;

        public event Action<byte[]> OnEncodedData;

        public AudioHelper(int sampleRate = 48000, int channels = 1)
        {
            _waveIn = new WaveInEvent
            {
                WaveFormat = new WaveFormat(sampleRate, 16, channels),
                BufferMilliseconds = 20 // Standard Opus frame size
            };

            _waveIn.DataAvailable += OnDataAvailable;

            // Opus Encoder
            _encoder = new OpusEncoder(sampleRate, channels, OpusApplication.OPUS_APPLICATION_VOIP);
            _encoder.Bitrate = 32000; // 32kbps is enough for voice
        }

        public void Start()
        {
            if (_isRecording) return;
            _waveIn.StartRecording();
            _isRecording = true;
        }

        public void Stop()
        {
            if (!_isRecording) return;
            _waveIn.StopRecording();
            _isRecording = false;
        }

        private void OnDataAvailable(object sender, WaveInEventArgs e)
        {
            if (e.BytesRecorded <= 0) return;

            // Convert byte[] (PCM 16-bit) to short[] for Opus encoder
            short[] pcmBuffer = new short[e.BytesRecorded / 2];
            Buffer.BlockCopy(e.Buffer, 0, pcmBuffer, 0, e.BytesRecorded);

            // Encode Opus
            // Frame size for 20ms at 48kHz is 960 samples
            int frameSize = 960; 
            byte[] encoded = new byte[1275]; // Max Opus frame size
            int encodedLength = _encoder.Encode(pcmBuffer, 0, frameSize, encoded, 0, encoded.Length);

            if (encodedLength > 0)
            {
                byte[] finalData = new byte[encodedLength];
                Array.Copy(encoded, finalData, encodedLength);
                OnEncodedData?.Invoke(finalData);
            }
        }

        public void Dispose()
        {
            Stop();
            _waveIn?.Dispose();
        }
    }
}
