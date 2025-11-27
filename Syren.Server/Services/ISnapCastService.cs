using Syren.Server.Models.SnapCast;

namespace Syren.Server.Services;

/// <summary>
/// Interface for SnapCast service
/// </summary>
public interface ISnapCastService
{
    public Task<Status?> GetStatusAsync();
    public Task SetClientVolumeAsync(string id, int percent);

    public Task<double?> GetClientVolume(string id);
}
