namespace IPABridge.Models;

public sealed class ConnectedDevice
{
    public string Udid { get; init; } = string.Empty;

    public string Name { get; init; } = "Unnamed device";

    public string ProductType { get; init; } = "iOS device";

    public string ProductVersion { get; init; } = "Unknown version";

    public string ConnectionType { get; init; } = "USB";

    public bool IsPaired { get; init; }

    public string PairingLabel => IsPaired ? "Trusted" : "Trust required";

    public string ShortUdid => Udid.Length <= 12 ? Udid : $"{Udid[..6]}…{Udid[^6..]}";
}
