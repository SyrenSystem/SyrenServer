using Syren.Server.Models;

namespace Syren.Server.Services;

public static class ProfilePlaybackValidation
{
    public static readonly string[] Sources = ["spotify", "laptop", "casting"];

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
                (session.Claimed && (session.ClaimSequence == 0 || session.State == "ended"))))
        {
            throw new InvalidDataException("Session ledger is invalid");
        }
    }
}
