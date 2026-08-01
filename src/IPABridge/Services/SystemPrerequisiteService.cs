using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using IPABridge.Infrastructure;
using IPABridge.Models;
using Microsoft.Win32;
using Windows.ApplicationModel;
using Windows.Management.Deployment;

namespace IPABridge.Services;

public sealed partial class SystemPrerequisiteService
{
    public const string AppleDevicesStoreProductId = "9NP83LWLPZ9K";
    public const string AppleDevicesStoreUri =
        $"ms-windows-store://pdp/?ProductId={AppleDevicesStoreProductId}";
    public const string AppleDevicesStoreWebUrl =
        $"https://apps.microsoft.com/detail/{AppleDevicesStoreProductId}";

    private static readonly (string DirectoryPattern, string InfName)[] AppleUsbDriverPackages =
    [
        ("appleusb.inf_*", "appleusb.inf"),
        ("usbaapl64.inf_*", "usbaapl64.inf"),
        ("usbaapl.inf_*", "usbaapl.inf")
    ];

    private readonly ProcessRunner _processRunner;
    private readonly ToolLocationService? _toolLocationService;

    public SystemPrerequisiteService(
        ProcessRunner? processRunner = null,
        ToolLocationService? toolLocationService = null)
    {
        _processRunner = processRunner ?? new ProcessRunner();
        _toolLocationService = toolLocationService;
    }

    public async Task<AppleDeviceSupportStatus> GetAppleDeviceSupportStatusAsync(
        CancellationToken cancellationToken = default)
    {
        var package = TryFindAppleDevicesPackage();
        var serviceRegistered = IsAppleMobileDeviceServiceRegistered();
        var driverStoreDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32",
            "DriverStore",
            "FileRepository");
        // Driver Store inspection is diagnostic; unreadable or renamed packages remain unknown.
        var driverTask = Task.Run(
            () => DetectAppleUsbDriver(driverStoreDirectory),
            cancellationToken);
        var endpointTask = IsAppleDeviceTransportEndpointReachableAsync(cancellationToken);
        var backendProbeTask = ProbeDeviceBackendAsync(cancellationToken);
        await Task.WhenAll(driverTask, endpointTask, backendProbeTask);

