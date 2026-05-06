using System.Text.Json;
using System.Text.Json.Serialization;

namespace gStream.Core.Streaming;

/// <summary>
/// Base class for all signaling messages. Supports discriminated union via the 'type' field.
/// </summary>
[JsonConverter(typeof(SignalingMessageConverter))]
public abstract class SignalingMessage
{
    [JsonPropertyName("type")]
    public string Type { get; protected set; } = string.Empty;
}

// ==================== Client → Server Messages ====================

/// <summary>
/// Client sends this to register with the signaling server.
/// </summary>
public sealed class ConnectMessage : SignalingMessage
{
    public ConnectMessage()
    {
        Type = "connect";
    }

    public ConnectMessage(string connectionId) : this()
    {
        ConnectionId = connectionId;
    }

    [JsonPropertyName("connectionId")]
    public string ConnectionId { get; set; } = string.Empty;
}

/// <summary>
/// Client sends this to unregister from the signaling server.
/// </summary>
public sealed class DisconnectMessage : SignalingMessage
{
    public DisconnectMessage()
    {
        Type = "disconnect";
    }

    public DisconnectMessage(string connectionId) : this()
    {
        ConnectionId = connectionId;
    }

    [JsonPropertyName("connectionId")]
    public string ConnectionId { get; set; } = string.Empty;
}

/// <summary>
/// Client sends an SDP offer to the server for relay.
/// Format: { type: "offer", from: connectionId, data: { connectionId, sdp } }
/// </summary>
public sealed class OfferMessage : SignalingMessage
{
    public OfferMessage()
    {
        Type = "offer";
    }

    public OfferMessage(string connectionId, string sdp) : this()
    {
        From = connectionId;
        Data = new OutgoingOfferData { ConnectionId = connectionId, Sdp = sdp };
    }

    [JsonPropertyName("from")]
    public string From { get; set; } = string.Empty;

    [JsonPropertyName("data")]
    public OutgoingOfferData? Data { get; set; }
}

/// <summary>
/// Payload for outgoing offer messages (client → server).
/// </summary>
public sealed class OutgoingOfferData
{
    [JsonPropertyName("connectionId")]
    public string ConnectionId { get; set; } = string.Empty;

    [JsonPropertyName("sdp")]
    public string Sdp { get; set; } = string.Empty;
}

/// <summary>
/// Client sends an SDP answer to the server for relay.
/// Format: { type: "answer", from: connectionId, data: { connectionId, sdp } }
/// </summary>
public sealed class AnswerMessage : SignalingMessage
{
    public AnswerMessage()
    {
        Type = "answer";
    }

    public AnswerMessage(string connectionId, string sdp) : this()
    {
        From = connectionId;
        Data = new OutgoingAnswerData { ConnectionId = connectionId, Sdp = sdp };
    }

    [JsonPropertyName("from")]
    public string From { get; set; } = string.Empty;

    [JsonPropertyName("data")]
    public OutgoingAnswerData? Data { get; set; }
}

/// <summary>
/// Payload for outgoing answer messages (client → server).
/// </summary>
public sealed class OutgoingAnswerData
{
    [JsonPropertyName("connectionId")]
    public string ConnectionId { get; set; } = string.Empty;

    [JsonPropertyName("sdp")]
    public string Sdp { get; set; } = string.Empty;
}

/// <summary>
/// Client sends an ICE candidate to the server for relay.
/// Format: { type: "candidate", from: connectionId, data: { connectionId, candidate, sdpMLineIndex, sdpMid } }
/// </summary>
public sealed class CandidateMessage : SignalingMessage
{
    public CandidateMessage()
    {
        Type = "candidate";
    }

    public CandidateMessage(string connectionId, string candidate, int sdpMLineIndex, string sdpMid) : this()
    {
        From = connectionId;
        Data = new OutgoingCandidateData
        {
            ConnectionId = connectionId,
            Candidate = candidate,
            SdpMLineIndex = sdpMLineIndex,
            SdpMid = sdpMid
        };
    }

    [JsonPropertyName("from")]
    public string From { get; set; } = string.Empty;

    [JsonPropertyName("data")]
    public OutgoingCandidateData? Data { get; set; }
}

/// <summary>
/// Payload for outgoing candidate messages (client → server).
/// </summary>
public sealed class OutgoingCandidateData
{
    [JsonPropertyName("connectionId")]
    public string ConnectionId { get; set; } = string.Empty;

    [JsonPropertyName("candidate")]
    public string Candidate { get; set; } = string.Empty;

    [JsonPropertyName("sdpMLineIndex")]
    public int SdpMLineIndex { get; set; }

    [JsonPropertyName("sdpMid")]
    public string SdpMid { get; set; } = string.Empty;
}

