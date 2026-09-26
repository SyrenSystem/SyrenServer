using Syren.Server.Models.SnapCast;

namespace Syren.Server.Services;

/// <summary>
/// Interface for SnapCast service
/// </summary>
public interface ISnapCastService
{
    Task SetClientVolumeAsync(string id, int percent, CancellationToken cancellationToken = default);
    Task<SnapServerStatus> GetStatusAsync(CancellationToken cancellationToken = default);
    Task SetGroupClientsAsync(string id, IReadOnlyList<string> clientIds, CancellationToken cancellationToken = default);
    Task SetGroupNameAsync(string id, string name, CancellationToken cancellationToken = default);
    Task SetGroupStreamAsync(string id, string streamId, CancellationToken cancellationToken = default);
    Task SetGroupMuteAsync(string id, bool muted, CancellationToken cancellationToken = default);
    Task<string> AddStreamAsync(string streamUri, CancellationToken cancellationToken = default);
    Task RemoveStreamAsync(string streamId, CancellationToken cancellationToken = default);
    Task DeleteClientAsync(string id, CancellationToken cancellationToken = default);
}
