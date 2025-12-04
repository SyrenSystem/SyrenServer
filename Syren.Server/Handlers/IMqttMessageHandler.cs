using MQTTnet;
using Syren.Server.Services;

namespace Syren.Server.Handlers;

/// <summary>
/// Interface for handling MQTT messages
/// </summary>
public interface IMqttMessageHandler
{
    /// <summary>
    /// Handle incoming MQTT message
    /// </summary>
    /// <param name="message">MQTT application message</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task HandleMessageAsync(MqttApplicationMessage message, IMqttClientService client, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get the topic this handler is responsible for
    /// </summary>
    string Topic { get; }
}
