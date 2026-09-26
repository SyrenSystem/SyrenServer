using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Syren.Server.Models;

namespace Syren.Server.Services;

public enum LifecycleOutcome
{
    Accepted,
    Ignored,
    UnknownSession,
}

public sealed class SessionCatalogueService(ISystemStateStore store, TimeProvider clock)
{
    public static readonly TimeSpan InterruptionGrace = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan EndedRetention = TimeSpan.FromHours(1);

    // Liveness stays in memory so heartbeats never write the state file.
    // Monotonic timestamps keep liveness steady when the wall clock steps.
    private readonly ConcurrentDictionary<string, long> _heartbeats = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _endedSince = new(StringComparer.Ordinal);

    public long Generation => store.Current.Generation;

    public void BeginGeneration()
    {
        DateTimeOffset now = clock.GetUtcNow();
        RefreshHeartbeats();
        store.Update(current => current with
        {
            Generation = checked(current.Generation + 1),
            CatalogueRevision = checked(current.CatalogueRevision + 1),
            Sessions = current.Sessions.Select(session => session.State == "ended" ? session : Evaluate(session with
            {
                InterruptedAt = session.InterruptedAt ?? now,
                Transports = session.Transports.Select(transport => transport with { Available = false }).ToList(),
            }, now)).ToList(),
        });
    }

    // Producers could not reach the server while it was offline, so every open session gets a fresh grace period.
    public void RefreshHeartbeats()
    {
        long now = clock.GetTimestamp();
        foreach (PlaybackSession session in store.Current.Sessions.Where(session => session.State != "ended"))
        {
            _heartbeats[session.Id] = now;
        }
    }

    public SessionCatalogue Snapshot()
    {
        PersistentSystemState current = store.Current;
        return new SessionCatalogue
        {
            StateId = current.StateId,
            Generation = current.Generation,
            Revision = current.CatalogueRevision,
            ConfigurationRevision = current.Revision,
            Profiles = current.Profiles.ToArray(),
            // The account stays on the server so guest usernames never reach the retained topic.
            Sessions = current.Sessions.Select(session => session with { AccountId = null }).ToArray(),
            SourcePolicies = new(current.SourcePolicies),
        };
    }

    public LifecycleOutcome Apply(SessionLifecycleEvent message) => Apply(message, null, "spotify");

    // Only the server creates PC sessions, after it has checked the profile and made the stream.
    public bool StartPc(SessionLifecycleEvent message, long expectedRevision) =>
        Apply(message, expectedRevision, "laptop") == LifecycleOutcome.Accepted;

    private LifecycleOutcome Apply(SessionLifecycleEvent message, long? expectedRevision, string createdSource)
    {
        LifecycleOutcome outcome = LifecycleOutcome.Ignored;
        store.Update(current =>
        {
            outcome = LifecycleOutcome.Ignored;
            if (current.Version != 3 || (expectedRevision != null && current.Revision != expectedRevision) ||
                message.Generation != current.Generation || message.EventSequence <= 0 ||
                string.IsNullOrWhiteSpace(message.SessionId) || string.IsNullOrWhiteSpace(message.ProducerId))
            {
                return current;
            }
            PlaybackSession? session = current.Sessions.Find(candidate => candidate.Id == message.SessionId);
            if (session != null && (session.ProducerId != message.ProducerId || session.State == "ended" ||
                !SequenceAccepted(session, message)))
            {
                return current;
            }
            DateTimeOffset now = clock.GetUtcNow();
            if (session == null)
            {
                outcome = LifecycleOutcome.UnknownSession;
                if (message.Action != "start" || message.Source != createdSource ||
                    !ValidDestination(current, message.Destination, message.Source))
                {
                    return current;
                }
                string? owner = message.Source == "spotify"
                    ? OwnerForAccount(current, message.AccountId)
                    : current.Profiles.Find(profile => profile.Id == message.OwnerId)?.Id;
                if (owner == null)
                {
                    return current;
                }
                session = new PlaybackSession
                {
                    Id = message.SessionId,
                    ProducerId = message.ProducerId,
                    OwnerId = owner,
                    AccountId = message.AccountId,
                    Source = message.Source,
                    Destination = message.Destination!,
                };
            }
            if (message.Transports != null && !ValidTransports(message.Transports, session.Source))
            {
                return current;
            }
            PlaybackSession updated = session with { EventSequence = Math.Max(session.EventSequence, message.EventSequence) };
            if (session.Source != "spotify" && message.Action is "play" or "pause")
            {
                return current;
            }
            bool claim = false;
            switch (message.Action)
            {
                case "start":
                    if (session.EventSequence != 0)
                    {
                        return current;
                    }
                    claim = session.Source == "laptop";
                    updated = updated with { State = claim ? "playing" : "connected" };
                    break;
                case "play":
                    claim = !session.Claimed;
                    updated = updated with { State = "playing", InterruptedAt = null };
                    break;
                case "pause":
                    updated = updated with
                    {
                        State = "paused",
                        Claimed = current.SourcePolicies[session.Source] == "connected" && session.Claimed,
                        InterruptedAt = null,
                    };
                    break;
                case "end":
                    updated = updated.End();
                    break;
                case "interrupt":
                    updated = updated with { InterruptedAt = session.InterruptedAt ?? now };
                    break;
                case "transport":
                case "reconcile":
                    updated = updated with { InterruptedAt = null };
                    break;
                case "heartbeat":
                    // A PC heartbeat carries its transports so it can replace a lost reconcile.
                    if (session.Source == "laptop" && message.Transports?.Any(transport => transport.Available) == true)
                    {
                        updated = updated with { InterruptedAt = null };
                    }
                    break;
                default:
                    return current;
            }
            if (message.Transports != null && updated.State != "ended" &&
                (message.Action != "heartbeat" || session.Source == "laptop"))
            {
                updated = updated with { Transports = message.Transports.ToList() };
            }
            if (updated.State != "ended" && !updated.Transports.Any(transport => transport.Available))
            {
                updated = updated with { InterruptedAt = session.InterruptedAt ?? now };
            }
            long nextClaim = claim ? checked(current.ClaimSequence + 1) : current.ClaimSequence;
            if (claim)
            {
                updated = updated with { ClaimSequence = nextClaim, Claimed = true };
            }
            _heartbeats[updated.Id] = clock.GetTimestamp();
            updated = Evaluate(updated, now);
            outcome = LifecycleOutcome.Accepted;
            if (Same(updated, session))
            {
                return current;
            }
            return current with
            {
                ClaimSequence = nextClaim,
                CatalogueRevision = checked(current.CatalogueRevision + 1),
                Sessions = current.Sessions.Where(candidate => candidate.Id != updated.Id).Append(updated).ToList(),
            };
        });
        return outcome;
    }

