using System.Text.Json;
using System.Text.Json.Nodes;
using Syren.Server.Handlers;

namespace Syren.Server.Services;

public sealed class ProfilePlaybackPublisher(
    ProfilePlaybackCoordinator coordinator,
    SessionCatalogueService catalogue,
    SessionTransportBindings bindings,
    ISystemStateStore store,
    IMqttClientService mqtt,
    TimeProvider clock,
    ILogger<ProfilePlaybackPublisher> logger) : BackgroundService
{
    private static readonly TimeSpan OwnerGrace = TimeSpan.FromSeconds(3);

    private readonly Dictionary<string, string> _published = [];
    private readonly SemaphoreSlim _signal = new(0, 1);
    private long _connectionEpoch = -1;
    private long _messageRevision;
    private long _connectedAt;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!coordinator.Enabled)
        {
            return;
        }
        catalogue.BeginGeneration();
        store.Changed += Signal;
        coordinator.Changed += Signal;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                if (mqtt.IsConnected)
                {
                    if (_connectionEpoch != mqtt.ConnectionEpoch)
                    {
                        _published.Clear();
                        _connectionEpoch = mqtt.ConnectionEpoch;
                        _connectedAt = clock.GetTimestamp();
                        catalogue.RefreshHeartbeats();
                    }
                    catalogue.Expire(expireOwners: clock.GetElapsedTime(_connectedAt) >= OwnerGrace);
                    try
                    {
                        await Publish("Configuration", coordinator.Configuration(), stoppingToken);
                        await Publish("Catalogue", catalogue.Snapshot(), stoppingToken);
                        await Publish("Gains", coordinator.Gains(), stoppingToken);
                        await Publish("Receivers", coordinator.ReceiverState(), stoppingToken);
                        await Publish("Bindings", new
                        {
                            stateId = store.Current.StateId, generation = catalogue.Generation,
                            clients = bindings.Confirmed,
                        }, stoppingToken);
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        _published.Clear();
                        logger.LogWarning("Profile playback publication failed: {Type}", exception.GetType().Name);
                    }
                }
                else
                {
                    _published.Clear();
                }
                await _signal.WaitAsync(TimeSpan.FromMilliseconds(100), stoppingToken);
            }
        }
        finally
        {
            store.Changed -= Signal;
            coordinator.Changed -= Signal;
        }
    }

    private void Signal()
    {
        try { _signal.Release(); }
        catch (SemaphoreFullException) { }
    }

    private async Task Publish(string topic, object value, CancellationToken cancellationToken)
    {
        string serialized = JsonSerializer.Serialize(value);
        if (_published.GetValueOrDefault(topic) == serialized)
        {
            return;
        }
        JsonNode payload = JsonNode.Parse(serialized)!;
        if (topic is "Gains" or "Receivers" or "Bindings")
        {
            payload["revision"] = checked(++_messageRevision);
        }
        await mqtt.PublishAsync(ProfilePlaybackHandler.Prefix + topic, payload, retain: true, cancellationToken: cancellationToken);
        _published[topic] = serialized;
    }
}
