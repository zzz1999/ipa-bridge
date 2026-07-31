namespace IPABridge.Models;

public sealed record DeviceToolLocation(DeviceBackend Backend, string DirectoryPath)
{
    public static DeviceToolLocation Missing { get; } = new(DeviceBackend.None, string.Empty);

    public bool IsAvailable => Backend != DeviceBackend.None;
}
