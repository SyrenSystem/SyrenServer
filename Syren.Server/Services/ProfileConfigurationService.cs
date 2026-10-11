using Syren.Server.Models;

namespace Syren.Server.Services;

public sealed class ProfileConfigurationService(ISystemStateStore store, CastingDeviceRegistry registry)
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
                    // Links are changed only by their own commands, so a save from any app version keeps them.
                    ListenerProfile configured = command.Profile with
                    {
                        SpotifyAccountId = existing?.SpotifyAccountId, CastingDevices = existing?.CastingDevices ?? [],
                    };
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
                case "linkCasting" when command.ProfileId != null && command.DeviceId != null:
                    ListenerProfile? target = current.Profiles.Find(profile => profile.Id == command.ProfileId);
                    if (target == null || registry.Find(command.DeviceId) is not { } device)
                    {
                        result = Result(command, false, "This device has not played here in the last 15 minutes");
                        return current;
                    }
                    if (current.Profiles.Any(profile => profile.Id != target.Id &&
                        profile.CastingDevices.Any(linked => linked.Id == device.Id)))
                    {
                        result = Result(command, false, "This device is already linked to another profile");
                        return current;
                    }
                    if (target.CastingDevices.Any(linked => linked.Id == device.Id))
                    {
                        result = Unchanged(command, current);
                        return current;
                    }
                    // The session keeps its ID, claim and transports, like a linked Spotify guest.
                    var linkedDevice = new LinkedCastingDevice { Id = device.Id, Name = device.Name };
                    changed = current with
                    {
                        Profiles = current.Profiles.Select(profile => profile.Id == target.Id
                            ? profile with { CastingDevices = [.. profile.CastingDevices, linkedDevice] } : profile).ToList(),
                        Sessions = current.Sessions.Select(session => session.Source == "casting" && session.State != "ended" &&
                            session.SenderId == linkedDevice.Id ? session with { OwnerId = target.Id } : session).ToList(),
                    };
                    break;
                case "unlinkCasting" when command.ProfileId != null && command.DeviceId != null:
                    if (current.Profiles.Find(profile => profile.Id == command.ProfileId) is not { } owner)
                    {
                        return current;
                    }
                    if (!owner.CastingDevices.Any(linked => linked.Id == command.DeviceId))
                    {
                        result = Unchanged(command, current);
                        return current;
                    }
                    // The sender is still connected, so its sessions go back to the guest instead of ending.
                    changed = current with
                    {
                        Profiles = current.Profiles.Select(profile => profile.Id == command.ProfileId
                            ? profile with { CastingDevices = profile.CastingDevices.Where(linked => linked.Id != command.DeviceId).ToList() }
                            : profile).ToList(),
                        Sessions = current.Sessions.Select(session => session.Source == "casting" && session.State != "ended" &&
                            session.SenderId == command.DeviceId && session.OwnerId == command.ProfileId
                                ? session with { OwnerId = "guest:" + command.DeviceId } : session).ToList(),
                    };
                    break;
                case "forgetCasting" when command.DeviceId != null:
                    if (current.Profiles.Any(profile => profile.CastingDevices.Any(linked => linked.Id == command.DeviceId)))
                    {
                        result = Result(command, false, "Unlink this device first");
                        return current;
                    }
                    // The registry lives in memory only, so forgetting saves nothing.
                    result = Unchanged(command, current);
                    return current;
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
        if (result.Success && command.Action == "forgetCasting")
        {
            registry.Forget(command.DeviceId!);
        }
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

    // A command that changes nothing keeps the revision, so it never makes another app's command stale.
    private static CommandResultMessage Unchanged(ProfileConfigurationCommand command, PersistentSystemState current) => new()
    {
        RequestId = command.RequestId,
        Success = true,
        Revision = current.Revision,
    };

    private CommandResultMessage Result(ProfileConfigurationCommand command, bool success, string error) => new()
    {
        RequestId = command.RequestId,
        Success = success,
        Revision = store.Current.Revision,
        Error = error,
    };
}
