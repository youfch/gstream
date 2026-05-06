// WebRtcStreamer.cs - H.264 NALU to WebRTC streaming using SIPSorcery

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;

namespace gStream.Core.Streaming;

/// <summary>
/// WebRTC streamer that takes H.264 NALU data and streams it to browser clients.
/// Uses SIPSorcery for WebRTC implementation with DataChannel support for input relay.
/// Two-step negotiation: step 1 answers data-only offer (SCTP), step 2 sends video renegotiation offer.
/// <para>
/// NOTE: All Debug.WriteLine calls have been removed from this file because every SIPSorcery
/// callback (ICE, SCTP, connection state) runs on background threads, and Godot's native
/// logging (which intercepts Debug.WriteLine) is not thread-safe for non-main threads.
/// This caused "Unexpected NUL character" errors in UTF-8 string marshaling.
/// All important events are logged by MultiStreamServer.cs via GD.Print on the main thread.
/// </para>
/// </summary>
public sealed class WebRtcStreamer : IDisposable
{
    private RTCPeerConnection? _peerConnection;
    private RTCDataChannel? _inputDataChannel;
    private readonly List<RTCIceServer> _iceServers;
    private readonly uint _clockRate = 90000; // Video RTP clock rate (same for H264/HEVC/AV1)
    private readonly string _codecName; // "H264", "H265", "AV1", or "VP9" — must match encoder output
    private readonly string _fmtp;      // SDP fmtp line for the selected codec variant
    private readonly bool _declareBothCodecs; // Default mode: declare H264+H265+AV1+VP9 in SDP
    private int _fps = 30;
    private bool _disposed;
    private bool _isConnected;
    private byte[]? _pendingKeyframe;
    private int _pendingKeyframeLength;

    private uint _vp9Timestamp;            // VP9 RTP timestamp (managed manually for SendRtpRaw)
    private int _vp9PayloadType = 96;      // Updated from OnVideoFormatsNegotiated
    private string _negotiatedCodecName = "H264"; // Updated after SDP negotiation
    private bool _videoTrackAdded;
    private bool _audioTrackAdded;
    private uint _audioTimestamp; // RTP timestamp for audio (48000 Hz clock)
    private const uint AudioClockRate = 48000;
    private const int AudioFrameDurationMs = 20; // 20ms Opus frames
    private IPAddress? _bindAddress;
    private string[]? _allowedIcePrefixes;

    /// <summary>
    /// DataChannel label to watch for incoming input messages.
    /// Defaults to "input". Set to "data" for videoplayer mode.
    /// </summary>
    public string DataChannelLabel { get; set; } = "input";

    /// <summary>
    /// Fired when a message is received on the "input" DataChannel from browser clients.
    /// </summary>
    public event Action<byte[]>? OnInputEvent;

    /// <summary>
    /// Fired when the connection becomes ready and a keyframe is needed from the encoder.
    /// </summary>
    public event Action? OnKeyframeRequested;

    /// <summary>
    /// Fired after SDP negotiation completes with the negotiated video codec name
    /// ("H264", "H265", "AV1", or "VP9"). Used in Default/Auto mode to lazily initialize the encoder.
    /// </summary>
    public event Action<string>? OnVideoFormatNegotiated;

    /// <summary>
    /// Fired when the peer connection state changes.
    /// </summary>
    public event Action<RTCPeerConnectionState>? OnStateChanged;

    /// <summary>
    /// Fired when the ICE connection state changes.
    /// </summary>
    public event Action<RTCIceConnectionState>? OnIceConnectionStateChanged;

    /// <summary>
    /// Fired when a new ICE candidate is available (for trickle ICE).
    /// </summary>
    public event Action<RTCIceCandidate>? OnIceCandidate;

    /// <summary>
    /// Fired when a complete video frame is received from the remote peer (browser).
    /// The byte[] contains the encoded video frame (H.264 Annex-B, VP8, etc.).
    /// Only fires when <see cref="ReceiveRemoteVideo"/> is true.
    /// </summary>
    public event Action<byte[], VideoFormat>? OnRemoteVideoFrame;

    /// <summary>
    /// When true, the video track direction is set to SendRecv instead of SendOnly,
    /// enabling receipt of video from the browser (e.g., camera feed in bidirectional mode).
    /// Must be set before calling AddVideoTrack/CreateOffer/CreateAnswer.
    /// </summary>
    public bool ReceiveRemoteVideo { get; set; } = false;

