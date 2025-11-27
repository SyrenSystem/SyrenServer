using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using MQTTnet;
using Syren.Server.Configuration;
using Syren.Server.Handlers;

namespace Syren.Server.Services;

/// <summary>
/// MQTT client service for managing broker connections and message publishing
/// </summary>
public class MqttClientService : IMqttClientService, IAsyncDisposable
{
    private readonly IMqttClient _mqttClient;
    private readonly MqttOptions _options;
    private readonly IEnumerable<IMqttMessageHandler> _handlers;
    private readonly ILogger<MqttClientService> _logger;
    private bool _isConnected;

    public bool IsConnected => _isConnected && _mqttClient.IsConnected;

    public MqttClientService(
        IOptions<MqttOptions> options,
        IEnumerable<IMqttMessageHandler> handlers,
        ILogger<MqttClientService> logger)
    {
        _options = options.Value;
        _handlers = handlers;
        _logger = logger;

        var factory = new MqttClientFactory();
        _mqttClient = factory.CreateMqttClient();

        // Set up event handlers
        _mqttClient.ConnectedAsync += OnConnectedAsync;
        _mqttClient.DisconnectedAsync += OnDisconnectedAsync;
        _mqttClient.ApplicationMessageReceivedAsync += OnMessageReceivedAsync;
    }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var optionsBuilder = new MqttClientOptionsBuilder()
                .WithTcpServer(_options.Host, _options.Port)
                .WithClientId(_options.ClientId)
                .WithCleanSession();

            // Add credentials if provided
            if (!string.IsNullOrEmpty(_options.Username))
            {
                optionsBuilder.WithCredentials(_options.Username, _options.Password);
            }

            // Add TLS if enabled
            if (_options.UseTls)
            {
                optionsBuilder.WithTlsOptions(o => o.UseTls());
            }

            var clientOptions = optionsBuilder.Build();

            _logger.LogInformation("Connecting to MQTT broker at {Host}:{Port}", _options.Host, _options.Port);
            var result = await _mqttClient.ConnectAsync(clientOptions, cancellationToken);

            if (result.ResultCode == MqttClientConnectResultCode.Success)
            {
                _isConnected = true;
                _logger.LogInformation("Successfully connected to MQTT broker");

                // Subscribe to all topics
                await SubscribeToTopicsAsync(cancellationToken);
            }
            else
            {
                _logger.LogError("Failed to connect to MQTT broker: {ResultCode}", result.ResultCode);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error connecting to MQTT broker");
            throw;
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (_mqttClient.IsConnected)
            {
                _logger.LogInformation("Disconnecting from MQTT broker");
                var disconnectOptions = new MqttClientDisconnectOptionsBuilder().Build();
                await _mqttClient.DisconnectAsync(disconnectOptions, cancellationToken);
                _isConnected = false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error disconnecting from MQTT broker");
            throw;
        }
    }

    public async Task PublishAsync<T>(string topic, T message, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!IsConnected)
            {
                _logger.LogWarning("Cannot publish message: MQTT client is not connected");
                return;
            }

            var json = JsonSerializer.Serialize(message);
            var payload = Encoding.UTF8.GetBytes(json);

            var mqttMessage = new MqttApplicationMessageBuilder()
                .WithTopic(topic)
                .WithPayload(payload)
                .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
                .WithRetainFlag(false)
                .Build();

            await _mqttClient.PublishAsync(mqttMessage, cancellationToken);

            _logger.LogDebug("Published message to topic {Topic}: {Payload}", topic, json);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error publishing message to topic {Topic}", topic);
            throw;
        }
    }

    private async Task SubscribeToTopicsAsync(CancellationToken cancellationToken)
    {
        var subscribeOptionsBuilder = new MqttClientSubscribeOptionsBuilder();

        foreach (var handler in _handlers)
        {
            subscribeOptionsBuilder.WithTopicFilter(f =>
                f.WithTopic(handler.Topic)
                 .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce));

            _logger.LogInformation("Subscribing to topic: {Topic}", handler.Topic);
        }

        var subscribeOptions = subscribeOptionsBuilder.Build();
        var result = await _mqttClient.SubscribeAsync(subscribeOptions, cancellationToken);

        foreach (var item in result.Items)
        {
            if (item.ResultCode == MqttClientSubscribeResultCode.GrantedQoS0 ||
                item.ResultCode == MqttClientSubscribeResultCode.GrantedQoS1 ||
                item.ResultCode == MqttClientSubscribeResultCode.GrantedQoS2)
            {
                _logger.LogInformation("Successfully subscribed to topic: {Topic}", item.TopicFilter.Topic);
            }
            else
            {
                _logger.LogError("Failed to subscribe to topic {Topic}: {ResultCode}",
                    item.TopicFilter.Topic, item.ResultCode);
            }
        }
    }

    private Task OnConnectedAsync(MqttClientConnectedEventArgs args)
    {
        _logger.LogInformation("MQTT client connected");
        _isConnected = true;
        return Task.CompletedTask;
    }

    private Task OnDisconnectedAsync(MqttClientDisconnectedEventArgs args)
    {
        _logger.LogWarning("MQTT client disconnected: {Reason}", args.Reason);
        _isConnected = false;
        
        // Auto-reconnect if enabled
        if (_options.AutoReconnect && args.ClientWasConnected)
        {
            _logger.LogInformation("Will attempt to reconnect in {Seconds} seconds", _options.ReconnectDelaySeconds);
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(_options.ReconnectDelaySeconds));
                try
                {
                    await ConnectAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Auto-reconnect failed");
                }
            });
        }
        
        return Task.CompletedTask;
    }

    private async Task OnMessageReceivedAsync(MqttApplicationMessageReceivedEventArgs args)
    {
        var topic = args.ApplicationMessage.Topic;
        _logger.LogDebug("Received message on topic: {Topic}", topic);

        try
        {
            // Find the appropriate handler for this topic
            var handler = _handlers.FirstOrDefault(h => h.Topic == topic);
            if (handler != null)
            {
                await handler.HandleMessageAsync(args.ApplicationMessage);
            }
            else
            {
                _logger.LogWarning("No handler found for topic: {Topic}", topic);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing message from topic {Topic}", topic);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        _mqttClient.Dispose();
    }
}
