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
    /// Topic for receiving sensor data from SyrenApp
    /// </summary>
    public string SensorDataTopic { get; set; } = "SyrenSystem/SyrenApp/SensorData";

    /// <summary>
    /// Topic for adding speakers from SyrenApp
    /// </summary>
    public string AddSpeakerTopic { get; set; } = "SyrenSystem/SyrenApp/AddSpeaker";
}
