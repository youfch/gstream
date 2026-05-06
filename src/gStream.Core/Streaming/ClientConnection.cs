using System;
using System.Collections.Concurrent;
using gStream.Core.Input;

namespace gStream.Core.Streaming;

/// <summary>
/// Represents a single connected browser client in multiplay mode.
/// Each client has its own WebRTC peer connection, input relay, and multiplay message queue.
/// </summary>
public sealed class ClientConnection : IDisposable
{
    /// <summary>
    /// Browser's unique connection ID assigned by the signaling server.
    /// Used to route signaling messages (SDP offers/answers/candidates) to this client.
    /// </summary>
    public string ConnectionId { get; }

    /// <summary>
    /// Per-client WebRTC peer connection. Each browser client has its own
    /// <see cref="WebRtcStreamer"/> for independent SDP negotiation and RTP transport.
    /// </summary>
    public WebRtcStreamer Streamer { get; }

    /// <summary>
    /// Optional dedicated signaling client for this connection.
    /// When null, the shared <c>MultiStreamServer</c> signaling client is used.
    /// </summary>
    public SignalingClient? Signaling { get; set; }

    /// <summary>
    /// Per-client input relay that parses URS binary DataChannel messages.
    /// Each client has independent input state (mouse position, keyboard state, etc.).
    /// </summary>
    public InputRelay InputRelay { get; }

    /// <summary>
    /// Client display label. Set via multiplay <c>ChangeLabel</c> message
    /// (<c>{ type: 0, argument: "randomNumber" }</c>).
    /// Defaults to the connection ID until a label change message is received.
    /// </summary>
    public string Label { get; set; }

    /// <summary>
    /// Whether the underlying WebRTC peer connection is established and ready.
    /// </summary>
    public bool IsConnected => Streamer.IsConnected;

    /// <summary>
    /// Timestamp (UTC) when this client connection was first created.
    /// </summary>
    public DateTime ConnectedAt { get; }

    /// <summary>
    /// Queue of incoming multiplay DataChannel messages (JSON strings).
    /// Messages are drained and broadcast to other clients in <c>MultiStreamServer._Process</c>.
    /// <para>
    /// Note: Requires <see cref="WebRtcStreamer"/> to be extended with a handler for the
    /// "multiplay" DataChannel label. When that extension is added, incoming messages
    /// should be enqueued here.
    /// </para>
    /// </summary>
    public ConcurrentQueue<string> MultiplayMessages { get; } = new();

    // ── Two-step negotiation state (per client) ──

    /// <summary>True after data-only SDP answer sent (SCTP/DataChannel established).</summary>
    public volatile bool Step1Done;

    /// <summary>True after video renegotiation offer sent.</summary>
    public volatile bool Step2Done;

    /// <summary>
    /// Creates a new client connection.
    /// </summary>
    /// <param name="connectionId">Browser's connection ID from the signaling server.</param>
    /// <param name="streamer">Per-client WebRTC peer connection.</param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="connectionId"/> or <paramref name="streamer"/> is null.
    /// </exception>
    public ClientConnection(string connectionId, WebRtcStreamer streamer)
    {
        ConnectionId = connectionId ?? throw new ArgumentNullException(nameof(connectionId));
        Streamer = streamer ?? throw new ArgumentNullException(nameof(streamer));
        InputRelay = new InputRelay();
        Label = connectionId;
        ConnectedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Releases the WebRTC peer connection and input relay resources.
    /// </summary>
    public void Dispose()
    {
        Streamer?.Dispose();
        InputRelay?.Dispose();
    }
}
