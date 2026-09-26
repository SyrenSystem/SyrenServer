using Syren.Server.Models;

namespace Syren.Server.Services;

public sealed class ProfileConfigurationService(ISystemStateStore store)
{
    public CommandResultMessage Configure(ProfileConfigurationCommand command)
    {
        CommandResultMessage result = Result(command, false, "Configuration changed or server generation is stale");
        store.Update(current =>
        {
            if (current.Version != 3 || command.ProtocolVersion != 3 || command.StateId != current.StateId ||
                command.Generation != current.Generation || command.ExpectedRevision != current.Revision ||
                string.IsNullOrWhiteSpace(command.RequestId))
            {
                return current;
            }
            PersistentSystemState changed;
            switch (command.Action)
            {
                case "profile" when command.Profile != null:
                    ListenerProfile? existing = current.Profiles.Find(profile => profile.Id == command.Profile.Id);
                    ListenerProfile configured = command.Profile with { SpotifyAccountId = existing?.SpotifyAccountId };
                    changed = current with
                    {
                        Profiles = current.Profiles.Where(profile => profile.Id != configured.Id).Append(configured).ToList(),
                    };
                    break;
                case "policy" when command.Source != null && command.Policy != null:
                    var policies = new Dictionary<string, string>(current.SourcePolicies) { [command.Source] = command.Policy };
                    changed = current with
                    {
                        SourcePolicies = policies,
                        Sessions = current.Sessions.Select(session => session.Source == command.Source &&
                            session.State == "paused" && command.Policy == "playing"
                                ? session with { Claimed = false } : session).ToList(),
                    };
                    break;
                case "unlink" when command.ProfileId != null:
                    if (!current.Profiles.Any(profile => profile.Id == command.ProfileId))
                    {
                        return current;
                    }
                    changed = current with
                    {
                        Profiles = current.Profiles.Select(profile => profile.Id == command.ProfileId
                            ? profile with { SpotifyAccountId = null } : profile).ToList(),
                        Sessions = current.Sessions.Select(session => session.OwnerId == command.ProfileId &&
                            session.Source == "spotify" && session.State != "ended" ? session.End() : session).ToList(),
                    };
                    break;
                default:
                    result = Result(command, false, "Unknown profile command");
                    return current;
            }
            changed = changed with { Revision = checked(current.Revision + 1), CatalogueRevision = checked(current.CatalogueRevision + 1) };
            try
            {
                ProfilePlaybackValidation.Validate(changed);
            }
            catch (InvalidDataException exception)
            {
                result = Result(command, false, exception.Message);
                return current;
            }
            result = new CommandResultMessage { RequestId = command.RequestId, Revision = changed.Revision, Success = true };
            return changed;
        });
        return result;
    }

    public void LinkVerifiedAccount(string profileId, string accountId)
    {
        store.Update(current =>
        {
            if (!current.Profiles.Any(profile => profile.Id == profileId) || string.IsNullOrWhiteSpace(accountId) ||
                current.Profiles.Any(profile => profile.Id == profileId && profile.SpotifyAccountId != null && profile.SpotifyAccountId != accountId))
            {
                throw new InvalidOperationException("Account linking requires an existing unlinked profile");
            }
            if (current.Profiles.Any(profile => profile.Id != profileId && profile.SpotifyAccountId == accountId))
            {
                throw new InvalidOperationException("This Spotify account is already linked to another profile");
            }
            return current with
            {
                Revision = checked(current.Revision + 1),
                CatalogueRevision = checked(current.CatalogueRevision + 1),
                Profiles = current.Profiles.Select(profile => profile.Id == profileId
                    ? profile with { SpotifyAccountId = accountId } : profile).ToList(),
                Sessions = current.Sessions.Select(session => session.Source == "spotify" && session.AccountId == accountId
                    ? session with { OwnerId = profileId } : session).ToList(),
            };
        });
    }

    private CommandResultMessage Result(ProfileConfigurationCommand command, bool success, string error) => new()
    {
        RequestId = command.RequestId,
        Success = success,
        Revision = store.Current.Revision,
        Error = error,
    };
}
