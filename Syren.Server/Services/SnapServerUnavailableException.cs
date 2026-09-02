namespace Syren.Server.Services;

public sealed class SnapServerUnavailableException : Exception
{
    public SnapServerUnavailableException(string message)
        : base(message)
    {
    }

    public SnapServerUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
