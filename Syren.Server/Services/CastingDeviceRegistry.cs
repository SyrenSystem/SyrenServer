using System.Globalization;
using System.Text;

namespace Syren.Server.Services;

public sealed record CastingDeviceEntry(string Id, string Name, string Protocol, DateTimeOffset LastSeen, bool Connected);

// Recently seen casting devices live in memory only, so guest names never reach the disk.
public sealed class CastingDeviceRegistry(ISystemStateStore store, TimeProvider clock)
{
    public const int Capacity = 32;
    public const int MaxNameLength = 100;
    public const int MaxNameUnits = 400;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(15);

    private readonly object _sync = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    internal int Count
    {
        get
        {
            lock (_sync)
            {
                return _entries.Count;
            }
        }
    }

    public void Seen(string deviceId, string? name, string protocol)
    {
        string? cleaned = CleanName(name);
        if (cleaned == null)
        {
            return;
        }
        lock (_sync)
        {
            HashSet<string> open = OpenSenders();
            if (!_entries.ContainsKey(deviceId) && _entries.Count >= Capacity)
            {
                KeyValuePair<string, Entry>[] closed = _entries.Where(pair => !open.Contains(pair.Key)).ToArray();
                if (closed.Length == 0)
                {
                    return;
                }
                _entries.Remove(closed.MinBy(pair => pair.Value.SeenAt).Key);
            }
            _entries[deviceId] = new Entry(cleaned, protocol, clock.GetUtcNow(), clock.GetTimestamp(), open.Contains(deviceId));
        }
    }

    public CastingDeviceEntry? Find(string deviceId)
    {
        lock (_sync)
        {
            HashSet<string> open = OpenSenders();
            return _entries.TryGetValue(deviceId, out Entry? entry) && Recent(deviceId, entry, open)
                ? Snapshot(deviceId, entry, open) : null;
        }
    }

    public CastingDeviceEntry[] Devices()
    {
        lock (_sync)
        {
            HashSet<string> open = OpenSenders();
            // A connected device comes first, because its last seen time is when its session started.
            return _entries.Where(pair => Recent(pair.Key, pair.Value, open))
                .OrderByDescending(pair => open.Contains(pair.Key)).ThenByDescending(pair => pair.Value.LastSeen)
                .ThenBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => Snapshot(pair.Key, pair.Value, open)).ToArray();
        }
    }

    public void Forget(string deviceId)
    {
        lock (_sync)
        {
            _entries.Remove(deviceId);
        }
    }

    // A session that has just ended counts as the last time its device was seen.
    public void Prune()
    {
        lock (_sync)
        {
            HashSet<string> open = OpenSenders();
            foreach ((string deviceId, Entry entry) in _entries.ToArray())
            {
                bool hasSession = open.Contains(deviceId);
                if (hasSession && !entry.Open)
                {
                    _entries[deviceId] = entry with { Open = true };
                }
                else if (!hasSession && entry.Open)
                {
                    _entries[deviceId] = entry with { Open = false, LastSeen = clock.GetUtcNow(), SeenAt = clock.GetTimestamp() };
                }
                else if (!hasSession && clock.GetElapsedTime(entry.SeenAt) >= Window)
                {
                    _entries.Remove(deviceId);
                }
            }
        }
    }

    // Returns null when nothing printable is left, so a bad name only skips the registry.
    public static string? CleanName(string? name)
    {
        if (name == null)
        {
            return null;
        }
        try
        {
            string visible = new string(name.Normalize(NormalizationForm.FormC)
                .Where(character => !char.IsControl(character)).ToArray()).Trim();
            // A text element can hold any number of combining marks, so the length in code units is capped too.
            int end = 0;
            for (int elements = 0; elements < MaxNameLength && end < visible.Length; elements++)
            {
                int next = StringInfo.GetNextTextElementLength(visible, end);
                if (end + next > MaxNameUnits)
                {
                    break;
                }
                end += next;
            }
            visible = visible[..end].TrimEnd();
            return visible.Length == 0 ? null : visible;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private bool Recent(string deviceId, Entry entry, HashSet<string> open) =>
        open.Contains(deviceId) || entry.Open || clock.GetElapsedTime(entry.SeenAt) < Window;

    private HashSet<string> OpenSenders() => store.Current.Sessions
        .Where(session => session.Source == "casting" && session.State != "ended" && session.SenderId != null)
        .Select(session => session.SenderId!).ToHashSet(StringComparer.Ordinal);

    private static CastingDeviceEntry Snapshot(string deviceId, Entry entry, HashSet<string> open) =>
        new(deviceId, entry.Name, entry.Protocol, entry.LastSeen, open.Contains(deviceId));

    private sealed record Entry(string Name, string Protocol, DateTimeOffset LastSeen, long SeenAt, bool Open);
}
