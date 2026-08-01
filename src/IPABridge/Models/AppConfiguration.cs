using System.Text.Json.Serialization;

namespace IPABridge.Models;

public sealed class AppConfiguration
{
    public const int CurrentSchemaVersion = 3;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public string IpatoolPath { get; set; } = string.Empty;

    public string DeviceToolsDirectory { get; set; } = string.Empty;

    public string DownloadDirectory { get; set; } = string.Empty;

    public List<AppleAccountProfile> AppleAccounts { get; set; } = [];

    public string SelectedAppleAccountId { get; set; } = string.Empty;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string? AppleAccountEmail { get; set; }

    public bool AutomaticallyRefreshDevices { get; set; } = true;
}
