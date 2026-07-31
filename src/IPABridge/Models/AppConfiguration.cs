namespace IPABridge.Models;

public sealed class AppConfiguration
{
    public string IpatoolPath { get; set; } = string.Empty;

    public string DeviceToolsDirectory { get; set; } = string.Empty;

    public string DownloadDirectory { get; set; } = string.Empty;

    public string AppleAccountEmail { get; set; } = string.Empty;

    public bool AutomaticallyRefreshDevices { get; set; } = true;
}
