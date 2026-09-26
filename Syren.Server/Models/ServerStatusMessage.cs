using System.Text.Json.Serialization;

namespace Syren.Server.Models;

public sealed class ServerStatusMessage
{
    [JsonPropertyName("sessionId")]
    public required string SessionId { get; init; }

    [JsonPropertyName("stateId")]
    public required string StateId { get; init; }

    [JsonPropertyName("online")]
    public required bool Online { get; init; }

    [JsonPropertyName("connectedSpeakerIds")]
    public string[]? ConnectedSpeakerIds { get; init; }

    // Apps leave profile playback when an online server does not announce protocol 3.
    [JsonPropertyName("protocolVersion")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ProtocolVersion { get; init; }

    public static ServerStatusMessage Create(
        string sessionId,
        string stateId,
        bool online,
        string[] connectedSpeakerIds,
        int? protocolVersion = null) => new()
    {
        SessionId = sessionId,
        StateId = stateId,
        Online = online,
        ConnectedSpeakerIds = connectedSpeakerIds,
        ProtocolVersion = protocolVersion,
    };
}
