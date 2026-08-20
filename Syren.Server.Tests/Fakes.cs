using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MQTTnet.Protocol;
using Syren.Server.Configuration;
using Syren.Server.Models;
using Syren.Server.Services;

namespace Syren.Server.Tests;

internal sealed class MemoryStateStore : ISystemStateStore
{
    public MemoryStateStore(PersistentSystemState? state = null)
    {
        Current = state ?? new PersistentSystemState { StateId = Guid.NewGuid().ToString() };
    }

    public PersistentSystemState Current { get; private set; }
    public bool FailSaves { get; set; }

    public void Save(PersistentSystemState state)
    {
        if (FailSaves)
        {
            throw new IOException("State storage failed");
        }
        Current = state;
    }
}

internal sealed class RecordingSnapCastService : ISnapCastService
{
    public List<(string ClientId, int Percent)> VolumeChanges { get; } = [];
    public HashSet<string> FailingClientIds { get; } = [];
    public TaskCompletionSource? Blocker { get; set; }

    public async Task SetClientVolumeAsync(
        string id,
        int percent,
        CancellationToken cancellationToken = default)
    {
        if (FailingClientIds.Contains(id))
        {
            throw new HttpRequestException("Snapclient unavailable");
        }
        if (Blocker != null)
        {
            await Blocker.Task.WaitAsync(cancellationToken);
        }
        VolumeChanges.Add((id, percent));
    }
}

internal sealed class FakeMqttClientService : IMqttClientService
{
    private int _failuresRemaining;

    public FakeMqttClientService(int failuresBeforeSuccess = 0)
    {
        _failuresRemaining = failuresBeforeSuccess;
    }

    public bool IsConnected { get; private set; }
    public int ConnectionAttempts { get; private set; }
    public List<(string Topic, object Message, bool Retain, MqttQualityOfServiceLevel Quality)> Published { get; } = [];
    public List<string> Cleared { get; } = [];

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        ConnectionAttempts++;
        if (_failuresRemaining-- > 0)
        {
            throw new InvalidOperationException("Broker unavailable");
        }
        IsConnected = true;
        return Task.CompletedTask;
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        IsConnected = false;
        return Task.CompletedTask;
    }

    public Task PublishAsync<T>(
        string topic,
        T message,
        bool retain = false,
        MqttQualityOfServiceLevel qualityOfService = MqttQualityOfServiceLevel.AtLeastOnce,
        CancellationToken cancellationToken = default)
    {
        Published.Add((topic, message!, retain, qualityOfService));
        return Task.CompletedTask;
    }

    public Task ClearRetainedAsync(string topic, CancellationToken cancellationToken = default)
    {
        Cleared.Add(topic);
        return Task.CompletedTask;
    }

    public Task PublishEmptyAsync(
        string topic,
        bool retain = false,
        MqttQualityOfServiceLevel qualityOfService = MqttQualityOfServiceLevel.AtMostOnce,
        CancellationToken cancellationToken = default)
    {
        Cleared.Add(topic);
        return Task.CompletedTask;
    }
}

internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _responseFactory;

    public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
    {
        _responseFactory = responseFactory;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken) => Task.FromResult(_responseFactory(request));

    public static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };
}

internal static class TestServices
{
    public static DistanceService CreateDistanceService(
        ISnapCastService snapCastService,
        MemoryStateStore? stateStore = null,
        params SpeakerInfo[] speakers)
    {
        if (speakers.Length == 0)
        {
            speakers =
            [
                new SpeakerInfo
                {
                    SensorId = "sensor",
                    SnapClientId = "snap-client",
                    FullVolumeDistance = 0,
                    MuteDistance = 100,
                },
            ];
        }
        return new DistanceService(
            Options.Create(new SpeakersOptions { SpeakersInfo = speakers }),
            snapCastService,
            stateStore ?? new MemoryStateStore(),
            NullLogger<DistanceService>.Instance
        );
    }
}
