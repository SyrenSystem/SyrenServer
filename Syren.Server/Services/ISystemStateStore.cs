using Syren.Server.Models;

namespace Syren.Server.Services;

public interface ISystemStateStore
{
    PersistentSystemState Current { get; }
    void Save(PersistentSystemState state);
}
