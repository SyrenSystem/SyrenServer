using System.Numerics;
using Syren.Server.Models;
using Syren.Server.Models.SnapCast;

namespace Syren.Server.Services;

public interface IDistanceService
{
    Task UpdateDistanceAsync(DistanceData distance, CancellationToken cancellationToken = default);
    Task SetSpeakerVolumeAsync(string sensorId, double volume, CancellationToken cancellationToken = default);

    Task<SpeakerState?> ConnectSpeakerAsync(string sensorId, double? volume, CancellationToken cancellationToken = default);
    Task<DisconnectResult> DisconnectSpeakerAsync(string sensorId, CancellationToken cancellationToken = default);

    Vector3? GetUserPosition();
    Task<IReadOnlyList<SpeakerPosition>> GetConnectedSpeakerPositionsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> GetConfiguredSpeakerIdsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> GetRetiredSpeakerIdsAsync(CancellationToken cancellationToken = default);
    Task ConfirmRetiredSpeakerIdsClearedAsync(IEnumerable<string> sensorIds, CancellationToken cancellationToken = default);
    PersistentSystemState ApplyConfigurationChange(Func<PersistentSystemState, PersistentSystemState> mutation);
    // Pass the client volumes Snapserver reports so unknown clients are skipped and drift is corrected.
    Task ApplyCurrentVolumesAsync(
        IReadOnlyDictionary<string, SnapClientVolumeStatus?>? reportedVolumes = null,
        CancellationToken cancellationToken = default);
    string StateId { get; }
}