    /// <summary>
    /// Gets the negotiated remote video codec name after SDP negotiation completes.
    /// Returns null if no remote video has been negotiated yet.
    /// </summary>
    public string? RemoteVideoCodecName { get; private set; }

    /// <summary>
    /// Gets the current peer connection state.
    /// </summary>
    public RTCPeerConnectionState ConnectionState => _peerConnection?.connectionState ?? RTCPeerConnectionState.@new;

    /// <summary>
    /// Gets the current ICE connection state.
    /// </summary>
    public RTCIceConnectionState IceConnectionState => _peerConnection?.iceConnectionState ?? RTCIceConnectionState.@new;

    /// <summary>
    /// Gets whether the connection is established and ready to send video.
    /// </summary>
    public bool IsConnected => _isConnected && _peerConnection?.connectionState == RTCPeerConnectionState.connected;

    /// <summary>
    /// Creates a new WebRtcStreamer instance.
    /// </summary>
    public WebRtcStreamer(IEnumerable<RTCIceServer>? iceServers = null, int fps = 30, string codecName = "H264",
        string? fmtp = null, bool declareBothCodecs = false,
        IPAddress? bindAddress = null, string[]? allowedIcePrefixes = null)
    {
        _fps = fps;
        _codecName = codecName;
        _fmtp = fmtp ?? codecName switch
        {
            "H265" => "level-id=123;profile-id=1;tier-flag=0;tx-mode=SRST",
            "AV1" => "level-idx=5;profile=0;tier=0",
            "VP9" => "profile-id=0",
            _ => "level-asymmetry-allowed=1;packetization-mode=1;profile-level-id=64001f"
        };
        _declareBothCodecs = declareBothCodecs;
        _bindAddress = bindAddress;
        _allowedIcePrefixes = allowedIcePrefixes;
        _iceServers = iceServers?.ToList() ?? new List<RTCIceServer>
        {
            new RTCIceServer { urls = "stun:stun.l.google.com:19302" }
        };
    }

    /// <summary>
    /// Creates the peer connection (with DataChannel) if not already created.
    /// Does NOT add the video track — call AddVideoTrack separately.
    /// </summary>
    private void EnsurePeerConnection()
    {
        if (_peerConnection != null) return;

        var config = new RTCConfiguration
        {
            iceServers = _iceServers,
            X_BindAddress = _bindAddress
        };
        _peerConnection = new RTCPeerConnection(config);

        // Create a DataChannel so SIPSorcery includes m=application in SDP.
        // Label "_sctp" avoids conflicting with the browser's "input" DataChannel.
        CreateInputDataChannel();
        HookPeerConnectionEvents();
    }

    /// <summary>
    /// Adds the video track to the peer connection. Can be called after the initial
    /// SCTP-only offer/answer to add video via renegotiation.
    /// </summary>
    public void AddVideoTrack()
    {
        EnsurePeerConnection();
        if (_videoTrackAdded) return;

        List<SDPAudioVideoMediaFormat> videoFormats;
        if (_declareBothCodecs)
        {
            var h264Format = new SDPAudioVideoMediaFormat(
                SDPMediaTypesEnum.video, 96, "H264", 90000, 0,
                "level-asymmetry-allowed=1;packetization-mode=1;profile-level-id=64001f");
            var h265Format = new SDPAudioVideoMediaFormat(
                SDPMediaTypesEnum.video, 97, "H265", 90000, 0,
                "level-id=123;profile-id=1;tier-flag=0;tx-mode=SRST");
            var av1Format = new SDPAudioVideoMediaFormat(
                SDPMediaTypesEnum.video, 98, "AV1", 90000, 0, "level-idx=5;profile=0;tier=0");
            var vp9Format = new SDPAudioVideoMediaFormat(
                SDPMediaTypesEnum.video, 99, "VP9", 90000, 0,
                "profile-id=0");
            videoFormats = new List<SDPAudioVideoMediaFormat> { h264Format, h265Format, av1Format, vp9Format };
        }
        else
        {
            videoFormats = new List<SDPAudioVideoMediaFormat> {
                new SDPAudioVideoMediaFormat(SDPMediaTypesEnum.video, 96, _codecName, 90000, 0, _fmtp)
            };
        }

        var trackStatus = ReceiveRemoteVideo ? MediaStreamStatusEnum.SendRecv : MediaStreamStatusEnum.SendOnly;
        var videoTrack = new MediaStreamTrack(
            SDPMediaTypesEnum.video, false, videoFormats, trackStatus);
        _peerConnection!.addTrack(videoTrack);
        _videoTrackAdded = true;
    }

