namespace IPABridge.Models;

public sealed class IpatoolAccountSessionException : InvalidOperationException
{
    public IpatoolAccountSessionException(string message)
        : base(message)
    {
    }
}
