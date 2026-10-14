using System.Text.Json;
using Microsoft.Extensions.Options;
using Syren.Server.Configuration;

namespace Syren.Server.Services;

// The volume each Spotify or casting producer last reported, kept apart from the state file so volume changes never bump a revision.
public sealed class ProducerVolumeStore
{
    public const string FileName = "producer-volumes.json";
    public static readonly TimeSpan SaveInterval = TimeSpan.FromSeconds(30);

    private readonly string? _filePath;
    private readonly TimeProvider _clock;
    private readonly ILogger<ProducerVolumeStore>? _logger;
    private readonly object _sync = new();
    // Keeps writes in order, so an older copy never replaces a newer one on disk.
    private readonly object _saveSync = new();
    private readonly Dictionary<string, double> _volumes = new(StringComparer.Ordinal);
    private bool _changed;
    private long? _savedAt;

    // Without a file path the volumes live in memory only.
    public ProducerVolumeStore(string? filePath, TimeProvider clock, ILogger<ProducerVolumeStore>? logger = null)
    {
        _filePath = filePath;
        _clock = clock;
        _logger = logger;
        Load();
    }

    [ActivatorUtilitiesConstructor]
    public ProducerVolumeStore(IOptions<StateOptions> options, TimeProvider clock, ILogger<ProducerVolumeStore> logger)
        : this(PathNextTo(options.Value.FilePath), clock, logger)
    {
    }

    public static string PathNextTo(string stateFilePath) =>
        Path.Combine(Path.GetDirectoryName(stateFilePath) ?? "", FileName);

    public double Volume(string producerId)
    {
        lock (_sync)
        {
            return _volumes.GetValueOrDefault(producerId, 1.0);
        }
    }

    // Returns true when the value is new, and ignores anything outside 0 to 1.
    public bool Set(string producerId, double volume)
    {
        if (!Valid(volume))
        {
            return false;
        }
        lock (_sync)
        {
            if (_volumes.GetValueOrDefault(producerId, 1.0) == volume)
            {
                return false;
            }
            // Full volume is the default, so it needs no entry.
            if (volume == 1.0)
            {
                _volumes.Remove(producerId);
            }
            else
            {
                _volumes[producerId] = volume;
            }
            _changed = true;
            return true;
        }
    }

    // Writes at most once per interval, and only after a change.
    public void SaveIfDue() => Save(due: true);

    // Writes any change right away, for shutdown.
    public void Flush() => Save(due: false);

    public static bool Valid(double volume) => double.IsFinite(volume) && volume is >= 0 and <= 1;

    // The disk write runs outside _sync, so gains and lifecycle events never wait for it.
    private void Save(bool due)
    {
        lock (_saveSync)
        {
            SortedDictionary<string, double> snapshot;
            lock (_sync)
            {
                if (!_changed || (due && _savedAt is { } savedAt && _clock.GetElapsedTime(savedAt) < SaveInterval))
                {
                    return;
                }
                snapshot = new SortedDictionary<string, double>(_volumes, StringComparer.Ordinal);
                // A change made during the write sets this again and is saved next time.
                _changed = false;
                _savedAt = _clock.GetTimestamp();
            }
            if (_filePath == null)
            {
                return;
            }
            try
            {
                AtomicFile.Write(_filePath, stream => JsonSerializer.Serialize(stream, snapshot));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A failed write is tried again after the next interval.
                lock (_sync)
                {
                    _changed = true;
                }
                _logger?.LogWarning("Unable to save producer volumes: {Type}", exception.GetType().Name);
            }
        }
    }

    private void Load()
    {
        if (_filePath == null || !File.Exists(_filePath))
        {
            return;
        }
        try
        {
            using FileStream stream = File.OpenRead(_filePath);
            Dictionary<string, JsonElement>? saved = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(stream);
            foreach ((string producerId, JsonElement value) in saved ?? [])
            {
                if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double volume) &&
                    Valid(volume) && volume != 1.0)
                {
                    _volumes[producerId] = volume;
                }
            }
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            // Volumes are only a convenience, so a bad file starts everyone at full volume.
            _logger?.LogWarning("Ignoring unreadable producer volumes: {Type}", exception.GetType().Name);
        }
    }
}
