using Syren.Server.Configuration;
using Syren.Server.Models;

namespace Syren.Server.Services;

internal static class PersistentStateFactory
{
    public const string DefaultGroupId = "default";

    public static PersistentSystemState CreateNew(SpeakersOptions options) => SeedConfiguredSpeakers(
        new PersistentSystemState { StateId = Guid.NewGuid().ToString() },
        options
    );

    // Adds missing configured speakers, returning the same instance when nothing was added.
    public static PersistentSystemState SeedConfiguredSpeakers(
        PersistentSystemState state,
        SpeakersOptions options)
    {
        var speakerIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sensorIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var snapClientIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (PersistentSpeakerState speaker in state.Speakers)
        {
            AddIdentifiers(speaker, speakerIds, sensorIds, snapClientIds);
        }

        var speakers = state.Speakers.ToList();
        foreach (SpeakerInfo configured in options.SpeakersInfo)
        {
            string sensorId = Identifiers.Normalize(configured.SensorId);
            string snapClientId = Identifiers.Normalize(configured.SnapClientId);
            if (speakerIds.Contains(sensorId) ||
                sensorIds.Contains(sensorId) ||
                snapClientIds.Contains(snapClientId))
            {
                continue;
            }
            var speaker = new PersistentSpeakerState
            {
                SpeakerId = sensorId,
                Name = configured.SensorId,
                SensorId = sensorId,
                SnapClientId = snapClientId,
                FullVolumeDistance = configured.FullVolumeDistance,
                MuteDistance = configured.MuteDistance,
                Connected = false,
                Volume = 100,
            };
            speakers.Add(speaker);
            AddIdentifiers(speaker, speakerIds, sensorIds, snapClientIds);
        }

        if (speakers.Count == state.Speakers.Count)
        {
            return state;
        }
        return state with
        {
            Revision = state.Revision + 1,
            Speakers = speakers,
        };
    }

    public static PersistentSystemState MigrateVersionOne(
        PersistentSystemState legacy,
        SpeakersOptions options,
        IReadOnlyList<string> sourceIds)
    {
        var configuredBySensor = options.SpeakersInfo.ToDictionary(
            speaker => speaker.SensorId,
            StringComparer.OrdinalIgnoreCase
        );
        var speakers = new List<PersistentSpeakerState>();
        foreach (PersistentSpeakerState legacySpeaker in legacy.Speakers)
        {
            if (legacySpeaker.SensorId == null ||
                !configuredBySensor.TryGetValue(legacySpeaker.SensorId, out SpeakerInfo configured))
            {
                continue;
            }
            string sensorId = Identifiers.Normalize(legacySpeaker.SensorId);
            speakers.Add(new PersistentSpeakerState
            {
                SpeakerId = sensorId,
                Name = legacySpeaker.SensorId,
                SensorId = sensorId,
                SnapClientId = Identifiers.Normalize(configured.SnapClientId),
                FullVolumeDistance = configured.FullVolumeDistance,
                MuteDistance = configured.MuteDistance,
                Connected = legacySpeaker.Connected,
                Volume = legacySpeaker.Volume,
                Position = legacySpeaker.Position,
            });
        }

        PersistentSystemState migrated = SeedConfiguredSpeakers(
            new PersistentSystemState
            {
                StateId = legacy.StateId,
                Speakers = speakers,
                RetiredSensorIds = legacy.RetiredSensorIds,
            },
            options
        );
        var groups = new List<PersistentPlaybackGroup>();
        if (migrated.Speakers.Count != 0 && sourceIds.Count != 0)
        {
            string volumeMode = migrated.Speakers.All(IsCalibrated) ? "automatic" : "manual";
            groups.Add(CreateDefaultGroup(
                migrated.Speakers.Select(speaker => speaker.SpeakerId!),
                sourceIds,
                volumeMode
            ));
        }
        return migrated with { Revision = 1, Groups = groups };
    }

