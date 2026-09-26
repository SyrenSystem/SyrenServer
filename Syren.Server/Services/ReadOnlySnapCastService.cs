using Syren.Server.Models.SnapCast;

namespace Syren.Server.Services;

// Legacy services get this in profile mode, so they can read Snapcast but never change physical clients.
public sealed class ReadOnlySnapCastService(ISnapCastService inner) : ISnapCastService
{
    public Task<SnapServerStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
        inner.GetStatusAsync(cancellationToken);

    public Task SetClientVolumeAsync(string id, int percent, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task SetGroupClientsAsync(string id, IReadOnlyList<string> clientIds, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task SetGroupNameAsync(string id, string name, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task SetGroupStreamAsync(string id, string streamId, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task SetGroupMuteAsync(string id, bool muted, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task<string> AddStreamAsync(string streamUri, CancellationToken cancellationToken = default) =>
        Task.FromException<string>(new InvalidOperationException("Legacy streams are off in profile mode"));

    public Task RemoveStreamAsync(string streamId, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task DeleteClientAsync(string id, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
