using System.Net;
using System.Text.RegularExpressions;
using Syren.Server.Models;

namespace Syren.Server.Services;

public sealed class PcSessionService(ISystemStateStore store, SessionCatalogueService catalogue, ISnapCastService snapcast, ILogger<PcSessionService> logger) : BackgroundService
{
    private static readonly TimeSpan ActiveInterval = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromSeconds(5);
    private static readonly Regex SessionClient = new("^syren-[0-9a-f]{24}$", RegexOptions.CultureInvariant);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string[] _knownClients = [];

    // Starts true so streams and clients left by an earlier run are removed.
    private bool _cleanupPending = true;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (store.Current.Version != 3)
        {
            return;
        }
        while (!stoppingToken.IsCancellationRequested)
        {
            bool pcSessionsOpen = await StepAsync(stoppingToken);
            await Task.Delay(pcSessionsOpen ? ActiveInterval : CleanupInterval, stoppingToken);
        }
    }

    // Asks Snapcast only while PC sessions are open or something may be left over, and returns whether PC sessions are open.
    public async Task<bool> StepAsync(CancellationToken cancellationToken)
    {
        PersistentSystemState current = store.Current;
        string[] clients = SessionClients(current);
        if (!clients.SequenceEqual(_knownClients))
        {
            // A session ended or started, so its old stream or snapclient may now be left over.
            _knownClients = clients;
            _cleanupPending = true;
        }
        bool pcSessionsOpen = PcTransports(current).Length > 0;
        if (pcSessionsOpen || _cleanupPending)
        {
            await ReconcileAsync(cancellationToken);
        }
        return pcSessionsOpen;
    }

    private async Task ReconcileAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            PersistentSystemState current = store.Current;
            var status = await snapcast.GetStatusAsync(cancellationToken);
            SessionTransport[] transports = PcTransports(current);
            bool leftovers = false;
            foreach (var stream in status.Streams.Where(stream => stream.Id.StartsWith("pc-", StringComparison.Ordinal)))
            {
                if (!transports.Any(transport => transport.Endpoint == stream.Id))
                {
                    leftovers = true;
                    await snapcast.RemoveStreamAsync(stream.Id, cancellationToken);
                }
            }
            foreach (SessionTransport transport in transports)
            {
                if (!status.Streams.Any(stream => stream.Id == transport.Endpoint))
                {
                    await snapcast.AddStreamAsync(StreamUri(transport.Endpoint, transport.TcpPort!.Value), cancellationToken);
                }
            }
            // Every PC session gives each speaker a new snapclient identity, so ended ones are deleted once they disconnect.
            HashSet<string> wanted = SessionClients(current).ToHashSet(StringComparer.Ordinal);
            foreach (var client in status.Groups.SelectMany(group => group.Clients)
                .Where(client => SessionClient.IsMatch(client.Id) && !wanted.Contains(client.Id)))
            {
                leftovers = true;
                if (!client.Connected)
                {
                    await snapcast.DeleteClientAsync(client.Id, cancellationToken);
                }
            }
            // One more pass confirms the removals before the loop goes idle.
            _cleanupPending = leftovers;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning("PC transport reconciliation failed: {Type}", exception.GetType().Name);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static SessionTransport[] PcTransports(PersistentSystemState current) =>
        current.Sessions.Where(session => session.Source == "laptop" && session.State != "ended")
            .SelectMany(session => session.Transports).Where(transport => transport.Kind == "snapcast" && transport.TcpPort != null).ToArray();

    private static string[] SessionClients(PersistentSystemState current) =>
        current.Speakers.Where(speaker => speaker.SnapClientId != null)
            .SelectMany(speaker => current.Sessions.Where(session => session.State != "ended")
                .SelectMany(session => session.Transports).Where(transport => transport.Kind == "snapcast")
                .Select(transport => SessionTransportBindings.ClientIdentity(speaker.SnapClientId!, transport.Id)))
            .Distinct().Order(StringComparer.Ordinal).ToArray();

    private static string StreamUri(string identity, int port) =>
        $"tcp://0.0.0.0:{port}?name={identity}&mode=server&sampleformat=48000:16:2&chunk_ms=10&idle_threshold=100&codec=pcm";

    public async Task<object> StartAsync(string requestId, string owner, string instance, string destination,
        string? speakerId, string? senderAddress, string? receiverAddress, long expectedRevision, long generation, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            PersistentSystemState current = store.Current;
            if (current.Revision != expectedRevision || current.Generation != generation)
            {
                throw new InvalidOperationException("Configuration changed while enabling PC audio");
            }
            if (!current.PlaybackActivated || !current.Profiles.Any(profile => profile.Id == owner) ||
                string.IsNullOrWhiteSpace(instance) || (destination != "house" &&
                !current.Groups.Any(group => group.Id == destination && group.SourcePriority.Contains("laptop"))))
            {
                throw new InvalidOperationException("Choose a profile and an available PC destination");
            }
            if (speakerId != null && (!current.Speakers.Any(speaker => speaker.SpeakerId == speakerId) ||
                !IPAddress.TryParse(senderAddress, out var sender) || sender.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork ||
                !IPAddress.TryParse(receiverAddress, out var receiver) || receiver.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork))
            {
                throw new InvalidOperationException("Low latency audio requires a configured speaker and IPv4 addresses");
            }
            HashSet<int> occupied = current.Sessions.Where(session => session.State != "ended")
                .SelectMany(session => session.Transports).Where(transport => transport.TcpPort != null)
                .Select(transport => transport.TcpPort!.Value).ToHashSet();
            int port = Enumerable.Range(4954, 64).First(port => !occupied.Contains(port));
            string identity = Guid.NewGuid().ToString("N");
            string stream = "pc-" + identity;
            var status = await snapcast.GetStatusAsync(cancellationToken);
            foreach (var unused in status.Streams.Where(candidate => candidate.Id.StartsWith("pc-", StringComparison.Ordinal) &&
                !current.Sessions.Any(session => session.State != "ended" && session.Transports.Any(transport => transport.Endpoint == candidate.Id))))
            {
                await snapcast.RemoveStreamAsync(unused.Id, cancellationToken);
            }
            await snapcast.AddStreamAsync(StreamUri(stream, port), cancellationToken);
            List<SessionTransport> transports = [new() { Id = stream, Kind = "snapcast", Endpoint = stream, TcpPort = port, Available = true }];
            if (speakerId != null)
            {
                transports.Add(new() { Id = "rtp-" + identity, Kind = "rtp", SpeakerId = speakerId,
                    Endpoint = $"rtp://{senderAddress}@{receiverAddress}:{46000 + port - 4954}", Available = true });
            }
            bool accepted = catalogue.StartPc(new SessionLifecycleEvent
            {
                Generation = current.Generation, SessionId = identity, ProducerId = instance, OwnerId = owner,
                Source = "laptop", Destination = destination, EventSequence = 1, Action = "start", Transports = transports,
            }, expectedRevision);
            if (!accepted)
            {
                await snapcast.RemoveStreamAsync(stream, cancellationToken);
                throw new InvalidOperationException("PC session changed while enabling audio");
            }
            return new { requestId, success = true, revision = current.Revision, sessionId = identity, transports };
        }
        finally
        {
            _gate.Release();
        }
    }
}