// ==================== Server → Client Messages ====================

/// <summary>
/// Server acknowledges client connection.
/// </summary>
public sealed class ConnectAckMessage : SignalingMessage
{
    public ConnectAckMessage()
    {
        Type = "connect";
    }

    [JsonPropertyName("connectionId")]
    public string ConnectionId { get; set; } = string.Empty;

    [JsonPropertyName("polite")]
    public bool Polite { get; set; }
}

/// <summary>
/// Server notifies about a disconnection.
/// </summary>
public sealed class ServerDisconnectMessage : SignalingMessage
{
    public ServerDisconnectMessage()
    {
        Type = "disconnect";
    }

    [JsonPropertyName("connectionId")]
    public string ConnectionId { get; set; } = string.Empty;
}

/// <summary>
/// Server sends an error message.
/// </summary>
public sealed class ErrorMessage : SignalingMessage
{
    public ErrorMessage()
    {
        Type = "error";
    }

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// Base class for relayed messages (offer/answer/candidate from another peer).
/// These have 'from', 'to', and 'data' fields.
/// </summary>
public abstract class RelayedMessage : SignalingMessage
{
    [JsonPropertyName("from")]
    public string From { get; set; } = string.Empty;

    [JsonPropertyName("to")]
    public string To { get; set; } = string.Empty;
}

/// <summary>
/// Payload data for relayed offer messages (server → client).
/// </summary>
public sealed class RelayedOfferData
{
    [JsonPropertyName("sdp")]
    public string Sdp { get; set; } = string.Empty;

    [JsonPropertyName("datetime")]
    public long DateTime { get; set; }

    [JsonPropertyName("polite")]
    public bool Polite { get; set; }
}

/// <summary>
/// Relayed offer message from another peer via the server.
/// </summary>
public sealed class RelayedOfferMessage : RelayedMessage
{
    public RelayedOfferMessage()
    {
        Type = "offer";
    }

    [JsonPropertyName("data")]
    public RelayedOfferData Data { get; set; } = new();
}

/// <summary>
/// Payload data for relayed answer messages (server → client).
/// </summary>
public sealed class RelayedAnswerData
{
    [JsonPropertyName("sdp")]
    public string Sdp { get; set; } = string.Empty;

    [JsonPropertyName("datetime")]
    public long DateTime { get; set; }
}

/// <summary>
/// Relayed answer message from another peer via the server.
/// </summary>
public sealed class RelayedAnswerMessage : RelayedMessage
{
    public RelayedAnswerMessage()
    {
        Type = "answer";
    }

    [JsonPropertyName("data")]
    public RelayedAnswerData Data { get; set; } = new();
}

/// <summary>
/// Payload data for relayed ICE candidate messages (server → client).
/// Uses JsonElement for sdpMLineIndex and sdpMid because the browser may send
/// them as either strings ("0") or integers (0), inconsistently.
/// </summary>
public sealed class RelayedCandidateData
{
    [JsonPropertyName("candidate")]
    public string Candidate { get; set; } = string.Empty;

    /// <summary>
    /// SDP media line index. Accepts both string ("0") and integer (0) from JSON.
    /// </summary>
    [JsonPropertyName("sdpMLineIndex")]
    public JsonElement SdpMLineIndexElement { get; set; }

    /// <summary>
    /// SDP media ID. Accepts both string ("0") and integer (0) from JSON.
    /// </summary>
    [JsonPropertyName("sdpMid")]
    public JsonElement SdpMidElement { get; set; }

    [JsonPropertyName("datetime")]
    public long DateTime { get; set; }

    /// <summary>
    /// Parses sdpMLineIndex from either a JSON string or integer value.
    /// </summary>
    public int GetSdpMLineIndex()
    {
        return SdpMLineIndexElement.ValueKind switch
        {
            JsonValueKind.Number => SdpMLineIndexElement.GetInt32(),
            JsonValueKind.String => int.TryParse(SdpMLineIndexElement.GetString(), out var val) ? val : 0,
            _ => 0
        };
    }

    /// <summary>
    /// Parses sdpMid from either a JSON string or integer value.
    /// </summary>
    public string GetSdpMid()
    {
        return SdpMidElement.ValueKind switch
        {
            JsonValueKind.String => SdpMidElement.GetString() ?? "0",
            JsonValueKind.Number => SdpMidElement.GetInt32().ToString(),
            _ => "0"
        };
    }
}

/// <summary>
/// Relayed ICE candidate message from another peer via the server.
/// </summary>
public sealed class RelayedCandidateMessage : RelayedMessage
{
    public RelayedCandidateMessage()
    {
        Type = "candidate";
    }

