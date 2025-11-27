using Syren.Server.Models.SnapCast;

namespace Syren.Server.Services;

/// <summary>
/// Interface for SnapCast service
/// </summary>
public interface ISnapCastService
{
    Task<Status?> GetStatusAsync();
    Task SetVolumeAsync(int percent, bool muted = false);

    Task<double?> GetClientVolume(string id);
}
