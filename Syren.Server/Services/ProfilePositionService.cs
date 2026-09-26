using Syren.Server.Models;

namespace Syren.Server.Services;

public sealed class ProfilePositionService(ISystemStateStore store, TimeProvider clock)
{
    private const int RememberedSelections = 64;
    private readonly object _sync = new();
    private readonly Dictionary<string, PositionOwner> _owners = [];

    public string? Select(string profileId, string instanceId, long selectionSequence, long generation)
    {
        lock (_sync)
        {
            if (generation != store.Current.Generation || string.IsNullOrWhiteSpace(instanceId) ||
                !store.Current.Profiles.Any(profile => profile.Id == profileId) ||
                selectionSequence <= store.Current.SelectionSequences.GetValueOrDefault(instanceId))
            {
                return null;
            }
            string lease = Guid.NewGuid().ToString("N");
            store.Update(current =>
            {
                List<PositionReporterOwner> owners = current.PositionOwners
                    .Where(owner => owner.ProfileId != profileId && owner.InstanceId != instanceId)
                    .Append(new PositionReporterOwner { ProfileId = profileId, InstanceId = instanceId, Lease = lease }).ToList();
                return current with
                {
                    SelectionSequences = RecentSelections(current.SelectionSequences, owners, instanceId, selectionSequence),
                    PositionOwners = owners,
                    CatalogueRevision = checked(current.CatalogueRevision + 1),
                };
            });
            foreach (string previous in _owners.Where(pair => pair.Value.InstanceId == instanceId).Select(pair => pair.Key).ToArray())
            {
                _owners.Remove(previous);
            }
            _owners[profileId] = new PositionOwner(instanceId, lease);
            return lease;
        }
    }

    public bool Report(string profileId, string instanceId, string lease, long sequence,
        IReadOnlyDictionary<string, double> distances, long generation)
    {
        lock (_sync)
        {
            PositionReporterOwner? authority = store.Current.PositionOwners.Find(owner => owner.ProfileId == profileId);
            if (generation != store.Current.Generation || authority?.InstanceId != instanceId || authority.Lease != lease ||
                distances.Any(pair => !double.IsFinite(pair.Value) || pair.Value < 0))
            {
                return false;
            }
            if (!_owners.TryGetValue(profileId, out PositionOwner? owner) || owner.Lease != lease)
            {
                owner = new PositionOwner(instanceId, lease);
                _owners[profileId] = owner;
            }
            if (sequence <= owner.Sequence)
            {
                return false;
            }
            owner.Sequence = sequence;
            long now = clock.GetTimestamp();
            foreach ((string sensorId, double distance) in distances)
            {
                owner.Distances[sensorId] = (distance, now);
            }
            return true;
        }
    }

    public Dictionary<string, Dictionary<string, double>> DesiredGains()
    {
        lock (_sync)
        {
            PersistentSystemState current = store.Current;
            var result = new Dictionary<string, Dictionary<string, double>>();
            foreach (PersistentSpeakerState speaker in current.Speakers)
            {
                var gains = new Dictionary<string, double>();
                result[speaker.SpeakerId!] = gains;
                PersistentPlaybackGroup? group = current.Groups.Find(candidate => candidate.SpeakerIds.Contains(speaker.SpeakerId!));
                foreach (PlaybackSession session in current.Sessions.Where(session => session.State != "ended"))
                {
                    double gain = 0;
                    if (group != null && !group.Muted && group.SourcePriority.Contains(session.Source) &&
                        (session.Destination == "house" || session.Destination == group.Id))
                    {
                        gain = speaker.Volume / 100 * group.MasterVolume / 100 * group.SourceLevels.GetValueOrDefault(session.Source, 100) / 100;
                        ListenerProfile? profile = current.Profiles.Find(candidate => candidate.Id == session.OwnerId);
                        if (profile?.FollowMe == true)
                        {
                            gain *= PositionGain(profile.Id, speaker);
                        }
                    }
                    gains[session.Id] = gain;
                }
            }
            return result;
        }
    }

    private double PositionGain(string profileId, PersistentSpeakerState speaker)
    {
        if (speaker.SensorId == null || !_owners.TryGetValue(profileId, out PositionOwner? owner) ||
            !owner.Distances.TryGetValue(speaker.SensorId, out var reading) ||
            clock.GetElapsedTime(reading.Time) >= TimeSpan.FromSeconds(3))
        {
            return 0;
        }
        return Math.Clamp((speaker.MuteDistance - reading.Distance) /
            (speaker.MuteDistance - speaker.FullVolumeDistance), 0, 1);
    }

    // App instance IDs change on every launch, so only current owners and the newest selections are kept.
    private static Dictionary<string, long> RecentSelections(Dictionary<string, long> previous,
        List<PositionReporterOwner> owners, string instanceId, long selectionSequence)
    {
        var selections = new Dictionary<string, long>(previous) { [instanceId] = selectionSequence };
        HashSet<string> owning = owners.Select(owner => owner.InstanceId).ToHashSet(StringComparer.Ordinal);
        return selections.OrderByDescending(pair => owning.Contains(pair.Key)).ThenByDescending(pair => pair.Value)
            .Take(Math.Max(RememberedSelections, owning.Count)).ToDictionary(pair => pair.Key, pair => pair.Value);
    }

    private sealed class PositionOwner(string instanceId, string lease)
    {
        public string InstanceId { get; } = instanceId;
        public string Lease { get; } = lease;
        public long Sequence { get; set; }
        public Dictionary<string, (double Distance, long Time)> Distances { get; } = [];
    }
}
