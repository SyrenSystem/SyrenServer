namespace Syren.Server.Services;

/// <summary>
/// Interface for SnapCast service
/// </summary>
public interface ISnapCastService
{
    Task SetClientVolumeAsync(string id, int percent, CancellationToken cancellationToken = default);
}
