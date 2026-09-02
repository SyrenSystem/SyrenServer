using Syren.Server.Models;

namespace Syren.Server.Services;

public interface ISystemStateStore
{
    PersistentSystemState Current { get; }
    event Action? Changed;
    void Save(PersistentSystemState state);
    PersistentSystemState Update(Func<PersistentSystemState, PersistentSystemState> update);
}
