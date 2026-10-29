using SIPSorcery.Net;
using RemoteDesk.Protocol;

namespace RemoteDesk.Transport;

public sealed class RtcPeerCoordinator : IAsyncDisposable
{
    private readonly RTCPeerConnection _peerConnection;
    private readonly DataChannelMessageFramer _messageFramer = new();
    private RTCDataChannel? _dataChannel;
    private readonly TaskCompletionSource<bool> _dataChannelReady = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public event Action<RtcIceCandidateMessage>? OnLocalIceCandidate;
    public event Action<byte[]>? OnBinaryDataReceived;
    public event Action<string>? OnConnectionStateChange;

    public bool IsDataChannelOpen => _dataChannel?.readyState == RTCDataChannelState.open;

    public RtcPeerCoordinator()
    {
        var config = new RTCConfiguration
        {
            iceServers = new List<RTCIceServer>
            {
                new() { urls = "stun:stun.l.google.com:19302" },
            },
        };

        _peerConnection = new RTCPeerConnection(config);

        _peerConnection.onicecandidate += candidate =>
        {
            if (candidate is not null && !string.IsNullOrWhiteSpace(candidate.candidate))
            {
                OnLocalIceCandidate?.Invoke(new RtcIceCandidateMessage(
                    candidate.candidate,
                    candidate.sdpMid,
                    candidate.sdpMLineIndex));
            }
        };

        _peerConnection.onconnectionstatechange += state =>
        {
            OnConnectionStateChange?.Invoke(state.ToString());
        };

        _peerConnection.ondatachannel += channel =>
        {
            AttachDataChannel(channel);
        };
    }

    public async Task InitializeDataChannelAsync(string label = "swiftdesk-stream")
    {
        var channel = await _peerConnection.createDataChannel(label, new RTCDataChannelInit
        {
            ordered = false,
            maxRetransmits = 0,
        });

        AttachDataChannel(channel);
    }

    private void AttachDataChannel(RTCDataChannel channel)
    {
        _dataChannel = channel;
        channel.onopen += () =>
        {
            _dataChannelReady.TrySetResult(true);
        };

        channel.onmessage += (_, _, data) =>
        {
            if (data is { Length: > 0 }
                && _messageFramer.TryDecode(data, out var message)
                && message is { Length: > 0 })
            {
                OnBinaryDataReceived?.Invoke(message);
            }
        };
    }

    public async Task<RtcSessionDescriptionMessage> CreateOfferAsync()
    {
        var offer = _peerConnection.createOffer();
        await _peerConnection.setLocalDescription(offer);
        return new RtcSessionDescriptionMessage("offer", offer.sdp);
    }

    public async Task<RtcSessionDescriptionMessage> HandleOfferAndCreateAnswerAsync(RtcSessionDescriptionMessage offer)
    {
        var init = new RTCSessionDescriptionInit
        {
            type = RTCSdpType.offer,
            sdp = offer.Sdp,
        };

        _peerConnection.setRemoteDescription(init);
        var answer = _peerConnection.createAnswer();
        await _peerConnection.setLocalDescription(answer);
        return new RtcSessionDescriptionMessage("answer", answer.sdp);
    }

    public void HandleAnswer(RtcSessionDescriptionMessage answer)
    {
        var init = new RTCSessionDescriptionInit
        {
            type = RTCSdpType.answer,
            sdp = answer.Sdp,
        };

        _peerConnection.setRemoteDescription(init);
    }

    public void AddIceCandidate(RtcIceCandidateMessage candidate)
    {
        _peerConnection.addIceCandidate(new RTCIceCandidateInit
        {
            candidate = candidate.Candidate,
            sdpMid = candidate.SdpMid,
            sdpMLineIndex = candidate.SdpMLineIndex ?? 0,
        });
    }

    public bool TrySendData(byte[] data)
    {
        if (_dataChannel?.readyState == RTCDataChannelState.open)
        {
            foreach (var packet in _messageFramer.Encode(data))
            {
                _dataChannel.send(packet);
            }

            return true;
        }

        return false;
    }

    public ValueTask DisposeAsync()
    {
        _dataChannel?.close();
        _peerConnection.close();
        _peerConnection.Dispose();
        return ValueTask.CompletedTask;
    }
}