    /// <summary>
    /// Adds the audio track to the peer connection. Should be called AFTER
    /// AddVideoTrack() but BEFORE CreateOffer/CreateAnswer.
    /// </summary>
    public void AddAudioTrack()
    {
        EnsurePeerConnection();
        if (_audioTrackAdded) return;

        var opusFormat = new SDPAudioVideoMediaFormat(
            SDPMediaTypesEnum.audio, 111, "opus", 48000, 2, "");

        var audioTrack = new MediaStreamTrack(
            SDPMediaTypesEnum.audio, false,
            new List<SDPAudioVideoMediaFormat> { opusFormat },
            MediaStreamStatusEnum.SendOnly);
        _peerConnection!.addTrack(audioTrack);
        _audioTrackAdded = true;
    }

    /// <summary>
    /// Creates an SDP offer. Adds the video track if not yet added.
    /// For renegotiation offers (after step 1 established SCTP), reorders m= sections
    /// so m=application stays at position 0 (matching step 1) and m=video at position 1.
    /// Per RFC 8829 §5.2.2, Chrome requires existing m= sections to remain at their
    /// original position in subsequent offers.
    /// </summary>
    public string CreateOffer()
    {
        EnsurePeerConnection();
        if (!_videoTrackAdded)
            AddVideoTrack();
        if (!_audioTrackAdded)
            AddAudioTrack();

        var offer = _peerConnection!.createOffer(new RTCOfferOptions());
        if (offer == null || offer.sdp == null)
            throw new InvalidOperationException("Failed to create SDP offer.");

        var sdp = offer.sdp.ToString();

        // Inject RTCP feedback lines for NACK, PLI, FIR, and transport-cc.
        // SIPSorcery's SDPAudioVideoMediaFormat does not support rtcp-fb attributes,
        // so we inject them via SDP munging after offer creation.
        sdp = InjectRtcpFeedback(sdp);

        // If renegotiating (PC already has step 1 SCTP established),
        // reorder sections and swap mids so m=application keeps position 0, mid:0.
        if (_peerConnection.remoteDescription != null)
        {
            sdp = ReorderRenegotiationOffer(sdp);
        }

        return sdp;
    }

    /// <summary>
    /// Reorders renegotiation offer so m=application stays at position 0 (matching step 1),
    /// m=audio at position 1, and m=video at position 2. Per RFC 8829 §5.2.2, Chrome
    /// rejects offers where existing m= sections move to different positions.
    /// Also swaps mid values and fixes DTLS setup role.
    /// </summary>
    private static string ReorderRenegotiationOffer(string sdp)
    {
        // Find all m= line positions
        var mPositions = new List<int>();
        int searchFrom = 0;
        while (searchFrom < sdp.Length)
        {
            int pos = sdp.IndexOf("m=", searchFrom);
            if (pos < 0) break;
            mPositions.Add(pos);
            searchFrom = pos + 2;
        }

        if (mPositions.Count < 2) return sdp;

        // Extract header and m= sections
        string header = sdp.Substring(0, mPositions[0]);
        var sections = new List<string>();
        for (int i = 0; i < mPositions.Count; i++)
        {
            int start = mPositions[i];
            int end = (i + 1 < mPositions.Count) ? mPositions[i + 1] : sdp.Length;
            sections.Add(sdp.Substring(start, end - start));
        }

        // Categorize sections by media type
        string? appSection = null, audioSection = null, videoSection = null;
        var otherSections = new List<string>();
        foreach (var sec in sections)
        {
            if (sec.StartsWith("m=application ") && appSection == null)
                appSection = sec;
            else if (sec.StartsWith("m=audio ") && audioSection == null)
                audioSection = sec;
            else if (sec.StartsWith("m=video ") && videoSection == null)
                videoSection = sec;
            else
                otherSections.Add(sec);
        }

        // If no application section or already at position 0, no reorder needed
        if (appSection == null || sections[0].StartsWith("m=application "))
            return sdp;

        // Build ordered result: application(pos 0), audio(pos 1), video(pos 2), others
        var ordered = new List<string>();
        if (appSection != null) ordered.Add(appSection);
        if (audioSection != null) ordered.Add(audioSection);
        if (videoSection != null) ordered.Add(videoSection);
        ordered.AddRange(otherSections);

        // Assign sequential mid values and fix DTLS setup role
        var result = header;
        for (int i = 0; i < ordered.Count; i++)
        {
            string sec = ordered[i];

            // Replace existing mid value with the correct sequential one
            int midIdx = sec.IndexOf("a=mid:");
            if (midIdx >= 0)
            {
                int crIdx = sec.IndexOf("\r\n", midIdx);
                if (crIdx >= 0)
                    sec = sec.Substring(0, midIdx) + $"a=mid:{i}" + sec.Substring(crIdx);
            }

            // Fix DTLS setup role: offerer must use actpass, not active
            sec = sec.Replace("a=setup:active\r\n", "a=setup:actpass\r\n");

            result += sec;
        }

        return result;
    }

