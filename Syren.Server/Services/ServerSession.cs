namespace Syren.Server.Services;

public sealed class ServerSession
{
    public string Id { get; } = Guid.NewGuid().ToString();
}