        var driver = await driverTask;
        var backendProbe = await backendProbeTask;
        return new AppleDeviceSupportStatus
        {
            HasBeenChecked = true,
            IsAppleDevicesInstalled = package is not null,
            AppleDevicesVersion = package?.Version,
            IsUsbDriverInstalled = driver.IsInstalled,
            UsbDriverVersion = driver.Version,
            UsbDriverPackageName = driver.PackageName,
            IsTransportEndpointReachable = await endpointTask,
            IsTransportServiceRegistered = serviceRegistered,
            IsBackendProbeSuccessful = backendProbe.IsSuccessful,
            BackendName = backendProbe.BackendName,
            BackendProbeError = backendProbe.Error
        };
    }

    public async Task<bool> IsAppleDeviceServiceAvailableAsync(
        CancellationToken cancellationToken = default)
    {
        return (await GetAppleDeviceSupportStatusAsync(cancellationToken)).IsReady;
    }

    public async Task<AppleDevicesInstallationResult> InstallAppleDevicesAsync(
        IProgress<ToolInstallationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Report(new ToolInstallationProgress(
            "Checking Windows Package Manager…",
            0.05));
        var winget = await ResolveWorkingWingetAsync(cancellationToken);
        if (winget is null)
        {
            var storeOpened = TryOpenAppleDevicesStorePage();
            var currentStatus = await GetAppleDeviceSupportStatusAsync(cancellationToken);
            return new AppleDevicesInstallationResult(
                false,
                storeOpened,
                AppendStoreFallback(
                    "A working WinGet installation was not detected.",
                    storeOpened),
                currentStatus);
        }

        progress?.Report(new ToolInstallationProgress(
            "Downloading and installing Apple Devices through Microsoft Store…",
            0.18));
        using var installationTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        installationTimeout.CancelAfter(TimeSpan.FromMinutes(15));

        ProcessResult result;
        try
        {
            result = await _processRunner.RunAsync(
                winget,
                BuildAppleDevicesWingetInstallArguments(),
                outputReceived: _ => progress?.Report(new ToolInstallationProgress(
                    "Microsoft Store is processing Apple Devices…",
                    0.55)),
                cancellationToken: installationTimeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            var storeOpened = TryOpenAppleDevicesStorePage();
            var timedOutStatus = await GetAppleDeviceSupportStatusAsync(cancellationToken);
            return new AppleDevicesInstallationResult(
                false,
                storeOpened,
                AppendStoreFallback("The automatic installation timed out.", storeOpened),
                timedOutStatus);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            var storeOpened = TryOpenAppleDevicesStorePage();
            var launchFailedStatus = await GetAppleDeviceSupportStatusAsync(cancellationToken);
            return new AppleDevicesInstallationResult(
                false,
                storeOpened,
                AppendStoreFallback($"Could not start WinGet: {exception.Message}", storeOpened),
                launchFailedStatus);
        }

        var exitCode = unchecked((uint)result.ExitCode);
        var packageAlreadyInstalled = exitCode == 0x8A150061;
        var rebootRequired = exitCode == 0x8A150109;
        if (rebootRequired)
        {
            var rebootStatus = await GetAppleDeviceSupportStatusAsync(cancellationToken);
            var rebootInstallationVerified = rebootStatus.IsAppleDevicesInstalled;
            return new AppleDevicesInstallationResult(
                rebootInstallationVerified,
                false,
                rebootInstallationVerified
                    ? "Apple Devices installation was verified. Restart Windows to enable its drivers and device services."
                    : "WinGet reported that a restart is required, but the Apple Devices package is not registered yet. Restart Windows, then check again; this automatic request has not been marked as verified.",
                rebootStatus);
        }

        if (!result.IsSuccess && !packageAlreadyInstalled)
        {
            var error = DescribeWingetFailure(result);
            var storeOpened = TryOpenAppleDevicesStorePage();
            var failedStatus = await GetAppleDeviceSupportStatusAsync(cancellationToken);
            return new AppleDevicesInstallationResult(
                false,
                storeOpened,
                AppendStoreFallback(error, storeOpened),
                failedStatus);
        }

        progress?.Report(new ToolInstallationProgress(
            "Verifying the Apple Devices package and device transport…",
            0.88));
        var status = await WaitForInstalledStatusAsync(cancellationToken);
        progress?.Report(new ToolInstallationProgress(
            "The Apple Devices installation request has completed",
            1));

        var installationVerified = status.IsAppleDevicesInstalled;
        var message = (installationVerified, status.IsReady, status.HasCompleteUsbSupport) switch
        {
            (true, true, true) =>
                "Apple Devices installation was verified. The Apple USB driver and idevice_id transport probe are ready.",
            (true, true, false) =>
                "Apple Devices installation and the idevice_id transport probe were verified, but the Apple USB driver was not detected in the Windows Driver Store.",
            (true, false, _) =>
                "Apple Devices installation was verified. Open Apple Devices once, install the iOS device tools if needed, then check again to verify device communication.",
            _ =>
                "WinGet completed the install command, but the Apple Devices package could not be verified. Open Microsoft Store to confirm its status; this automatic request has not been marked successful."
        };
        return new AppleDevicesInstallationResult(
            installationVerified,
            false,
            message,
            status);
    }

    public static AppleUsbDriverDetection DetectAppleUsbDriver(string driverStoreDirectory)
    {
        if (string.IsNullOrWhiteSpace(driverStoreDirectory) ||
            !Directory.Exists(driverStoreDirectory))
        {
            return new AppleUsbDriverDetection(null, null, null);
        }

        var candidates = new List<AppleUsbDriverDetection>();
        try
        {
            foreach (var (directoryPattern, infName) in AppleUsbDriverPackages)
            {
                foreach (var packageDirectory in Directory.EnumerateDirectories(
                             driverStoreDirectory,
                             directoryPattern,
                             SearchOption.TopDirectoryOnly))
                {
                    var infPath = Path.Combine(packageDirectory, infName);
                    var version = TryReadDriverVersion(infPath);
                    candidates.Add(new AppleUsbDriverDetection(true, version, infName));
                }
            }
        }
        catch (UnauthorizedAccessException)
        {
            return new AppleUsbDriverDetection(null, null, null);
        }
        catch (IOException)
        {
            return new AppleUsbDriverDetection(null, null, null);
        }

        return candidates
                   .OrderByDescending(candidate => ParseVersion(candidate.Version))
                   .FirstOrDefault()
               ?? new AppleUsbDriverDetection(false, null, null);
    }

    public static IReadOnlyList<string> BuildAppleDevicesWingetInstallArguments()
    {
        return
        [
            "install",
            "--id",
            AppleDevicesStoreProductId,
            "--exact",
            "--source",
            "msstore",
            "--accept-source-agreements",
            "--accept-package-agreements",
            "--silent",
            "--disable-interactivity"
        ];
    }

    public static void OpenAppleDevicesStorePage()
    {
        _ = TryOpenAppleDevicesStorePage();
    }

    public static bool TryOpenAppleDevicesStorePage()
    {
        return TryOpenUri(AppleDevicesStoreUri) || TryOpenUri(AppleDevicesStoreWebUrl);
    }

    private async Task<bool> IsAppleDeviceTransportEndpointReachableAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            // Port reachability is diagnostic only and never establishes operational readiness.
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, 27015, cancellationToken)
                .AsTask()
                .WaitAsync(TimeSpan.FromMilliseconds(500), cancellationToken);
            return client.Connected;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    private async Task<DeviceBackendProbe> ProbeDeviceBackendAsync(
        CancellationToken cancellationToken)
    {
        var location = _toolLocationService?.ResolveDeviceTools() ?? DeviceToolLocation.Missing;
        if (!location.IsAvailable)
        {
            return new DeviceBackendProbe(null, null, null);
        }

        var backendName = location.Backend switch
        {
            DeviceBackend.ModernIdeviceTools => "idevice-tools",
            DeviceBackend.Libimobiledevice => "libimobiledevice",
            _ => null
        };
        var arguments = location.Backend == DeviceBackend.Libimobiledevice
            ? new[] { "-l" }
            : Array.Empty<string>();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            var result = await _processRunner.RunAsync(
                Path.Combine(location.DirectoryPath, "idevice_id.exe"),
                arguments,
                location.DirectoryPath,
                cancellationToken: timeout.Token);
            return result.IsSuccess
                ? new DeviceBackendProbe(true, backendName, null)
                : new DeviceBackendProbe(
                    false,
                    backendName,
                    ExtractProbeFailure(result.CombinedOutput) ??
                    $"idevice_id exited with code {result.ExitCode}.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new DeviceBackendProbe(false, backendName, "The idevice_id probe timed out.");
        }
        catch (Exception exception)
        {
            return new DeviceBackendProbe(false, backendName, exception.Message);
        }
    }

    private static AppleDevicesPackage? TryFindAppleDevicesPackage()
    {
        try
        {
            var package = new PackageManager()
                .FindPackagesForUser(string.Empty)
                .FirstOrDefault(IsAppleDevicesPackage);
            if (package is null)
            {
                return null;
            }

            var version = package.Id.Version;
            return new AppleDevicesPackage(
                package.Id.Name,
                $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}");
        }
        catch
        {
            return null;
        }
    }

    private static bool IsAppleDevicesPackage(Package package)
    {
        return !package.IsFramework &&
               !package.IsResourcePackage &&
               package.Id.Name.Contains("AppleDevices", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAppleMobileDeviceServiceRegistered()
    {
        try
        {
            using var services = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Services",
                writable: false);
            if (services is null)
            {
                return false;
            }

            foreach (var serviceName in services.GetSubKeyNames().Where(name =>
                         name.Contains("Apple", StringComparison.OrdinalIgnoreCase)))
            {
                using var service = services.OpenSubKey(serviceName, writable: false);
                var displayName = service?.GetValue("DisplayName") as string;
                if (serviceName.Contains("AppleMobileDevice", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(
                        serviceName,
                        "Apple Mobile Device Service",
                        StringComparison.OrdinalIgnoreCase) ||
                    displayName?.Contains(
                        "Apple Mobile Device",
                        StringComparison.OrdinalIgnoreCase) == true)
                {
                    return true;
                }
            }
        }
        catch
        {
            // Registry access can be restricted by enterprise policy.
        }

        return false;
    }

    private async Task<string?> ResolveWorkingWingetAsync(CancellationToken cancellationToken)
    {
        var localAlias = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft",
            "WindowsApps",
            "winget.exe");
        var candidates = new[] { "winget.exe", localAlias }
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in candidates)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            try
            {
                var result = await _processRunner.RunAsync(
                    candidate,
                    ["--version"],
                    cancellationToken: timeout.Token);
                if (result.IsSuccess && !string.IsNullOrWhiteSpace(result.CombinedOutput))
                {
                    return candidate;
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Try the next known WinGet entry point.
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // App execution aliases can exist while App Installer is unavailable.
            }
        }

        return null;
    }

    private async Task<AppleDeviceSupportStatus> WaitForInstalledStatusAsync(
        CancellationToken cancellationToken)
    {
        AppleDeviceSupportStatus status = AppleDeviceSupportStatus.Checking;
        for (var attempt = 0; attempt < 6; attempt++)
        {
            status = await GetAppleDeviceSupportStatusAsync(cancellationToken);
            if (status.IsAppleDevicesInstalled)
            {
                return status;
            }

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }

        return status;
    }

    private static string? TryReadDriverVersion(string infPath)
    {
        try
        {
            if (!File.Exists(infPath))
            {
                return null;
            }

            var content = File.ReadAllText(infPath);
            var match = DriverVersionPattern().Match(content);
            return match.Success ? match.Groups[1].Value.Trim() : null;
        }
        catch
        {
            return null;
        }
    }

    private static Version ParseVersion(string? value)
    {
        return Version.TryParse(value, out var version) ? version : new Version(0, 0);
    }

    internal static string? ExtractProbeFailure(string output)
    {
        var sanitized = AnsiEscapePattern().Replace(output, string.Empty);
        if (sanitized.Contains("code: 10061", StringComparison.OrdinalIgnoreCase) ||
            sanitized.Contains("actively refused", StringComparison.OrdinalIgnoreCase))
        {
            return "Apple device transport is not running.";
        }

        if (sanitized.Contains(
                "Failed to parse USBMUXD_SOCKET_ADDRESS",
                StringComparison.OrdinalIgnoreCase))
        {
            return "The USBMUXD_SOCKET_ADDRESS setting is invalid.";
        }

        // Rust panic boilerplate is an implementation detail, not a useful recovery step.
        var lines = sanitized.Split(
            ['\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines
            .Where(line => !IsProbeDiagnosticNoise(line))
            .LastOrDefault() ??
            (lines.Length > 0 ? "The device communication tool stopped unexpectedly." : null);
    }

    private static bool IsProbeDiagnosticNoise(string line)
    {
        return line.StartsWith("thread '", StringComparison.OrdinalIgnoreCase) ||
               line.StartsWith("stack backtrace", StringComparison.OrdinalIgnoreCase) ||
               line.StartsWith("note:", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryOpenUri(string uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string AppendStoreFallback(string message, bool storeOpened)
    {
        return storeOpened
            ? $"{message} The official Apple Devices page was opened in Microsoft Store; continue the installation there."
            : $"{message} Microsoft Store could not be opened automatically; search for \"Apple Devices\" in the Store.";
    }

    private static string DescribeWingetFailure(ProcessResult result)
    {
        return unchecked((uint)result.ExitCode) switch
        {
            0x8A15001B => "The Microsoft Store client is blocked by system policy.",
            0x8A15001C => "Microsoft Store app installation is blocked by system policy.",
            0x8A150019 => "Installation requires administrator privileges. Retry only after user approval.",
            0x8A150076 => "Microsoft Store requires interactive account authentication.",
            0x8A15010C => "The user cancelled the Apple Devices installation.",
            0x8A15010F => "An organization policy blocked the Apple Devices installation.",
            0x8A150107 => "Microsoft Store is currently unreachable.",
            _ => BuildGenericWingetError(result)
        };
    }

    private static string BuildGenericWingetError(ProcessResult result)
    {
        var lastLine = AnsiEscapePattern()
            .Replace(result.CombinedOutput, string.Empty)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault();
        return string.IsNullOrWhiteSpace(lastLine)
            ? $"Automatic Apple Devices installation failed " +
              $"(WinGet exit code 0x{unchecked((uint)result.ExitCode):X8})."
            : $"Automatic Apple Devices installation failed: {lastLine}";
    }

    [GeneratedRegex(@"(?im)^\s*DriverVer\s*=\s*[^,\r\n]+,\s*([^;\r\n]+)")]
    private static partial Regex DriverVersionPattern();

    [GeneratedRegex(@"\x1B(?:[@-Z\\-_]|\[[0-?]*[ -/]*[@-~])", RegexOptions.CultureInvariant)]
    private static partial Regex AnsiEscapePattern();

    private sealed record AppleDevicesPackage(string Name, string Version);

    private sealed record DeviceBackendProbe(
        bool? IsSuccessful,
        string? BackendName,
        string? Error);
}