    /// <summary>
    /// Injects RTCP feedback (NACK, PLI, FIR, transport-cc) into m=video sections.
    /// SIPSorcery's SDPAudioVideoMediaFormat doesn't support rtcp-fb attributes natively.
    /// These enable: NACK (packet loss recovery), PLI (request keyframe), transport-cc (adaptive bitrate).
    /// </summary>
    private static string InjectRtcpFeedback(string sdp)
    {
        if (string.IsNullOrEmpty(sdp)) return sdp;

        var lines = sdp.Split(new[] { "\r\n" }, StringSplitOptions.None);
        var result = new List<string>(lines.Length + 40);
        bool inVideoSection = false;

        // Collect existing rtcp-fb lines per payload type to avoid duplicates
        var existingFb = new HashSet<string>();
        foreach (var line in lines)
        {
            if (line.StartsWith("m=video "))
                inVideoSection = true;
            else if (line.StartsWith("m="))
                inVideoSection = false;

            if (inVideoSection && line.StartsWith("a=rtcp-fb:"))
                existingFb.Add(line);
        }

        inVideoSection = false;
        string[] desiredFb = { "nack", "nack pli", "ccm fir", "transport-cc" };

        foreach (var line in lines)
        {
            result.Add(line);

            if (line.StartsWith("m=video "))
                inVideoSection = true;
            else if (line.StartsWith("m="))
                inVideoSection = false;

            if (!inVideoSection) continue;

            // After rtpmap for video payload types, inject missing rtcp-fb lines
            if (line.StartsWith("a=rtpmap:"))
            {
                var colonIdx = line.IndexOf(':');
                var spaceIdx = line.IndexOf(' ', colonIdx + 1);
                if (colonIdx >= 0 && spaceIdx > colonIdx)
                {
                    var pt = line.Substring(colonIdx + 1, spaceIdx - colonIdx - 1);
                    foreach (var fb in desiredFb)
                    {
                        var fbLine = $"a=rtcp-fb:{pt} {fb}";
                        if (!existingFb.Contains(fbLine))
                            result.Add(fbLine);
                    }
                }
            }
        }

        return string.Join("\r\n", result);
    }

    /// <summary>
    /// Sets the remote SDP offer from the browser. Creates peer connection if needed.
    /// After this, call AddVideoTrack() then CreateAnswer() to send back an answer with video.
    /// </summary>
    public bool SetRemoteOffer(string sdp)
    {
        EnsurePeerConnection();

        if (string.IsNullOrWhiteSpace(sdp))
            throw new ArgumentException("SDP offer cannot be null or empty.", nameof(sdp));

        var offerInit = new RTCSessionDescriptionInit { type = RTCSdpType.offer, sdp = sdp };
        var result = _peerConnection!.setRemoteDescription(offerInit);

        return result == SetDescriptionResultEnum.OK;
    }