    // Ends silent PC owners, updates eligibility as grace periods pass, and drops old ended sessions.
    public void Expire(bool expireOwners)
    {
        DateTimeOffset now = clock.GetUtcNow();
        store.Update(current =>
        {
            bool changed = false;
            var sessions = new List<PlaybackSession>(current.Sessions.Count);
            foreach (PlaybackSession session in current.Sessions)
            {
                if (session.State == "ended")
                {
                    if (clock.GetElapsedTime(_endedSince.GetOrAdd(session.Id, _ => clock.GetTimestamp())) >= EndedRetention)
                    {
                        changed = true;
                    }
                    else
                    {
                        sessions.Add(session);
                    }
                    continue;
                }
                PlaybackSession updated = expireOwners && session.Source == "laptop" && !Alive(session.Id)
                    ? session.End()
                    : Evaluate(session, now);
                changed |= !Same(updated, session);
                sessions.Add(updated);
            }
            return changed ? current with
            {
                CatalogueRevision = checked(current.CatalogueRevision + 1),
                Sessions = sessions,
            } : current;
        });
        HashSet<string> open = store.Current.Sessions.Where(session => session.State != "ended")
            .Select(session => session.Id).ToHashSet(StringComparer.Ordinal);
        HashSet<string> known = store.Current.Sessions.Select(session => session.Id).ToHashSet(StringComparer.Ordinal);
        foreach (string identity in _heartbeats.Keys.Where(identity => !open.Contains(identity)))
        {
            _heartbeats.TryRemove(identity, out _);
        }
        foreach (string identity in _endedSince.Keys.Where(identity => !known.Contains(identity)))
        {
            _endedSince.TryRemove(identity, out _);
        }
    }

    public static bool AudioEligible(PlaybackSession session, DateTimeOffset now, bool alive) =>
        session.Claimed && session.State != "ended" &&
        (session.Source == "laptop" || session.State == "playing") &&
        (alive || session.Source is not ("laptop" or "spotify")) &&
        (session.InterruptedAt is { } interrupted
            ? now - interrupted < InterruptionGrace
            : session.Transports.Any(transport => transport.Available));

    public static string? OwnerForAccount(PersistentSystemState current, string? account)
    {
        if (string.IsNullOrWhiteSpace(account))
        {
            return null;
        }
        return current.Profiles.Find(profile => profile.SpotifyAccountId == account)?.Id ??
            "guest:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(account)));
    }

    // Spotify events must be contiguous; a heartbeat repeats the latest sequence instead of taking a new one.
    private static bool SequenceAccepted(PlaybackSession session, SessionLifecycleEvent message)
    {
        if (message.Action == "heartbeat")
        {
            return session.Source == "spotify"
                ? message.EventSequence == session.EventSequence
                : message.EventSequence >= session.EventSequence;
        }
        return session.Source == "spotify"
            ? message.EventSequence == session.EventSequence + 1
            : message.EventSequence > session.EventSequence;
    }

    private bool Alive(string sessionId) =>
        _heartbeats.TryGetValue(sessionId, out long heartbeat) && clock.GetElapsedTime(heartbeat) < InterruptionGrace;

    private PlaybackSession Evaluate(PlaybackSession session, DateTimeOffset now) =>
        session with { Eligible = AudioEligible(session, now, Alive(session.Id)) };

    private static bool Same(PlaybackSession first, PlaybackSession second) =>
        first.Transports.SequenceEqual(second.Transports) && first with { Transports = second.Transports } == second;

    private static bool ValidDestination(PersistentSystemState current, string? destination, string source) =>
        destination == "house" || current.Groups.Any(group => group.Id == destination && group.SourcePriority.Contains(source));

    private static bool ValidTransports(List<SessionTransport> transports, string source) =>
        transports.Select(transport => transport.Id).Distinct().Count() == transports.Count &&
        transports.All(transport => !string.IsNullOrWhiteSpace(transport.Id) &&
            !string.IsNullOrWhiteSpace(transport.Endpoint) &&
            (transport.Kind == "snapcast" || (transport.Kind == "rtp" && source == "laptop" &&
                !string.IsNullOrWhiteSpace(transport.SpeakerId))));
}