    [JsonPropertyName("data")]
    public RelayedCandidateData Data { get; set; } = new();
}

/// <summary>
/// Custom JSON converter for discriminated union deserialization of SignalingMessage types.
/// </summary>
public sealed class SignalingMessageConverter : JsonConverter<SignalingMessage>
{
    public override SignalingMessage? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;

        if (!root.TryGetProperty("type", out var typeProp))
        {
            throw new JsonException("Missing 'type' property in signaling message");
        }

        var messageType = typeProp.GetString();

        // Check if this is a relayed message (has 'from' and 'to' fields)
        var hasFrom = root.TryGetProperty("from", out _);
        var hasTo = root.TryGetProperty("to", out _);
        var isRelayed = hasFrom && hasTo;

        return messageType switch
        {
            "connect" => isRelayed
                ? throw new JsonException("Invalid connect message format")
                : root.TryGetProperty("polite", out _) // Has 'polite' = server ack
                    ? JsonSerializer.Deserialize(root.GetRawText(), UrsJsonSourceGen.Context.ConnectAckMessage)
                    : JsonSerializer.Deserialize(root.GetRawText(), UrsJsonSourceGen.Context.ConnectMessage),
            "disconnect" => JsonSerializer.Deserialize(root.GetRawText(), UrsJsonSourceGen.Context.ServerDisconnectMessage), // Client only receives server-sent disconnects; client-sent DisconnectMessage is outbound only
            "error" => JsonSerializer.Deserialize(root.GetRawText(), UrsJsonSourceGen.Context.ErrorMessage),
            "offer" => isRelayed
                ? JsonSerializer.Deserialize(root.GetRawText(), UrsJsonSourceGen.Context.RelayedOfferMessage)
                : JsonSerializer.Deserialize(root.GetRawText(), UrsJsonSourceGen.Context.OfferMessage),
            "answer" => isRelayed
                ? JsonSerializer.Deserialize(root.GetRawText(), UrsJsonSourceGen.Context.RelayedAnswerMessage)
                : JsonSerializer.Deserialize(root.GetRawText(), UrsJsonSourceGen.Context.AnswerMessage),
            "candidate" => isRelayed
                ? JsonSerializer.Deserialize(root.GetRawText(), UrsJsonSourceGen.Context.RelayedCandidateMessage)
                : JsonSerializer.Deserialize(root.GetRawText(), UrsJsonSourceGen.Context.CandidateMessage),
            _ => throw new JsonException($"Unknown message type: {messageType}")
        };
    }

    public override void Write(Utf8JsonWriter writer, SignalingMessage value, JsonSerializerOptions options)
    {
        switch (value)
        {
            case ConnectMessage msg:
                JsonSerializer.Serialize(writer, msg, UrsJsonSourceGen.Context.ConnectMessage);
                break;
            case DisconnectMessage msg:
                JsonSerializer.Serialize(writer, msg, UrsJsonSourceGen.Context.DisconnectMessage);
                break;
            case ConnectAckMessage msg:
                JsonSerializer.Serialize(writer, msg, UrsJsonSourceGen.Context.ConnectAckMessage);
                break;
            case ServerDisconnectMessage msg:
                JsonSerializer.Serialize(writer, msg, UrsJsonSourceGen.Context.ServerDisconnectMessage);
                break;
            case ErrorMessage msg:
                JsonSerializer.Serialize(writer, msg, UrsJsonSourceGen.Context.ErrorMessage);
                break;
            case OfferMessage msg:
                JsonSerializer.Serialize(writer, msg, UrsJsonSourceGen.Context.OfferMessage);
                break;
            case AnswerMessage msg:
                JsonSerializer.Serialize(writer, msg, UrsJsonSourceGen.Context.AnswerMessage);
                break;
            case CandidateMessage msg:
                JsonSerializer.Serialize(writer, msg, UrsJsonSourceGen.Context.CandidateMessage);
                break;
            case RelayedOfferMessage msg:
                JsonSerializer.Serialize(writer, msg, UrsJsonSourceGen.Context.RelayedOfferMessage);
                break;
            case RelayedAnswerMessage msg:
                JsonSerializer.Serialize(writer, msg, UrsJsonSourceGen.Context.RelayedAnswerMessage);
                break;
            case RelayedCandidateMessage msg:
                JsonSerializer.Serialize(writer, msg, UrsJsonSourceGen.Context.RelayedCandidateMessage);
                break;
            default:
                throw new JsonException($"Unknown signaling message type: {value.GetType().Name}");
        }
    }
}

/// <summary>
/// JSON serialization options configured for URS WebApp protocol compatibility.
/// </summary>
public static class UrsJsonOptions
{
    public static readonly JsonSerializerOptions Options;

    static UrsJsonOptions()
    {
        Options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
        Options.Converters.Add(new SignalingMessageConverter());
    }
}