    // Drops source ids that are no longer configured, returning the same instance when nothing changed.
    public static PersistentSystemState DropUnknownSources(
        PersistentSystemState state,
        IReadOnlyList<string> sourceIds,
        Action<string, string> warn)
    {
        var knownSources = sourceIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool changed = false;
        var groups = new List<PersistentPlaybackGroup>();
        foreach (PersistentPlaybackGroup group in state.Groups)
        {
            List<string> kept = group.SourcePriority
                .Where(sourceId => knownSources.Contains(sourceId))
                .ToList();
            if (kept.Count == group.SourcePriority.Count)
            {
                groups.Add(group);
                continue;
            }
            foreach (string sourceId in group.SourcePriority.Except(kept, StringComparer.OrdinalIgnoreCase))
            {
                warn(group.Id, sourceId);
            }
            changed = true;
            groups.Add(group with
            {
                SourcePriority = kept.Count == 0 ? sourceIds.ToList() : kept,
            });
        }

        if (!changed)
        {
            return state;
        }
        return state with
        {
            Revision = state.Revision + 1,
            Groups = groups,
        };
    }

    public static PersistentPlaybackGroup CreateDefaultGroup(
        IEnumerable<string> speakerIds,
        IEnumerable<string> sourceIds,
        string volumeMode) => new()
    {
        Id = DefaultGroupId,
        Name = "Default",
        SpeakerIds = speakerIds.ToList(),
        SourcePriority = sourceIds.ToList(),
        VolumeMode = volumeMode,
        MasterVolume = 100,
    };

    public static bool IsCalibrated(PersistentSpeakerState speaker) =>
        speaker.SensorId != null && speaker.Connected && speaker.Position.HasValue;

    public static void Validate(PersistentSystemState state)
    {
        if (state.Version != 2)
        {
            throw new InvalidDataException($"Unsupported system state version {state.Version}");
        }

        if (!Guid.TryParse(state.StateId, out _))
        {
            throw new InvalidDataException("System stateId must be a GUID");
        }

        var speakerIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var snapClientIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sensorIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (PersistentSpeakerState speaker in state.Speakers)
        {
            if (string.IsNullOrWhiteSpace(speaker.SpeakerId) ||
                !speakerIds.Add(speaker.SpeakerId) ||
                string.IsNullOrWhiteSpace(speaker.Name) ||
                string.IsNullOrWhiteSpace(speaker.SnapClientId) ||
                !snapClientIds.Add(speaker.SnapClientId) ||
                (speaker.SensorId != null &&
                    (string.IsNullOrWhiteSpace(speaker.SensorId) || !sensorIds.Add(speaker.SensorId))) ||
                !double.IsFinite(speaker.Volume) ||
                speaker.Volume is < 0 or > 100 ||
                !double.IsFinite(speaker.FullVolumeDistance) ||
                speaker.FullVolumeDistance < 0 ||
                !double.IsFinite(speaker.MuteDistance) ||
                speaker.MuteDistance <= speaker.FullVolumeDistance)
            {
                throw new InvalidDataException("System state contains an invalid speaker");
            }

            if (speaker.Connected && (!speaker.Position.HasValue || !IsFinite(speaker.Position.Value)))
            {
                throw new InvalidDataException("Connected speakers require a finite position");
            }
        }

        var groupIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var assignedSpeakerIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (PersistentPlaybackGroup group in state.Groups)
        {
            if (string.IsNullOrWhiteSpace(group.Id) ||
                !groupIds.Add(group.Id) ||
                string.IsNullOrWhiteSpace(group.Name) ||
                group.VolumeMode is not ("automatic" or "manual") ||
                !double.IsFinite(group.MasterVolume) ||
                group.MasterVolume is < 0 or > 100 ||
                group.SourcePriority.Count == 0 ||
                group.SourcePriority.Any(string.IsNullOrWhiteSpace) ||
                group.SourcePriority.Distinct(StringComparer.OrdinalIgnoreCase).Count() !=
                    group.SourcePriority.Count ||
                group.SpeakerIds.Any(speakerId =>
                    !speakerIds.Contains(speakerId) || !assignedSpeakerIds.Add(speakerId)))
            {
                throw new InvalidDataException("System state contains an invalid playback group");
            }
        }
    }

    private static void AddIdentifiers(
        PersistentSpeakerState speaker,
        HashSet<string> speakerIds,
        HashSet<string> sensorIds,
        HashSet<string> snapClientIds)
    {
        if (speaker.SpeakerId != null)
        {
            speakerIds.Add(speaker.SpeakerId);
        }
        if (speaker.SensorId != null)
        {
            sensorIds.Add(speaker.SensorId);
        }
        if (speaker.SnapClientId != null)
        {
            snapClientIds.Add(speaker.SnapClientId);
        }
    }

    private static bool IsFinite(PositionVector position) =>
        double.IsFinite(position.X) &&
        double.IsFinite(position.Y) &&
        double.IsFinite(position.Z);
}
