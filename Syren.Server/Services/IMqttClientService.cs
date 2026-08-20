namespace Syren.Server.Services;

/// <summary>
/// Interface for MQTT client service
/// </summary>
public interface IMqttClientService
{
    /// <summary>
    /// Connect to the MQTT broker
    /// </summary>
    Task ConnectAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Disconnect from the MQTT broker
    /// </summary>
    Task DisconnectAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Publish a message to a topic
    /// </summary>
    /// <typeparam name="T">Message type</typeparam>
    /// <param name="topic">MQTT topic</param>
    /// <param name="message">Message payload</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task PublishAsync<T>(
        string topic,
        T message,
        bool retain = false,
        MQTTnet.Protocol.MqttQualityOfServiceLevel qualityOfService = MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce,
        CancellationToken cancellationToken = default
    );

    Task ClearRetainedAsync(string topic, CancellationToken cancellationToken = default);

    Task PublishEmptyAsync(
        string topic,
        bool retain = false,
        MQTTnet.Protocol.MqttQualityOfServiceLevel qualityOfService = MQTTnet.Protocol.MqttQualityOfServiceLevel.AtMostOnce,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Check if the client is connected
    /// </summary>
    bool IsConnected { get; }
}
