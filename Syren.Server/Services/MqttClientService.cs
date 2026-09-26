using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Protocol;
using Syren.Server.Configuration;
using Syren.Server.Handlers;
using Syren.Server.Models;

namespace Syren.Server.Services;

public sealed class MqttClientService : IMqttClientService, IAsyncDisposable
{
    private readonly IMqttClient _mqttClient;
    private readonly MqttOptions _options;
    private readonly IReadOnlyList<IMqttMessageHandler> _handlers;
    private readonly IDistanceService _distanceService;
    private readonly ServerSession _session;
    private readonly ILogger<MqttClientService> _logger;
    private bool _isConnected;
    private long _connectionEpoch;

    public MqttClientService(
        IOptions<MqttOptions> options,
        IEnumerable<IMqttMessageHandler> handlers,
        IDistanceService distanceService,
        ServerSession session,
        ISyrenMqttClientFactory clientFactory,
        ILogger<MqttClientService> logger)
    {
        _options = options.Value;
        _handlers = handlers.ToArray();
        _distanceService = distanceService;
        _session = session;
        _logger = logger;
        _mqttClient = clientFactory.CreateClient();
        _mqttClient.ConnectedAsync += OnConnectedAsync;
        _mqttClient.DisconnectedAsync += OnDisconnectedAsync;
        _mqttClient.ApplicationMessageReceivedAsync += OnMessageReceivedAsync;
    }

    public bool IsConnected => _isConnected && _mqttClient.IsConnected;
    public long ConnectionEpoch => Interlocked.Read(ref _connectionEpoch);

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        var optionsBuilder = new MqttClientOptionsBuilder()
            .WithTcpServer(_options.Host, _options.Port)
            .WithClientId(_options.ClientId)
            .WithCleanSession()
            .WithWillTopic(_options.ServerStatusTopic)
            .WithWillPayload(JsonSerializer.SerializeToUtf8Bytes(CreateStatus(online: false)))
            .WithWillQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .WithWillRetain();

        if (!string.IsNullOrEmpty(_options.Username))
        {
            optionsBuilder.WithCredentials(_options.Username, _options.Password);
        }
        if (_options.UseTls)
        {
            optionsBuilder.WithTlsOptions(tlsOptions => tlsOptions.UseTls());
        }

        try
        {
            MqttClientConnectResult result = await _mqttClient.ConnectAsync(
                optionsBuilder.Build(),
                cancellationToken
            );
            if (result.ResultCode != MqttClientConnectResultCode.Success)
            {
                throw new InvalidOperationException(
                    $"MQTT connection failed with {result.ResultCode}"
                );
            }

            await SubscribeToTopicsAsync(cancellationToken);
            _isConnected = true;
            Interlocked.Increment(ref _connectionEpoch);
            _logger.LogInformation("Connected and subscribed to MQTT at {Host}:{Port}", _options.Host, _options.Port);
        }
        catch (Exception exception)
        {
            _isConnected = false;
            await DisconnectRawClientBestEffortAsync();
            _logger.LogError(exception, "Unable to establish the MQTT connection");
            throw;
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        _isConnected = false;
        if (_mqttClient.IsConnected)
        {
            MqttClientDisconnectOptions options = new MqttClientDisconnectOptionsBuilder().Build();
            await _mqttClient.DisconnectAsync(options, cancellationToken);
        }
    }

    public Task PublishAsync<T>(
        string topic,
        T message,
        bool retain = false,
        MqttQualityOfServiceLevel qualityOfService = MqttQualityOfServiceLevel.AtLeastOnce,
        CancellationToken cancellationToken = default)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(message);
        return PublishPayloadAsync(topic, payload, retain, qualityOfService, cancellationToken);
    }

    public Task ClearRetainedAsync(
        string topic,
        CancellationToken cancellationToken = default) =>
        PublishPayloadAsync(
            topic,
            [],
            retain: true,
            MqttQualityOfServiceLevel.AtLeastOnce,
            cancellationToken
        );

    public Task PublishEmptyAsync(
        string topic,
        bool retain = false,
        MqttQualityOfServiceLevel qualityOfService = MqttQualityOfServiceLevel.AtMostOnce,
        CancellationToken cancellationToken = default) =>
        PublishPayloadAsync(topic, [], retain, qualityOfService, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        try
        {
            await DisconnectAsync();
        }
        finally
        {
            _mqttClient.Dispose();
        }
    }

    private async Task PublishPayloadAsync(
        string topic,
        byte[] payload,
        bool retain,
        MqttQualityOfServiceLevel qualityOfService,
        CancellationToken cancellationToken)
    {
        if (!IsConnected)
        {
            throw new InvalidOperationException("MQTT client is not connected and subscribed");
        }

        var message = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .WithQualityOfServiceLevel(qualityOfService)
            .WithRetainFlag(retain)
            .Build();
        try
        {
            await _mqttClient.PublishAsync(message, cancellationToken);
        }
        catch
        {
            _isConnected = false;
            await DisconnectRawClientBestEffortAsync();
            throw;
        }
    }

    private async Task SubscribeToTopicsAsync(CancellationToken cancellationToken)
    {
        var builder = new MqttClientSubscribeOptionsBuilder();
        foreach (IMqttMessageHandler handler in _handlers)
        {
            builder.WithTopicFilter(topicFilter => topicFilter
                .WithTopic(handler.Topic)
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce));
        }

        MqttClientSubscribeResult result = await _mqttClient.SubscribeAsync(
            builder.Build(),
            cancellationToken
        );
        MqttClientSubscribeResultItem[] deniedResults = result.Items
            .Where(subscribeResult => !MqttSubscriptionValidator.AreAllGranted(
                [subscribeResult.ResultCode]
            ))
            .ToArray();
        if (deniedResults.Length != 0)
        {
            string failures = string.Join(
                ", ",
                deniedResults.Select(subscribeResult =>
                    $"{subscribeResult.TopicFilter.Topic}: {subscribeResult.ResultCode}")
            );
            throw new InvalidOperationException($"MQTT subscriptions denied: {failures}");
        }
    }

    private Task OnConnectedAsync(MqttClientConnectedEventArgs eventArgs)
    {
        _logger.LogDebug("Raw MQTT transport connected");
        return Task.CompletedTask;
    }

    private Task OnDisconnectedAsync(MqttClientDisconnectedEventArgs eventArgs)
    {
        _isConnected = false;
        _logger.LogWarning("MQTT client disconnected: {Reason}", eventArgs.Reason);
        return Task.CompletedTask;
    }

    private async Task OnMessageReceivedAsync(MqttApplicationMessageReceivedEventArgs eventArgs)
    {
        string topic = eventArgs.ApplicationMessage.Topic;
        IMqttMessageHandler? handler = _handlers.FirstOrDefault(candidate => candidate.Topic == topic);
        if (handler == null)
        {
            _logger.LogWarning("No MQTT handler is registered for {Topic}", topic);
            return;
        }

        try
        {
            await handler.HandleMessageAsync(eventArgs.ApplicationMessage, this);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Unable to process MQTT message from {Topic}", topic);
        }
    }

    private async Task DisconnectRawClientBestEffortAsync()
    {
        if (!_mqttClient.IsConnected)
        {
            return;
        }
        try
        {
            MqttClientDisconnectOptions options = new MqttClientDisconnectOptionsBuilder().Build();
            await _mqttClient.DisconnectAsync(options, CancellationToken.None);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Unable to disconnect the raw MQTT transport");
        }
    }

    private ServerStatusMessage CreateStatus(bool online) => new()
    {
        SessionId = _session.Id,
        StateId = _distanceService.StateId,
        Online = online,
    };
}
