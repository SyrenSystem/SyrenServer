using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Syren.Server.Configuration;
using Syren.Server.Models;

namespace Syren.Server.Services;

public sealed class SystemStateStore : ISystemStateStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
    };

    private readonly string _filePath;
    private readonly SpeakersOptions _speakersOptions;
    private readonly string[] _sourceIds;
    private readonly bool _profileSessions;
    private readonly ILogger<SystemStateStore> _logger;
    private readonly object _syncRoot = new();

    public SystemStateStore(
        IOptions<StateOptions> options,
        IOptions<SpeakersOptions> speakersOptions,
        IOptions<PlaybackOptions> playbackOptions,
        ILogger<SystemStateStore> logger)
    {
        _filePath = options.Value.FilePath;
        _speakersOptions = speakersOptions.Value;
        _sourceIds = playbackOptions.Value.Sources.Select(source => source.Id).ToArray();
        _profileSessions = playbackOptions.Value.ProfileSessions;
        _logger = logger;
        Current = LoadOrCreate();
    }

    public PersistentSystemState Current { get; private set; }

    public event Action? Changed;

    public void Save(PersistentSystemState state)
    {
        lock (_syncRoot)
        {
            SaveLocked(state);
        }
        Changed?.Invoke();
    }

    public PersistentSystemState Update(Func<PersistentSystemState, PersistentSystemState> update)
    {
        PersistentSystemState state;
        lock (_syncRoot)
        {
            state = update(Current);
            if (ReferenceEquals(state, Current))
            {
                return state;
            }
            SaveLocked(state);
        }
        Changed?.Invoke();
        return state;
    }

    private void SaveLocked(PersistentSystemState state)
    {
        PersistentStateFactory.Validate(state);
        string? directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }
        string temporaryPath = $"{_filePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.WriteThrough
            ))
            {
                JsonNode document = JsonSerializer.SerializeToNode(state, SerializerOptions)!;
                if (state.Version == 3)
                {
                    ProfileGroupSchema.ToProfileNames(document["groups"]);
                }
                JsonSerializer.Serialize(stream, document, SerializerOptions);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, _filePath, overwrite: true);
            Current = state;
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private PersistentSystemState LoadOrCreate()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                PersistentSystemState created = PersistentStateFactory.CreateNew(_speakersOptions);
                if (_profileSessions)
                {
                    created = created with { Version = 3 };
                }
                SaveLocked(created);
                return created;
            }

            PersistentSystemState? loaded = ReadFile();
            if (loaded == null)
            {
                throw new InvalidDataException("The system state file is empty");
            }

            if (loaded.Version == 1)
            {
                PersistentSystemState migrated = PersistentStateFactory.MigrateVersionOne(
                    loaded,
                    _speakersOptions,
                    _sourceIds
                );
                migrated = PersistentStateFactory.DropUnknownSources(migrated, _sourceIds, WarnUnknownSource);
                if (_profileSessions)
                {
                    BackupMigration(loaded.Version);
                    migrated = MigrateProfiles(migrated);
                }
                SaveLocked(migrated);
                return migrated;
            }

            PersistentStateFactory.Validate(loaded);
            if (loaded.Version == 3 && !_profileSessions)
            {
                throw new InvalidDataException("Version 3 state requires profile session playback; refusing legacy audio control");
            }
            if (loaded.Version == 2 && _profileSessions)
            {
                BackupMigration(2);
                loaded = MigrateProfiles(loaded);
                SaveLocked(loaded);
            }
            PersistentSystemState cleaned = PersistentStateFactory.DropUnknownSources(
                loaded,
                _sourceIds,
                WarnUnknownSource
            );
            if (!ReferenceEquals(cleaned, loaded))
            {
                SaveLocked(cleaned);
            }
            return cleaned;
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException)
        {
            throw new InvalidDataException(
                $"Unable to load system state from {_filePath}",
                exception
            );
        }
    }

    private PersistentSystemState? ReadFile()
    {
        using FileStream stream = File.OpenRead(_filePath);
        JsonNode? document = JsonNode.Parse(stream);
        if (document?["version"]?.GetValue<int>() == 3)
        {
            ProfileGroupSchema.FromProfileNames(document["groups"]);
        }
        return document?.Deserialize<PersistentSystemState>(SerializerOptions);
    }

    private void BackupMigration(int version)
    {
        string backup = $"{_filePath}.v{version}.{DateTime.UtcNow:yyyyMMddTHHmmssfffffffZ}.backup";
        File.Copy(_filePath, backup, overwrite: false);
    }

    private static PersistentSystemState MigrateProfiles(PersistentSystemState state) => state with
    {
        Version = 3,
        Revision = state.Revision + 1,
        Groups = state.Groups.Select(group => group with
        {
            VolumeMode = "manual",
            SourcePriority = group.SourcePriority.Order(StringComparer.Ordinal).ToList(),
        }).ToList(),
    };

    private void WarnUnknownSource(string groupId, string sourceId) => _logger.LogWarning(
        "Dropping unknown audio source {SourceId} from playback group {GroupId}",
        sourceId,
        groupId
    );
}
