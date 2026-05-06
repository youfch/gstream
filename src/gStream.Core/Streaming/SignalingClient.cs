using System.Buffers;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace gStream.Core.Streaming;

/// <summary>
/// Event args for SDP offer received from a remote peer.
/// </summary>
public sealed class OfferReceivedEventArgs : EventArgs
{
    public required string FromConnectionId { get; init; }
    public required string Sdp { get; init; }
    public long DateTime { get; init; }
    public bool Polite { get; init; }
}

/// <summary>
/// Event args for SDP answer received from a remote peer.
/// </summary>
public sealed class AnswerReceivedEventArgs : EventArgs
{
    public required string FromConnectionId { get; init; }
    public required string Sdp { get; init; }
    public long DateTime { get; init; }
}

/// <summary>
/// Event args for ICE candidate received from a remote peer.
/// </summary>
public sealed class CandidateReceivedEventArgs : EventArgs
{
    public required string FromConnectionId { get; init; }
    public required string Candidate { get; init; }
    public required int SdpMLineIndex { get; init; }
    public required string SdpMid { get; init; }
    public long DateTime { get; init; }
}

/// <summary>
/// Event args for connection/disconnection events.
/// </summary>
public sealed class ConnectionEventArgs : EventArgs
{
    public required string ConnectionId { get; init; }
}

/// <summary>
/// Event args for error messages from the signaling server.
/// </summary>
public sealed class SignalingErrorEventArgs : EventArgs
{
    public required string Message { get; init; }
}

/// <summary>
/// WebSocket signaling client compatible with Unity Render Streaming WebApp protocol.
/// Connects to URS WebApp WebSocket endpoint and exchanges SDP offers/answers/candidates.
/// </summary>
public sealed class SignalingClient : IAsyncDisposable
{
    private readonly Uri _serverUri;
    private readonly string _connectionId;
    private ClientWebSocket? _webSocket;
    private readonly Channel<SignalingMessage> _sendChannel = Channel.CreateUnbounded<SignalingMessage>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _disposeCts = new();
    private Task? _receiveLoopTask;
    private Task? _sendLoopTask;
    private bool _isReconnecting;
    private int _reconnectDelayMs = 1000;
    private const int MaxReconnectDelayMs = 30000;
    private const int BaseReconnectDelayMs = 1000;

    /// <summary>
    /// Fired when an SDP offer is received from a remote peer.
    /// </summary>
    public event EventHandler<OfferReceivedEventArgs>? OnOfferReceived;

    /// <summary>
    /// Fired when an SDP answer is received from a remote peer.
    /// </summary>
    public event EventHandler<AnswerReceivedEventArgs>? OnAnswerReceived;

    /// <summary>
    /// Fired when an ICE candidate is received from a remote peer.
    /// </summary>
    public event EventHandler<CandidateReceivedEventArgs>? OnCandidateReceived;

    /// <summary>
    /// Fired when successfully connected to the signaling server.
    /// </summary>
    public event EventHandler<ConnectionEventArgs>? OnConnected;

    /// <summary>
    /// Fired when disconnected from the signaling server or a remote peer disconnects.
    /// </summary>
    public event EventHandler<ConnectionEventArgs>? OnDisconnected;

    /// <summary>
    /// Fired when the signaling server sends an error message.
    /// </summary>
    public event EventHandler<SignalingErrorEventArgs>? OnError;

    /// <summary>
    /// Fired when connection state changes.
    /// </summary>
    public event EventHandler<bool>? OnConnectionStateChanged;

    /// <summary>
    /// Fired when this client should create and send an SDP offer.
    /// In URS Perfect Negotiation pattern, the impolite peer (polite=false) must initiate.
    /// </summary>
    public event EventHandler? OnShouldCreateOffer;

    /// <summary>
    /// Gets the unique connection ID for this client.
    /// </summary>
    public string ConnectionId => _connectionId;

    /// <summary>
    /// Gets whether the client is currently connected.
    /// </summary>
    public bool IsConnected => _webSocket?.State == WebSocketState.Open;

    /// <summary>
    /// Gets whether the client should operate in polite mode (per the URS perfect negotiation pattern).
    /// </summary>
    public bool IsPolite { get; private set; }

    /// <summary>
    /// Gets or sets whether auto-reconnect is enabled.
    /// </summary>
    public bool AutoReconnect { get; set; } = true;

    /// <summary>
    /// Creates a new signaling client with the specified server URL.
    /// </summary>
    /// <param name="serverUrl">The WebSocket server URL (e.g., ws://localhost:8080)</param>
    /// <param name="connectionId">Optional connection ID. If null, a new GUID is generated.</param>
    public SignalingClient(string serverUrl, string? connectionId = null)
    {
        _serverUri = new Uri(serverUrl);
        _connectionId = connectionId ?? Guid.NewGuid().ToString();
    }

    /// <summary>
    /// Connects to the signaling server.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposeCts.Token);
        var ct = linkedCts.Token;

