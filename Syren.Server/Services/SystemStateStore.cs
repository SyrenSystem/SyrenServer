using System.Text.Json;
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

    public SystemStateStore(IOptions<StateOptions> options)
    {
        _filePath = options.Value.FilePath;
        Current = LoadOrCreate();
    }

    public PersistentSystemState Current { get; private set; }

    public void Save(PersistentSystemState state)
    {
        Validate(state);
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
                JsonSerializer.Serialize(stream, state, SerializerOptions);
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
        if (!File.Exists(_filePath))
        {
            var state = new PersistentSystemState
            {
                StateId = Guid.NewGuid().ToString(),
            };
            Save(state);
            return state;
        }

        try
        {
            using FileStream stream = File.OpenRead(_filePath);
            PersistentSystemState? state = JsonSerializer.Deserialize<PersistentSystemState>(
                stream,
                SerializerOptions
            );
            if (state == null)
            {
                throw new InvalidDataException("The system state file is empty");
            }

            Validate(state);
            return state;
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException)
        {
            throw new InvalidDataException(
                $"Unable to load system state from {_filePath}",
                exception
            );
        }
    }

    private static void Validate(PersistentSystemState state)
    {
        if (state.Version != 1)
        {
            throw new InvalidDataException($"Unsupported system state version {state.Version}");
        }

        if (!Guid.TryParse(state.StateId, out _))
        {
            throw new InvalidDataException("System stateId must be a GUID");
        }

        foreach (PersistentSpeakerState speaker in state.Speakers)
        {
            if (string.IsNullOrWhiteSpace(speaker.SensorId) ||
                !double.IsFinite(speaker.Volume) ||
                speaker.Volume is < 0 or > 100)
            {
                throw new InvalidDataException("System state contains an invalid speaker");
            }

            if (speaker.Connected && (!speaker.Position.HasValue || !IsFinite(speaker.Position.Value)))
            {
                throw new InvalidDataException("Connected speakers require a finite position");
            }
        }
    }

    private static bool IsFinite(PositionVector position) =>
        double.IsFinite(position.X) &&
        double.IsFinite(position.Y) &&
        double.IsFinite(position.Z);
}
