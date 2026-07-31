namespace IPABridge.Models;

public sealed record AppleDeviceSupportStatus
{
    public bool HasBeenChecked { get; init; }

    public bool IsAppleDevicesInstalled { get; init; }

    public string? AppleDevicesVersion { get; init; }

    public bool? IsUsbDriverInstalled { get; init; }

    public string? UsbDriverVersion { get; init; }

    public string? UsbDriverPackageName { get; init; }

    public bool IsTransportEndpointReachable { get; init; }

    public bool IsTransportServiceRegistered { get; init; }

    public bool? IsBackendProbeSuccessful { get; init; }

    public string? BackendName { get; init; }

    public string? BackendProbeError { get; init; }

    public bool IsReady => HasBeenChecked && IsBackendProbeSuccessful == true;

    public bool HasCompleteUsbSupport =>
        IsReady && IsUsbDriverInstalled == true;

    public string OverallLabel =>
        (HasBeenChecked, IsBackendProbeSuccessful, IsUsbDriverInstalled, IsAppleDevicesInstalled) switch
        {
            (false, _, _, _) => "Checking",
            (true, true, true, _) => "Ready",
            (true, true, false, _) => "Device transport verified — USB driver not detected",
            (true, true, null, _) => "Device transport verified — Driver status unknown",
            (true, null, _, _) => "Device transport not checked — Install iOS device tools",
            (true, false, true, _) => "Driver installed — Device transport probe failed",
            (true, false, _, true) => "Apple Devices installed — Device transport probe failed",
            _ => "Apple iOS device support not ready"
        };

    public string ApplicationLabel => !HasBeenChecked
        ? "Checking"
        : IsAppleDevicesInstalled
        ? string.IsNullOrWhiteSpace(AppleDevicesVersion)
            ? "Installed"
            : $"Installed — {AppleDevicesVersion}"
        : "Not installed";

    public string UsbDriverLabel => (HasBeenChecked, IsUsbDriverInstalled) switch
    {
        (false, _) => "Checking",
        (true, true) => string.IsNullOrWhiteSpace(UsbDriverVersion)
            ? "Installed"
            : $"Installed — {UsbDriverVersion}",
        (true, null) => "Driver inventory unavailable",
        _ => "Not detected"
    };

    public string BackendTransportLabel => !HasBeenChecked
        ? "Checking"
        : IsBackendProbeSuccessful == true
            ? string.IsNullOrWhiteSpace(BackendName)
                ? "Verified"
                : $"Verified — {BackendName}"
            : IsBackendProbeSuccessful == false
                ? string.IsNullOrWhiteSpace(BackendName)
                    ? "Probe failed"
                    : $"Unavailable — {BackendName}"
                : "Install device tools to verify";

    public string TransportEndpointLabel => !HasBeenChecked
        ? "Checking"
        : IsTransportEndpointReachable
            ? "Endpoint reachable — Diagnostic only"
            : IsTransportServiceRegistered
                ? "Service registered — Endpoint unavailable"
                : "Not detected";

    public static AppleDeviceSupportStatus Checking { get; } = new();
}

public sealed record AppleDevicesInstallationResult(
    bool AutomaticInstallationVerified,
    bool OpenedMicrosoftStore,
    string Message,
    AppleDeviceSupportStatus Status);

public sealed record AppleUsbDriverDetection(
    bool? IsInstalled,
    string? Version,
    string? PackageName);