        try
        {
            _webSocket = new ClientWebSocket();
            await _webSocket.ConnectAsync(_serverUri, ct);

            // Send connect message
            var connectMsg = new ConnectMessage(_connectionId);
            await SendImmediateAsync(connectMsg, ct);

            // Start receive loop
            _receiveLoopTask = ReceiveLoopAsync(ct);
            _sendLoopTask = SendLoopAsync(ct);

            OnConnectionStateChanged?.Invoke(this, true);
        }
        catch (Exception)
        {
            _webSocket?.Dispose();
            _webSocket = null;
            throw;
        }
    }

    /// <summary>
    /// Disconnects from the signaling server.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        if (_webSocket == null) return;

        try
        {
            // Send disconnect message
            var disconnectMsg = new DisconnectMessage(_connectionId);
            await SendImmediateAsync(disconnectMsg, cancellationToken);
        }
        catch
        {
            // Ignore send errors during disconnect
        }

        await CloseWebSocketAsync();
    }

    /// <summary>
    /// Sends an SDP offer to the signaling server for relay to remote peers.
    /// </summary>
    /// <param name="sdp">The SDP offer string.</param>
    /// <param name="asConnectionId">
    /// When provided, the offer is sent using this connectionId as the sender identity
    /// instead of our own. This is necessary when the remote peer (browser) expects
    /// all messages to carry its own connectionId (peer.js checks this.connectionId == connectionId).
    /// When null, our own connectionId is used (standard impolite-peer behaviour).
    /// </param>
    public void SendOffer(string sdp, string? asConnectionId = null)
    {
        var connectionId = !string.IsNullOrEmpty(asConnectionId) ? asConnectionId : _connectionId;
        var message = new OfferMessage(connectionId, sdp);
        EnqueueMessage(message);
    }

    /// <summary>
    /// Sends an SDP answer to the signaling server for relay to the offering peer.
    /// </summary>
    /// <param name="sdp">The SDP answer string.</param>
    /// <param name="targetConnectionId">The connection ID of the peer that sent the offer.</param>
    public void SendAnswer(string sdp, string? targetConnectionId = null)
    {
        // Use targetConnectionId if provided, otherwise use our own (for broadcast)
        var connectionId = targetConnectionId ?? _connectionId;
        var message = new AnswerMessage(connectionId, sdp);
        EnqueueMessage(message);
    }

    /// <summary>
    /// Sends an ICE candidate to the signaling server for relay to remote peers.
    /// </summary>
    /// <param name="candidate">The ICE candidate string.</param>
    /// <param name="sdpMLineIndex">The SDP media line index.</param>
    /// <param name="sdpMid">The SDP media ID.</param>
    /// <param name="targetConnectionId">The connection ID of the target peer.</param>
    public void SendCandidate(string candidate, int sdpMLineIndex, string sdpMid, string? targetConnectionId = null)
    {
        // Use targetConnectionId if provided, otherwise use our own (for broadcast)
        var connectionId = targetConnectionId ?? _connectionId;
        var message = new CandidateMessage(connectionId, candidate, sdpMLineIndex, sdpMid);
        EnqueueMessage(message);
    }

    private void EnqueueMessage(SignalingMessage message)
    {
        var json = JsonSerializer.Serialize(message, UrsJsonSourceGen.Context.SignalingMessage);
        _sendChannel.Writer.TryWrite(message);
    }

    private async Task SendImmediateAsync(SignalingMessage message, CancellationToken ct)
    {
        if (_webSocket?.State != WebSocketState.Open) return;

        await _sendLock.WaitAsync(ct);
        try
        {
            var json = JsonSerializer.Serialize(message, UrsJsonSourceGen.Context.SignalingMessage);
            var bytes = System.Text.Encoding.UTF8.GetBytes(json);
            await _webSocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task SendLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var message in _sendChannel.Reader.ReadAllAsync(ct))
            {
                if (ct.IsCancellationRequested || _webSocket?.State != WebSocketState.Open)
                    break;
                await SendImmediateAsync(message, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal cancellation
        }
        catch (Exception)
        {
            // Channel closed
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        // Pre-allocate buffer and MemoryStream once — eliminates per-message allocations.
        var buffer = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            while (!ct.IsCancellationRequested && _webSocket?.State == WebSocketState.Open)
            {
                var ms = new MemoryStream();
                try
                {
                    while (true)
                    {
                        var result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);

                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            await HandleDisconnectAsync();
                            return;
                        }

                        ms.Write(buffer, 0, result.Count);

                        if (result.EndOfMessage)
                            break;
                    }

                    if (ms.Length > 0)
                    {
                        var json = System.Text.Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
                        ProcessMessage(json);
                    }
                }
                finally
                {
                    ms.Dispose();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal cancellation
        }
        catch (WebSocketException)
        {
            await HandleDisconnectAsync();
        }
        catch (Exception)
        {
            await HandleDisconnectAsync();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private void ProcessMessage(string json)
    {
        try
        {
            var message = JsonSerializer.Deserialize(json, UrsJsonSourceGen.Context.SignalingMessage);
            if (message == null)
            {
                return;
            }

            switch (message)
            {
                case ConnectAckMessage connectAck:
                    IsPolite = connectAck.Polite;
                    OnConnected?.Invoke(this, new ConnectionEventArgs { ConnectionId = connectAck.ConnectionId });
                    // Reset reconnect delay on successful connection
                    _reconnectDelayMs = BaseReconnectDelayMs;

                    // In public mode both sides get polite=true. We don't create an offer here —
                    // instead we wait for the browser's offer (which carries its connectionId)
                    // and then send our own offer using that connectionId so the browser's Peer
                    // accepts it (peer.js checks this.connectionId == connectionId).
                    if (!IsPolite)
                    {
                        OnShouldCreateOffer?.Invoke(this, EventArgs.Empty);
                    }
                    break;

                case ServerDisconnectMessage disconnect:
                    OnDisconnected?.Invoke(this, new ConnectionEventArgs { ConnectionId = disconnect.ConnectionId });
                    break;

                case ErrorMessage error:
                    OnError?.Invoke(this, new SignalingErrorEventArgs { Message = error.Message });
                    break;

                case RelayedOfferMessage offer:
                    OnOfferReceived?.Invoke(this, new OfferReceivedEventArgs
                    {
                        FromConnectionId = offer.From,
                        Sdp = offer.Data.Sdp,
                        DateTime = offer.Data.DateTime,
                        Polite = offer.Data.Polite
                    });
                    break;

                case RelayedAnswerMessage answer:
                    OnAnswerReceived?.Invoke(this, new AnswerReceivedEventArgs
                    {
                        FromConnectionId = answer.From,
                        Sdp = answer.Data.Sdp,
                        DateTime = answer.Data.DateTime
                    });
                    break;

                case RelayedCandidateMessage candidate:
                    OnCandidateReceived?.Invoke(this, new CandidateReceivedEventArgs
                    {
                        FromConnectionId = candidate.From,
                        Candidate = candidate.Data.Candidate,
                        SdpMLineIndex = candidate.Data.GetSdpMLineIndex(),
                        SdpMid = candidate.Data.GetSdpMid(),
                        DateTime = candidate.Data.DateTime
                    });
                    break;
            }
        }
        catch (JsonException)
        {
            // Ignore malformed messages
        }
    }

    private async Task HandleDisconnectAsync()
    {
        OnConnectionStateChanged?.Invoke(this, false);
        OnDisconnected?.Invoke(this, new ConnectionEventArgs { ConnectionId = _connectionId });

        if (AutoReconnect && !_disposeCts.IsCancellationRequested)
        {
            _ = ReconnectAsync();
        }
    }

    private async Task ReconnectAsync()
    {
        if (_isReconnecting) return;
        _isReconnecting = true;

        try
        {
            while (!_disposeCts.IsCancellationRequested && AutoReconnect)
            {
                try
                {
                    await CloseWebSocketAsync();
                    await Task.Delay(_reconnectDelayMs, _disposeCts.Token);

                    _webSocket = new ClientWebSocket();
                    await _webSocket.ConnectAsync(_serverUri, _disposeCts.Token);

                    // Send connect message
                    var connectMsg = new ConnectMessage(_connectionId);
                    await SendImmediateAsync(connectMsg, _disposeCts.Token);

                    // Restart loops
                    _receiveLoopTask = ReceiveLoopAsync(_disposeCts.Token);
                    _sendLoopTask = SendLoopAsync(_disposeCts.Token);

                    OnConnectionStateChanged?.Invoke(this, true);
                    break;
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception)
                {
                    // Exponential backoff
                    _reconnectDelayMs = Math.Min(_reconnectDelayMs * 2, MaxReconnectDelayMs);
                }
            }
        }
        finally
        {
            _isReconnecting = false;
        }
    }

    private async Task CloseWebSocketAsync()
    {
        if (_webSocket == null) return;

        try
        {
            if (_webSocket.State == WebSocketState.Open)
            {
                await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
            }
        }
        catch
        {
            // Ignore close errors
        }
        finally
        {
            _webSocket.Dispose();
            _webSocket = null;
        }
    }

    /// <summary>
    /// Disposes the signaling client and releases all resources.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        // Complete the send channel first so SendLoopAsync exits
        _sendChannel.Writer.TryComplete();

        _disposeCts.Cancel();

        try
        {
            await CloseWebSocketAsync();
        }
        catch
        {
            // Ignore errors during dispose
        }

        // Wait for background tasks to complete
        try
        {
            if (_receiveLoopTask != null)
                await _receiveLoopTask.WaitAsync(TimeSpan.FromSeconds(2));
            if (_sendLoopTask != null)
                await _sendLoopTask.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // Ignore timeout errors
        }

        _disposeCts.Dispose();
        _sendLock.Dispose();
    }
}