    /// <summary>
    /// Creates an SDP answer after receiving a remote offer.
    /// Must be called after SetRemoteOffer. For step 1 (data-only), do NOT call
    /// AddVideoTrack before this — the answer must match the offer's m= section count.
    /// </summary>
    public string CreateAnswer()
    {
        if (_peerConnection == null)
            throw new InvalidOperationException("Peer connection not initialized. Call SetRemoteOffer first.");

        var answer = _peerConnection.createAnswer(null);
        if (answer == null || answer.sdp == null)
            throw new InvalidOperationException("Failed to create SDP answer.");

        // Inject RTCP feedback BEFORE setLocalDescription so local & remote SDP match.
        var sdp = answer.sdp.ToString();
        sdp = InjectRtcpFeedback(sdp);
        answer.sdp = sdp;

        _peerConnection.setLocalDescription(answer);

        return sdp;
    }

    /// <summary>
    /// Sets the remote SDP answer received from the browser client via signaling.
    /// </summary>
    public bool SetRemoteAnswer(string sdp)
    {
        if (_peerConnection == null)
            throw new InvalidOperationException("Peer connection not initialized.");

        if (string.IsNullOrWhiteSpace(sdp))
            throw new ArgumentException("SDP answer cannot be null or empty.", nameof(sdp));

        var answerInit = new RTCSessionDescriptionInit { type = RTCSdpType.answer, sdp = sdp };
        var result = _peerConnection.setRemoteDescription(answerInit);

        return result == SetDescriptionResultEnum.OK;
    }

    /// <summary>
    /// Adds an ICE candidate for trickle ICE.
    /// </summary>
    public void AddIceCandidate(string candidate, int? sdpMLineIndex = null, string? sdpMid = null)
    {
        if (_peerConnection == null)
            throw new InvalidOperationException("Peer connection not initialized.");

        if (string.IsNullOrWhiteSpace(candidate))
            return;

        var candidateInit = new RTCIceCandidateInit
        {
            candidate = candidate,
            sdpMLineIndex = (ushort)(sdpMLineIndex ?? 0),
            sdpMid = sdpMid
        };

        _peerConnection.addIceCandidate(candidateInit);
    }

    /// <summary>
    /// Forces re-sending of the cached keyframe to all connected clients.
    /// Useful when the browser explicitly requests a keyframe (via FIR/PLI) or when
    /// renegotiation completes and the decoder needs to re-initialize.
    /// </summary>
    public void ForceKeyframeSend()
    {
        if (_pendingKeyframe == null || _pendingKeyframeLength == 0)
            return;

        if (IsConnected)
        {
            SendNaluInternal(_pendingKeyframe, _pendingKeyframeLength, true);
        }
    }

    /// <summary>
    /// Returns a copy of the currently cached keyframe data, or null if none is available.
    /// Thread-safe: the returned byte[] is an independent copy.
    /// </summary>
    public byte[]? TryGetPendingKeyframe(out int length)
    {
        length = _pendingKeyframeLength;
        if (_pendingKeyframe == null || _pendingKeyframeLength == 0)
            return null;
        var copy = new byte[_pendingKeyframeLength];
        Buffer.BlockCopy(_pendingKeyframe, 0, copy, 0, _pendingKeyframeLength);
        return copy;
    }

    /// <summary>
    /// Seeds this streamer's cached keyframe from external data.
    /// Used when a new client joins an existing stream — the new client's streamer
    /// needs a keyframe so the browser decoder can initialize immediately, without
    /// waiting for the encoder to produce the next forced keyframe.
    /// </summary>
    public void CopyPendingKeyframe(byte[] data, int length)
    {
        if (data == null || length == 0) return;
        if (_pendingKeyframe == null || _pendingKeyframe.Length < length)
            _pendingKeyframe = new byte[Math.Max(length, 65536)];
        Buffer.BlockCopy(data, 0, _pendingKeyframe, 0, length);
        _pendingKeyframeLength = length;
    }

    /// <summary>
    /// Sends an encoded Opus audio frame to connected browser clients.
    /// The opusData buffer is from ArrayPool and is only valid during this call.
    /// </summary>
    public void SendAudio(byte[] opusData, int length)
    {
        if (!IsConnected || !_audioTrackAdded) return;

        uint durationRtpUnits = AudioClockRate * (uint)AudioFrameDurationMs / 1000;
        var buffer = (length == opusData.Length) ? opusData : opusData.AsSpan(0, length).ToArray();
        _peerConnection?.SendAudio(durationRtpUnits, buffer);

        _audioTimestamp += durationRtpUnits;
    }

