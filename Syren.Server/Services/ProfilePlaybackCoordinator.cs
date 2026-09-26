using System.Text.Json;
using System.Text.Json.Nodes;
using Syren.Server.Models;

namespace Syren.Server.Services;

public sealed class ProfilePlaybackCoordinator(
    ISystemStateStore store,
    ISystemConfigurationService configuration,
    SessionCatalogueService catalogue,
    ProfileConfigurationService profiles,
    ProfilePositionService positions,
    PcSessionService pcSessions,
    TimeProvider clock)
{
    private readonly object _sync = new();
    private static readonly TimeSpan ReceiverTimeout = TimeSpan.FromSeconds(3);
    private readonly Dictionary<string, (JsonElement Status, long Updated)> _receivers = [];
    private readonly Dictionary<string, (string Profile, long Generation, DateTimeOffset Expires)> _links = [];
    private bool _compatibleAppSeen;
    public event Action? Changed;

    public bool Enabled => store.Current.Version == 3;
    public long Generation => catalogue.Generation;

    public object Configuration()
    {
        PersistentSystemState current = store.Current;
        return new
        {
            protocolVersion = 3, stateId = current.StateId, generation = current.Generation,
            revision = current.Revision, playbackActivated = current.PlaybackActivated,
            profiles = current.Profiles, sourcePolicies = current.SourcePolicies,
            speakers = configuration.GetConfiguration().Speakers,
            sources = configuration.GetConfiguration().Sources,
            groups = current.Groups.Select(group => new
            {
                id = group.Id, name = group.Name, speakerIds = group.SpeakerIds,
                enabledSources = group.SourcePriority, sourceLevels = group.SourceLevels,
                masterVolume = group.MasterVolume, muted = group.Muted,
            }).ToArray(),
        };
    }

    public object Gains() => new
    {
        stateId = store.Current.StateId, generation = catalogue.Generation,
        configurationRevision = store.Current.Revision, catalogueRevision = store.Current.CatalogueRevision,
        gains = positions.DesiredGains(),
    };

    public object ReceiverState()
    {
        lock (_sync)
        {
            return new
            {
                generation = catalogue.Generation,
                receivers = _receivers.Select(pair => new
                {
                    speakerId = pair.Key, online = clock.GetElapsedTime(pair.Value.Updated) < ReceiverTimeout,
                    status = StableStatus(pair.Value.Status),
                }).ToArray(),
            };
        }
    }

    public JsonElement[] InputReports()
    {
        lock (_sync)
        {
            return _receivers.Values.Where(receiver => clock.GetElapsedTime(receiver.Updated) < ReceiverTimeout)
                .Select(receiver => receiver.Status).ToArray();
        }
    }

    public LifecycleOutcome Lifecycle(JsonElement payload)
    {
        if (!Enabled)
        {
            return LifecycleOutcome.Ignored;
        }
        LifecycleOutcome outcome = catalogue.Apply(payload.Deserialize<SessionLifecycleEvent>()!);
        if (outcome == LifecycleOutcome.Accepted)
        {
            Changed?.Invoke();
        }
        return outcome;
    }

    public bool Position(JsonElement payload)
    {
        bool accepted = Enabled && positions.Report(Text(payload, "profileId"), Text(payload, "instanceId"),
            Text(payload, "lease"), payload.GetProperty("sequence").GetInt64(),
            payload.GetProperty("distances").Deserialize<Dictionary<string, double>>()!,
            payload.GetProperty("generation").GetInt64());
        if (accepted)
        {
            Changed?.Invoke();
        }
        return accepted;
    }

    public bool ReceiverStatus(JsonElement payload)
    {
        if (!Enabled || payload.GetProperty("generation").GetInt64() != catalogue.Generation ||
            payload.GetProperty("protocolVersion").GetInt32() != 3)
        {
            return false;
        }
        string speakerId = Text(payload, "speakerId");
        PersistentSpeakerState? speaker = store.Current.Speakers.Find(candidate => candidate.SpeakerId == speakerId);
        if (speaker == null || speaker.SnapClientId != Text(payload, "physicalClientId"))
        {
            return false;
        }
        lock (_sync)
        {
            long epoch = payload.GetProperty("bootSequence").GetInt64();
            if (epoch < store.Current.ReceiverEpochs.GetValueOrDefault(speakerId))
            {
                return false;
            }
            if (epoch > store.Current.ReceiverEpochs.GetValueOrDefault(speakerId))
            {
                store.Update(current => current with { ReceiverEpochs = new(current.ReceiverEpochs) { [speakerId] = epoch } });
            }
            if (_receivers.TryGetValue(speakerId, out var previous) &&
                Text(previous.Status, "instanceId") == Text(payload, "instanceId") &&
                previous.Status.GetProperty("sequence").GetInt64() >= payload.GetProperty("sequence").GetInt64())
            {
                return false;
            }
            _receivers[speakerId] = (payload.Clone(), clock.GetTimestamp());
        }
        Changed?.Invoke();
        return true;
    }

    public async Task<object> CommandAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        string requestId = Text(payload, "requestId");
        PersistentSystemState current = store.Current;
        if (!Enabled || payload.GetProperty("protocolVersion").GetInt32() != 3 ||
            Text(payload, "stateId") != current.StateId || payload.GetProperty("generation").GetInt64() != current.Generation ||
            payload.GetProperty("expectedRevision").GetInt64() != current.Revision)
        {
            return new CommandResultMessage { RequestId = requestId, Success = false, Revision = current.Revision, Error = "Stale configuration or incompatible controller" };
        }
        string action = Text(payload, "action");
        if (action is "profile" or "policy" or "unlink")
        {
            CommandResultMessage configured = profiles.Configure(payload.Deserialize<ProfileConfigurationCommand>()!);
            if (action == "unlink" && configured.Success)
            {
                lock (_sync)
                {
                    foreach (string ticket in _links.Where(pair => pair.Value.Profile == Text(payload, "profileId")).Select(pair => pair.Key).ToArray())
                    {
                        _links.Remove(ticket);
                    }
                }
            }
            return configured;
        }
        if (action == "controllerReady")
        {
            _compatibleAppSeen = new[] { "profiles", "source-settings", "position-ownership", "pc-ownership" }.All(capability =>
                payload.GetProperty("capabilities").EnumerateArray().Any(value => value.GetString() == capability));
            return new CommandResultMessage { RequestId = requestId, Success = _compatibleAppSeen, Revision = current.Revision };
        }
        if (action == "pc")
        {
            string? Optional(string property) => payload.TryGetProperty(property, out var value) ? value.GetString() : null;
            return await pcSessions.StartAsync(requestId, Text(payload, "profileId"), Text(payload, "instanceId"),
                Text(payload, "destination"), Optional("speakerId"), Optional("senderAddress"), Optional("receiverAddress"), current.Revision, current.Generation, cancellationToken);
        }
        if (action == "linkSpotify")
        {
            string profileId = Text(payload, "profileId");
            ListenerProfile? profile = current.Profiles.Find(profile => profile.Id == profileId);
            if (profile == null || profile.SpotifyAccountId != null)
            {
                return new CommandResultMessage { RequestId = requestId, Success = false, Revision = current.Revision,
                    Error = "Choose an unlinked profile" };
            }
            string ticket = Guid.NewGuid().ToString("N");
            lock (_sync)
            {
                foreach (string previous in _links.Where(pair => pair.Value.Profile == profileId ||
                    pair.Value.Expires <= clock.GetUtcNow()).Select(pair => pair.Key).ToArray())
                {
                    _links.Remove(previous);
                }
                _links[ticket] = (profileId, current.Generation, clock.GetUtcNow().AddMinutes(10));
            }
            return new { requestId, success = true, revision = current.Revision,
                linkRequest = new { ticket, profileId, stateId = current.StateId, generation = current.Generation } };
        }
        if (action == "select")
        {
            string? lease = positions.Select(Text(payload, "profileId"), Text(payload, "instanceId"),
                payload.GetProperty("selectionSequence").GetInt64(), current.Generation);
            Changed?.Invoke();
            return new { requestId, success = lease != null, revision = current.Revision, lease };
        }
        if (action == "activate")
        {
            if (!_compatibleAppSeen)
            {
                return new CommandResultMessage { RequestId = requestId, Success = false, Revision = current.Revision,
                    Error = "A compatible SyrenApp must connect before initial activation" };
            }
            if (current.Speakers.Count == 0)
            {
                return new CommandResultMessage { RequestId = requestId, Success = false, Revision = current.Revision,
                    Error = "Configure at least one speaker before activation" };
            }
            lock (_sync)
            {
                bool ready = current.Speakers.All(speaker => _receivers.TryGetValue(speaker.SpeakerId!, out var receiver) &&
                    clock.GetElapsedTime(receiver.Updated) < ReceiverTimeout &&
                    receiver.Status.GetProperty("ready").GetBoolean() &&
                    new[] { "sessions", "mixing", "snapcast", "rtp" }.All(capability =>
                        receiver.Status.GetProperty("capabilities").EnumerateArray().Any(value => value.GetString() == capability)));
                if (!ready)
                {
                    return new CommandResultMessage { RequestId = requestId, Success = false, Revision = current.Revision,
                        Error = "Every configured speaker must report a compatible ready receiver before activation" };
                }
            }
            bool activated = false;
            store.Update(state =>
            {
                if (state.Revision != current.Revision || state.Generation != current.Generation)
                {
                    return state;
                }
                activated = true;
                return state with { PlaybackActivated = true, Revision = checked(state.Revision + 1),
                    CatalogueRevision = checked(state.CatalogueRevision + 1) };
            });
            return new CommandResultMessage { RequestId = requestId, Success = activated, Revision = store.Current.Revision };
        }
        CommandResultMessage result;
        if (action == "group")
        {
            JsonObject document = ProfileGroupSchema.FromProfileNames(JsonNode.Parse(payload.GetRawText())!.AsObject());
            result = await configuration.UpsertGroupAsync(document.Deserialize<UpsertGroupCommand>()!, cancellationToken);
        }
        else
        {
            result = action switch
            {
                "deleteGroup" => await configuration.DeleteGroupAsync(payload.Deserialize<DeleteGroupCommand>()!, cancellationToken),
                "speaker" => await configuration.ConfigureSpeakerAsync(payload.Deserialize<ConfigureSpeakerCommand>()!, cancellationToken),
                "deleteSpeaker" => await configuration.DeleteSpeakerAsync(payload.Deserialize<DeleteSpeakerCommand>()!, cancellationToken),
                "level" => await configuration.SetSpeakerLevelAsync(payload.Deserialize<SetSpeakerLevelCommand>()!, cancellationToken),
                _ => new CommandResultMessage { RequestId = requestId, Success = false, Revision = current.Revision, Error = "Unknown command" },
            };
        }
        return result;
    }

    public bool CompleteSpotifyLink(JsonElement payload, out string? error)
    {
        lock (_sync)
        {
            string ticket = Text(payload, "ticket");
            if (!_links.TryGetValue(ticket, out var link) || link.Generation != Generation ||
                payload.GetProperty("generation").GetInt64() != Generation ||
                link.Expires <= clock.GetUtcNow() || Text(payload, "stateId") != store.Current.StateId)
            {
                error = "The linking request expired; start linking again";
                return false;
            }
            _links.Remove(ticket);
            try
            {
                profiles.LinkVerifiedAccount(link.Profile, Text(payload, "accountId"));
            }
            catch (InvalidOperationException exception)
            {
                error = exception.Message;
                return false;
            }
            error = null;
            return true;
        }
    }

    // Receivers resend their status with a new sequence, so only the fields that describe playback are published.
    private static JsonNode StableStatus(JsonElement status)
    {
        JsonObject stable = JsonNode.Parse(status.GetRawText())!.AsObject();
        stable.Remove("sequence");
        stable.Remove("instanceId");
        return stable;
    }

    private static string Text(JsonElement payload, string property) => payload.GetProperty(property).GetString()
        ?? throw new JsonException($"Missing {property}");
}
