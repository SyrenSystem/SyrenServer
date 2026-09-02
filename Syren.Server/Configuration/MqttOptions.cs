namespace Syren.Server.Configuration;

/// <summary>
/// Configuration options for MQTT broker connection
/// </summary>
public class MqttOptions
{
    public const string SectionName = "Mqtt";

    /// <summary>
    /// MQTT broker host address (default: localhost)
    /// </summary>
    public string Host { get; set; } = "localhost";

    /// <summary>
    /// MQTT broker port (default: 1883)
    /// </summary>
    public int Port { get; set; } = 1883;

    /// <summary>
    /// Client identifier for MQTT connection
    /// </summary>
    public string ClientId { get; set; } = "SyrenServer";

    /// <summary>
    /// Username for authentication (optional)
    /// </summary>
    public string? Username { get; set; }

    /// <summary>
    /// Password for authentication (optional)
    /// </summary>
    public string? Password { get; set; }

    /// <summary>
    /// Enable TLS/SSL connection
    /// </summary>
    public bool UseTls { get; set; } = false;

    /// <summary>
    /// Automatic reconnect on connection loss
    /// </summary>
    public bool AutoReconnect { get; set; } = true;

    /// <summary>
    /// Reconnect delay in seconds
    /// </summary>
    public int ReconnectDelaySeconds { get; set; } = 5;

    /// <summary>
    /// Topic for receiving sensor data from SyrenServer
    /// </summary>
    public string UpdateDistanceTopic { get; set; } = "SyrenSystem/SyrenApp/UpdateDistance";

    /// <summary>
    /// Topic for setting speaker volumes
    /// </summary>
    public string SetSpeakerVolumeTopic { get; set; } = "SyrenSystem/SyrenApp/SetSpeakerVolume";

    /// <summary>
    /// Topic for adding speakers from SyrenServer
    /// </summary>
    public string ConnectSpeakerTopic { get; set; } = "SyrenSystem/SyrenApp/ConnectSpeaker";

    /// <summary>
    /// Topic for removing speakers from SyrenServer
    /// </summary>
    public string DisconnectSpeakerTopic { get; set; } = "SyrenSystem/SyrenApp/DisconnectSpeaker";

    /// <summary>
    /// Topic for sending speaker positions on speaker position update
    /// </summary>
    public string GetSpeakerPositionTopic { get; set; } = "SyrenSystem/SyrenServer/GetSpeakerPosition";

    /// <summary>
    /// Topic for sending user position on speaker distance update
    /// </summary>
    public string GetUserPositionTopic {get; set; } = "SyrenSystem/SyrenServer/GetUserPosition";

    public string ServerStatusTopic { get; set; } = "SyrenSystem/SyrenServer/Status";

    public string ConfigurationTopic { get; set; } = "SyrenSystem/SyrenServer/Configuration";
    public string RuntimeTopic { get; set; } = "SyrenSystem/SyrenServer/Runtime";
    public string CommandResultTopic { get; set; } = "SyrenSystem/SyrenServer/CommandResult";
    public string ConfigureSpeakerTopic { get; set; } = "SyrenSystem/SyrenApp/ConfigureSpeaker";
    public string DeleteSpeakerTopic { get; set; } = "SyrenSystem/SyrenApp/DeleteSpeaker";
    public string UpsertGroupTopic { get; set; } = "SyrenSystem/SyrenApp/UpsertGroup";
    public string DeleteGroupTopic { get; set; } = "SyrenSystem/SyrenApp/DeleteGroup";
    public string SetSpeakerLevelTopic { get; set; } = "SyrenSystem/SyrenApp/SetSpeakerLevel";
}
