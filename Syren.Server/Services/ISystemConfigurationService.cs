using Syren.Server.Models;

namespace Syren.Server.Services;

public interface ISystemConfigurationService
{
    SystemConfigurationSnapshot GetConfiguration();
    SystemRuntimeSnapshot CurrentRuntime { get; }
    event Action? RuntimeChanged;
    Task<CommandResultMessage> ConfigureSpeakerAsync(ConfigureSpeakerCommand command, CancellationToken cancellationToken = default);
    Task<CommandResultMessage> DeleteSpeakerAsync(DeleteSpeakerCommand command, CancellationToken cancellationToken = default);
    Task<CommandResultMessage> UpsertGroupAsync(UpsertGroupCommand command, CancellationToken cancellationToken = default);
    Task<CommandResultMessage> DeleteGroupAsync(DeleteGroupCommand command, CancellationToken cancellationToken = default);
    Task<CommandResultMessage> SetSpeakerLevelAsync(SetSpeakerLevelCommand command, CancellationToken cancellationToken = default);
    Task ReconcileAsync(CancellationToken cancellationToken = default);
}
