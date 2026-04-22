using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using Newtonsoft.Json;

namespace MidnightAgent.Helpers
{
    public class SipsorceryHelper : IDisposable
    {
        private RTCPeerConnection _peerConnection;
        private AudioHelper _audioHelper;
        private MediaStreamTrack _audioTrack;

        public event Action<string> OnSignalReady; // Sends SDP to Telegram

        public SipsorceryHelper()
        {
            _audioHelper = new AudioHelper();
            _audioHelper.OnEncodedData += SendAudioPacket;

            var config = new RTCConfiguration
            {
                iceServers = new List<RTCIceServer>
                {
                    new RTCIceServer { urls = "stun:stun.l.google.com:19302" }
                }
            };

            _peerConnection = new RTCPeerConnection(config);

            // Add audio track (Opus is preferred, PCMU/PCMA as fallbacks)
            var audioFormat = new AudioFormat(AudioCodecsEnum.OPUS, 111, 48000);
            _audioTrack = new MediaStreamTrack(new List<AudioFormat> { 
                audioFormat,
                new AudioFormat(AudioCodecsEnum.PCMU, 0),
                new AudioFormat(AudioCodecsEnum.PCMA, 8)
            });
            _peerConnection.addTrack(_audioTrack);

            _peerConnection.onconnectionstatechange += (state) =>
            {
                if (state == RTCPeerConnectionState.connected)
                {
                    _audioHelper.Start();
                }
                else if (state == RTCPeerConnectionState.closed || state == RTCPeerConnectionState.failed)
                {
                    _audioHelper.Stop();
                }
            };
        }

        public async Task<string> CreateOffer()
        {
            var offer = _peerConnection.createOffer();
            await _peerConnection.setLocalDescription(offer);
            
            // Wait for ICE gathering to complete (simple way for Telegram signaling)
            int retries = 0;
            while (_peerConnection.iceGatheringState != RTCIceGatheringState.complete && retries < 10)
            {
                await Task.Delay(500);
                retries++;
            }

            string rawSdp = _peerConnection.localDescription.sdp.ToString();
            // Force CRLF (\r\n) for browser compatibility
            return rawSdp.Replace("\r\n", "\n").Replace("\n", "\r\n");
        }

        public async Task SetAnswer(string sdp)
        {
            var result = _peerConnection.setRemoteDescription(new RTCSessionDescriptionInit
            {
                type = RTCSdpType.answer,
                sdp = sdp
            });
            
            if (result != SetDescriptionResultEnum.OK)
            {
                throw new Exception($"Failed to set remote answer: {result}");
            }
        }

        private void SendAudioPacket(byte[] data)
        {
            if (_peerConnection.connectionState == RTCPeerConnectionState.connected)
            {
                _peerConnection.SendAudio(20, data);
            }
        }

        public void Dispose()
        {
            _audioHelper?.Dispose();
            _peerConnection?.Close("Closed by user");
        }
    }
}
