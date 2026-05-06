using System.Text.Json;
using System.Text.Json.Serialization;

namespace gStream.Core.Streaming;

/// <summary>
/// Source-generated JSON serializer context for all signaling message types.
/// Provides Native AOT-compatible serialization without reflection.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SignalingMessage))]
[JsonSerializable(typeof(ConnectMessage))]
[JsonSerializable(typeof(DisconnectMessage))]
[JsonSerializable(typeof(ConnectAckMessage))]
[JsonSerializable(typeof(ServerDisconnectMessage))]
[JsonSerializable(typeof(ErrorMessage))]
[JsonSerializable(typeof(OfferMessage))]
[JsonSerializable(typeof(AnswerMessage))]
[JsonSerializable(typeof(CandidateMessage))]
[JsonSerializable(typeof(RelayedOfferMessage))]
[JsonSerializable(typeof(RelayedAnswerMessage))]
[JsonSerializable(typeof(RelayedCandidateMessage))]
[JsonSerializable(typeof(OutgoingOfferData))]
[JsonSerializable(typeof(OutgoingAnswerData))]
[JsonSerializable(typeof(OutgoingCandidateData))]
[JsonSerializable(typeof(RelayedOfferData))]
[JsonSerializable(typeof(RelayedAnswerData))]
[JsonSerializable(typeof(RelayedCandidateData))]
internal partial class UrsJsonSourceGen : JsonSerializerContext
{
    /// <summary>
    /// Singleton context instance with default options (used by SignalingMessageConverter's type-specific deserialization).
    /// The options are configured via <see cref="JsonSourceGenerationOptionsAttribute"/>.
    /// </summary>
    public static readonly UrsJsonSourceGen Context = new UrsJsonSourceGen();
}
