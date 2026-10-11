using System.Globalization;
using System.Text.RegularExpressions;
using Syren.Server.Models;

namespace Syren.Server.Services;

public static class ProfilePlaybackValidation
{
    public static readonly string[] Sources = ["spotify", "laptop", "casting"];
    public const int MaxCastingDevices = 16;
    private static readonly Regex ProtocolPattern = new(@"^[a-z][a-z0-9-]{0,15}\z", RegexOptions.CultureInvariant);
    private static readonly Regex DeviceIdPattern = new(@"^[0-9a-f]{64}\z", RegexOptions.CultureInvariant);

    public static void Validate(PersistentSystemState state)
    {
        if (state.Version != 3)
        {
            return;
        }
        var profileIds = new HashSet<string>(StringComparer.Ordinal);
        var accounts = new HashSet<string>(StringComparer.Ordinal);
        foreach (ListenerProfile profile in state.Profiles)
        {
            if (string.IsNullOrWhiteSpace(profile.Id) || profile.Id.StartsWith("guest:", StringComparison.Ordinal) ||
                !profileIds.Add(profile.Id) || string.IsNullOrWhiteSpace(profile.Name) || profile.Name.Length > 100 ||
                profile.SourcePriority.Count != Sources.Length ||
                !profile.SourcePriority.ToHashSet(StringComparer.Ordinal).SetEquals(Sources) ||
                (profile.SpotifyAccountId != null &&
                    (string.IsNullOrWhiteSpace(profile.SpotifyAccountId) || !accounts.Add(profile.SpotifyAccountId))))
            {
                throw new InvalidDataException("Profile identity, account or source order is invalid");
            }
            var pairs = new HashSet<string>(StringComparer.Ordinal);
            foreach (SourceOverlap pair in profile.Overlap)
            {
                string key = string.Join(':', new[] { pair.First, pair.Second }.Order(StringComparer.Ordinal));
                if (!Sources.Contains(pair.First) || !Sources.Contains(pair.Second) || !pairs.Add(key))
                {
                    throw new InvalidDataException("Overlap rules must contain unique symmetric source pairs");
                }
            }
        }
        var castingDevices = new HashSet<string>(StringComparer.Ordinal);
        if (state.Profiles.Any(profile => profile.CastingDevices == null || profile.CastingDevices.Count > MaxCastingDevices ||
                profile.CastingDevices.Any(device => device.Id == null || !DeviceIdPattern.IsMatch(device.Id) ||
                    !castingDevices.Add(device.Id) || string.IsNullOrWhiteSpace(device.Name) ||
                    device.Name.Length > CastingDeviceRegistry.MaxNameUnits ||
                    new StringInfo(device.Name).LengthInTextElements > CastingDeviceRegistry.MaxNameLength)) ||
            (state.CastingKey != null && !ValidCastingKey(state.CastingKey)))
        {
            throw new InvalidDataException("Linked casting devices are invalid");
        }
        if (state.SourcePolicies.Count != Sources.Length || state.SourcePolicies.Any(pair =>
                !Sources.Contains(pair.Key) || pair.Value is not ("playing" or "connected") ||
                (pair.Key != "spotify" && pair.Value != "connected")))
        {
            throw new InvalidDataException("Unsupported source release policy");
        }
        if (state.Generation < 0 || state.ClaimSequence < 0 || state.CatalogueRevision < 0 ||
            state.Sessions.Select(session => session.Id).Distinct().Count() != state.Sessions.Count ||
            state.Sessions.Any(session => string.IsNullOrWhiteSpace(session.Id) ||
                string.IsNullOrWhiteSpace(session.ProducerId) || string.IsNullOrWhiteSpace(session.OwnerId) ||
                !Sources.Contains(session.Source) || session.ClaimSequence > state.ClaimSequence ||
                session.ClaimSequence < 0 || session.EventSequence <= 0 ||
                session.State is not ("connected" or "playing" or "paused" or "ended") ||
                (session.Claimed && (session.ClaimSequence == 0 || session.State == "ended")) ||
                (session.Source == "casting"
                    ? !ValidProtocol(session.Protocol) || !ValidSenderId(session.SenderId)
                    : session.Protocol != null || session.SenderId != null)))
        {
            throw new InvalidDataException("Session ledger is invalid");
        }
    }

    public static bool ValidProtocol(string? protocol) => protocol != null && ProtocolPattern.IsMatch(protocol);

    public static bool ValidSenderId(string? senderId) => senderId != null && DeviceIdPattern.IsMatch(senderId);

    private static bool ValidCastingKey(string key)
    {
        Span<byte> decoded = stackalloc byte[33];
        return Convert.TryFromBase64String(key, decoded, out int length) && length == 32;
    }
}
