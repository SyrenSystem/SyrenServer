using System.Security.Cryptography;
using System.Text;
using Syren.Server.Models;
using Syren.Server.Models.SnapCast;

namespace Syren.Server.Services;

public sealed class SessionTransportBindings(
    ProfilePlaybackCoordinator coordinator,
    ISystemStateStore store,
    ISnapCastService snapcast,
    ILogger<SessionTransportBindings> logger) : BackgroundService
{
    private Dictionary<string, string> _confirmed = [];
    public IReadOnlyDictionary<string, string> Confirmed => Volatile.Read(ref _confirmed);

    public static string ClientIdentity(string physicalId, string transportId) =>
        "syren-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(physicalId + "\0" + transportId)))[..24];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!coordinator.Enabled)
        {
            return;
        }
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ReconcileAsync(stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                Volatile.Write(ref _confirmed, []);
                logger.LogWarning("Session transport binding failed: {Type}", exception.GetType().Name);
            }
            await Task.Delay(500, stoppingToken);
        }
    }

    public async Task ReconcileAsync(CancellationToken cancellationToken)
    {
        PersistentSystemState current = store.Current;
        var desired = new Dictionary<string, string>();
        foreach (var report in coordinator.InputReports())
        {
            string physicalId = report.GetProperty("physicalClientId").GetString()!;
            string speakerId = report.GetProperty("speakerId").GetString()!;
            PersistentPlaybackGroup? playbackGroup = current.Groups.Find(group => group.SpeakerIds.Contains(speakerId));
            if (!report.TryGetProperty("inputs", out var inputs) || playbackGroup == null)
            {
                continue;
            }
            foreach (var input in inputs.EnumerateArray())
            {
                PlaybackSession? session = current.Sessions.Find(session => session.Id == input.GetProperty("sessionId").GetString());
                SessionTransport? transport = session?.Transports.Find(transport => transport.Kind == "snapcast" &&
                    transport.Id == input.GetProperty("transportId").GetString());
                if (session == null || session.State == "ended" || transport == null ||
                    !playbackGroup.SourcePriority.Contains(session.Source) ||
                    (session.Destination != "house" && session.Destination != playbackGroup.Id))
                {
                    continue;
                }
                string clientId = ClientIdentity(physicalId, transport.Id);
                if (clientId == input.GetProperty("clientId").GetString() && input.GetProperty("receiving").GetBoolean())
                {
                    desired[clientId] = transport.Endpoint;
                }
            }
        }
        if (desired.Count == 0)
        {
            Volatile.Write(ref _confirmed, []);
            return;
        }
        SnapServerStatus status = await snapcast.GetStatusAsync(cancellationToken);
        var confirmed = new Dictionary<string, string>();
        foreach ((string clientId, string streamId) in desired)
        {
            SnapGroupStatus? group = status.Groups.FirstOrDefault(group => group.Clients.Any(client => client.Id == clientId && client.Connected));
            if (group == null || !status.Streams.Any(stream => stream.Id == streamId))
            {
                continue;
            }
            if (group.Clients.Any(client => desired.GetValueOrDefault(client.Id) != streamId))
            {
                await snapcast.SetGroupClientsAsync(group.Id,
                    group.Clients.Where(client => desired.GetValueOrDefault(client.Id) == streamId).Select(client => client.Id).ToArray(), cancellationToken);
                continue;
            }
            if (group.StreamId != streamId)
            {
                await snapcast.SetGroupStreamAsync(group.Id, streamId, cancellationToken);
            }
            if (group.Muted)
            {
                await snapcast.SetGroupMuteAsync(group.Id, false, cancellationToken);
            }
            SnapClientStatus client = group.Clients.Single(client => client.Id == clientId);
            if (client.Config?.Volume?.Percent != 100 || client.Config?.Volume?.Muted != false)
            {
                await snapcast.SetClientVolumeAsync(clientId, 100, cancellationToken);
            }
            confirmed[clientId] = streamId;
        }
        Volatile.Write(ref _confirmed, confirmed);
    }
}