    /// <summary>
    /// Sends an H.264 NALU to connected browser clients.
    /// The naluData buffer is from ArrayPool and is only valid during this call.
    /// Keyframes are copied to a persistent buffer and re-sent once per connection
    /// cycle to ensure the browser decoder always has a valid reference.
    /// </summary>
    public void SendH264Nalu(byte[] naluData, int length, bool isKeyframe)
    {
        if (naluData == null || length == 0) return;

        if (isKeyframe)
        {
            if (_pendingKeyframe == null || _pendingKeyframe.Length < length)
            {
                _pendingKeyframe = new byte[Math.Max(length, 65536)];
            }
            Buffer.BlockCopy(naluData, 0, _pendingKeyframe, 0, length);
            _pendingKeyframeLength = length;
        }

        if (!IsConnected) return;

        SendNaluInternal(naluData, length, isKeyframe);
    }

    /// <summary>
    /// Sends an AV1 OBU (Open Bitstream Unit) to connected browser clients.
    /// The obuData buffer is from ArrayPool and is only valid during this call.
    /// Keyframes are copied to a persistent buffer and re-sent once per connection cycle.
    /// </summary>
    public void SendAv1Obu(byte[] obuData, int length, bool isKeyframe)
    {
        if (obuData == null || length == 0) return;

        if (isKeyframe)
        {
            if (_pendingKeyframe == null || _pendingKeyframe.Length < length)
            {
                _pendingKeyframe = new byte[Math.Max(length, 65536)];
            }
            Buffer.BlockCopy(obuData, 0, _pendingKeyframe, 0, length);
            _pendingKeyframeLength = length;
        }

        if (!IsConnected) return;

        SendObuInternal(obuData, length, isKeyframe);
    }

    private void SendNaluInternal(byte[] naluData, int length, bool isKeyframe)
    {
        uint durationRtpUnits = _clockRate / (uint)_fps;
        var buffer = (length == naluData.Length) ? naluData : naluData.AsSpan(0, length).ToArray();
        _peerConnection?.SendVideo(durationRtpUnits, buffer);
    }

    private void SendObuInternal(byte[] obuData, int length, bool isKeyframe)
    {
        uint durationRtpUnits = _clockRate / (uint)_fps;
        var buffer = (length == obuData.Length) ? obuData : obuData.AsSpan(0, length).ToArray();
        _peerConnection?.SendVideo(durationRtpUnits, buffer);
    }

    public void SendVp9Frame(byte[] frameData, int length, bool isKeyframe)
    {
        if (frameData == null || length == 0) return;

        if (isKeyframe)
        {
            if (_pendingKeyframe == null || _pendingKeyframe.Length < length)
            {
                _pendingKeyframe = new byte[Math.Max(length, 65536)];
            }
            Buffer.BlockCopy(frameData, 0, _pendingKeyframe, 0, length);
            _pendingKeyframeLength = length;
        }

        if (!IsConnected)
            return;

        SendVp9Internal(frameData, length, isKeyframe);
    }

    private void SendVp9Internal(byte[] frameData, int length, bool isKeyframe)
    {
        uint durationRtpUnits = _clockRate / (uint)_fps;

        // SIPSorcery 10.0.5 does NOT support VP9 in VideoStream.SendVideo() — it throws
        // ApplicationException for unknown codecs. We bypass SendVideo entirely and use
        // SendRtpRaw directly with the VP9 RTP payload descriptor (draft-ietf-payload-vp9).
        const int maxPayload = 1200; // RTP_MAX_PAYLOAD typical value
        byte[] buffer = (length == frameData.Length) ? frameData : frameData.AsSpan(0, length).ToArray();

        try
        {
            for (int offset = 0; offset < buffer.Length; offset += maxPayload)
            {
                int chunkLen = Math.Min(maxPayload, buffer.Length - offset);
                bool isFirst = (offset == 0);
                bool isLast = (offset + chunkLen >= buffer.Length);

                byte descriptor;
                if (isFirst && isLast)
                    descriptor = 0x18; // B=1, E=1
                else if (isFirst)
                    descriptor = 0x08; // B=1
                else if (isLast)
                    descriptor = 0x10; // E=1
                else
                    descriptor = 0x00;

                byte[] payload = new byte[1 + chunkLen];
                payload[0] = descriptor;
                Buffer.BlockCopy(buffer, offset, payload, 1, chunkLen);

                int markerBit = isLast ? 1 : 0;
                _peerConnection?.SendRtpRaw(SIPSorcery.Net.SDPMediaTypesEnum.video,
                    payload, _vp9Timestamp, markerBit, _vp9PayloadType);
            }

            _vp9Timestamp += durationRtpUnits;
        }
        catch
        {
            // Silently ignore — runs on encode background thread.
            // MultiStreamServer logs errors on the main thread via stats.
        }
    }

    public void SetFrameRate(int fps)
    {
        if (fps <= 0) throw new ArgumentException("FPS must be positive", nameof(fps));
        _fps = fps;
    }

    private void HookPeerConnectionEvents()
    {
        _peerConnection!.onicecandidate += (candidate) =>
        {
            // Filter candidates by allowed IP prefixes if configured.
            if (_allowedIcePrefixes != null && _allowedIcePrefixes.Length > 0)
            {
                bool allowed = false;
                foreach (var prefix in _allowedIcePrefixes)
                {
                    if (candidate.address?.StartsWith(prefix) == true)
                    {
                        allowed = true;
                        break;
                    }
                }
                if (!allowed)
                    return;
            }

            OnIceCandidate?.Invoke(candidate);
        };

        _peerConnection.oniceconnectionstatechange += (state) =>
        {
            OnIceConnectionStateChanged?.Invoke(state);
        };

        _peerConnection.onconnectionstatechange += (state) =>
        {
            _isConnected = state == RTCPeerConnectionState.connected;
            OnStateChanged?.Invoke(state);

            if (state == RTCPeerConnectionState.connected)
            {
                OnKeyframeRequested?.Invoke();
            }

            if (state == RTCPeerConnectionState.failed || state == RTCPeerConnectionState.disconnected)
                _isConnected = false;
        };

        _peerConnection.OnVideoFormatsNegotiated += (formats) =>
        {
            var negotiated = formats.FirstOrDefault();
            if (!negotiated.IsEmpty())
            {
                var name = negotiated.FormatName ?? "H264";
                _negotiatedCodecName = name;
                _vp9PayloadType = negotiated.FormatID;
                OnVideoFormatNegotiated?.Invoke(name);
            }
        };

        // Hook remote video frame reception for bidirectional mode.
        if (ReceiveRemoteVideo)
        {
            _peerConnection.OnVideoFrameReceived += (endPoint, timestamp, frame, videoFormat) =>
            {
                if (frame != null && frame.Length > 0)
                {
                    RemoteVideoCodecName = videoFormat.Codec.ToString();
                    OnRemoteVideoFrame?.Invoke(frame, videoFormat);
                }
            };
        }

        _peerConnection.ondatachannel += (dc) =>
        {
            if (dc.label == DataChannelLabel)
                SetupDataChannelHandlers(dc);
        };
    }

    private void CreateInputDataChannel()
    {
        var dcInit = new RTCDataChannelInit { ordered = false, maxRetransmits = 0 };
        try
        {
            _inputDataChannel = _peerConnection!.createDataChannel("_sctp", dcInit).GetAwaiter().GetResult();
        }
        catch
        {
            // DataChannel creation failure — non-fatal, SCTP will still work via remote channel
        }
    }

    private void SetupDataChannelHandlers(RTCDataChannel dc)
    {
        // NOTE: All SIPSorcery DataChannel callbacks run on background SCTP threads.
        // Do NOT call Debug.WriteLine/GD.Print here — Godot's string marshaling
        // is not thread-safe from non-main threads and causes "Unexpected NUL character" errors.
        dc.onmessage += (channel, protocol, data) =>
        {
            if (data != null && data.Length > 0)
            {
                OnInputEvent?.Invoke(data);
            }
        };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _isConnected = false;

        try
        {
            _inputDataChannel?.close();
            _inputDataChannel = null;
            _peerConnection?.Close("normal");
            _peerConnection = null;
            RemoteVideoCodecName = null;
        }
        catch
        {
            // Silent — Dispose may be called from any thread
        }
    }
}
