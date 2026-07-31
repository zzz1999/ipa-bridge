using System.Text.RegularExpressions;
using IPABridge.Infrastructure;
using IPABridge.Models;

namespace IPABridge.Services;

public sealed partial class DeviceService
{
    private readonly ToolLocationService _toolLocationService;
    private readonly ProcessRunner _processRunner;
    private readonly SemaphoreSlim _operationLock = new(1, 1);

    public DeviceService(ToolLocationService toolLocationService, ProcessRunner processRunner)
    {
        _toolLocationService = toolLocationService;
        _processRunner = processRunner;
    }

    public DeviceToolLocation ToolLocation => _toolLocationService.ResolveDeviceTools();

    public async Task<IReadOnlyList<ConnectedDevice>> GetConnectedDevicesAsync(
        CancellationToken cancellationToken = default)
    {
        var location = RequireTools();
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            return location.Backend switch
            {
                DeviceBackend.ModernIdeviceTools =>
                    await GetModernDevicesAsync(location, cancellationToken),
                DeviceBackend.Libimobiledevice =>
                    await GetLibimobiledeviceDevicesAsync(location, cancellationToken),
                _ => []
            };
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task PairAsync(ConnectedDevice device, CancellationToken cancellationToken = default)
    {
        var location = RequireTools();
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            ProcessResult result;
            if (location.Backend == DeviceBackend.ModernIdeviceTools)
            {
                result = await _processRunner.RunAsync(
                    Path.Combine(location.DirectoryPath, "idevice-tools.exe"),
                    BuildModernPairArguments(device.Udid),
                    location.DirectoryPath,
                    cancellationToken: cancellationToken);
                EnsureProcessSuccess(result, "Pairing failed");

                var verification = await ReadModernDeviceValueAsync(
                    location,
                    device.Udid,
                    "DeviceName",
                    requirePairing: true,
                    cancellationToken);
                if (!verification.Success || string.IsNullOrWhiteSpace(verification.Value))
                {
                    throw new InvalidOperationException(
                        "A trusted device session could not be established after pairing. " +
                        "Keep the device unlocked and confirm \"Trust This Computer\".");
                }
            }
            else
            {
                var pairTool = Path.Combine(location.DirectoryPath, "idevicepair.exe");
                if (!File.Exists(pairTool))
                {
                    throw new FileNotFoundException(
                        "The selected libimobiledevice directory does not contain idevicepair.exe.");
                }

                result = await _processRunner.RunAsync(
                    pairTool,
                    ["-u", device.Udid, "pair"],
                    location.DirectoryPath,
                    cancellationToken: cancellationToken);
                EnsureProcessSuccess(result, "Pairing failed");

                var validation = await _processRunner.RunAsync(
                    pairTool,
                    ["-u", device.Udid, "validate"],
                    location.DirectoryPath,
                    cancellationToken: cancellationToken);
                EnsureProcessSuccess(validation, "Pairing validation failed");
            }
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task InstallAsync(
        ConnectedDevice device,
        string ipaPath,
        IProgress<double>? progress = null,
        Action<string>? outputReceived = null,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(ipaPath) ||
            !string.Equals(Path.GetExtension(ipaPath), ".ipa", StringComparison.OrdinalIgnoreCase))
        {
            throw new FileNotFoundException("Select a valid IPA file.", ipaPath);
        }

        var location = RequireTools();
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            void OnOutput(string line)
            {
                outputReceived?.Invoke(RemoveAnsi(line));
                var match = ProgressPattern().Match(line);
                if (match.Success &&
                    double.TryParse(match.Groups["percentage"].Value, out var percentage))
                {
                    progress?.Report(Math.Clamp(percentage / 100d, 0, 1));
                }
            }

            ProcessResult result;
            if (location.Backend == DeviceBackend.ModernIdeviceTools)
            {
                result = await _processRunner.RunAsync(
                    Path.Combine(location.DirectoryPath, "idevice-tools.exe"),
                    ["--udid", device.Udid, "ideviceinstaller", "install", ipaPath],
                    location.DirectoryPath,
                    OnOutput,
                    cancellationToken: cancellationToken);

                var combined = result.CombinedOutput;
                if (!result.IsSuccess ||
                    !combined.Contains("install success", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"Installation failed: " +
                        $"{ExtractUsefulError(combined) ?? "The device tool did not return a success status."}");
                }
            }
            else
            {
                var installer = Path.Combine(location.DirectoryPath, "ideviceinstaller.exe");
                result = await _processRunner.RunAsync(
                    installer,
                    BuildLibimobiledeviceInstallArguments(device.Udid, ipaPath, useLegacySyntax: false),
                    location.DirectoryPath,
                    OnOutput,
                    cancellationToken: cancellationToken);

                if (!result.IsSuccess && UsesLegacyInstallSyntax(result.CombinedOutput))
                {
                    // Older user-supplied ideviceinstaller builds use -i instead of the install subcommand.
                    result = await _processRunner.RunAsync(
                        installer,
                        BuildLibimobiledeviceInstallArguments(device.Udid, ipaPath, useLegacySyntax: true),
                        location.DirectoryPath,
                        OnOutput,
                        cancellationToken: cancellationToken);
                }

                EnsureProcessSuccess(result, "Installation failed");
            }

            progress?.Report(1);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    private async Task<IReadOnlyList<ConnectedDevice>> GetModernDevicesAsync(
        DeviceToolLocation location,
        CancellationToken cancellationToken)
    {
        var identifier = Path.Combine(location.DirectoryPath, "idevice_id.exe");
        var listResult = await _processRunner.RunAsync(
            identifier,
            [],
            location.DirectoryPath,
            cancellationToken: cancellationToken);
        if (!listResult.IsSuccess)
        {
            throw new InvalidOperationException(
                "Could not connect to Apple Mobile Device Service. " +
                "Install and start Apple Devices, then reconnect the device.");
        }

        var matches = ModernDevicePattern().Matches(RemoveAnsi(listResult.CombinedOutput));
        var devices = new List<ConnectedDevice>();
        foreach (Match match in matches)
        {
            var udid = match.Groups["udid"].Value;
            if (string.IsNullOrWhiteSpace(udid) ||
                devices.Any(device => string.Equals(device.Udid, udid, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var connection = match.Groups["connection"].Value;
            var nameResult = await ReadModernDeviceValueAsync(
                location,
                udid,
                "DeviceName",
                requirePairing: true,
                cancellationToken);
            var isPaired = nameResult.Success;
            var name = nameResult.Value;
            if (!isPaired)
            {
                name = (await ReadModernDeviceValueAsync(
                    location,
                    udid,
                    "DeviceName",
                    requirePairing: false,
                    cancellationToken)).Value;
            }

            var productType = (await ReadModernDeviceValueAsync(
                location,
                udid,
                "ProductType",
                requirePairing: isPaired,
                cancellationToken)).Value;
            var productVersion = (await ReadModernDeviceValueAsync(
                location,
                udid,
                "ProductVersion",
                requirePairing: isPaired,
                cancellationToken)).Value;

            devices.Add(new ConnectedDevice
            {
                Udid = udid,
                Name = string.IsNullOrWhiteSpace(name) ? "Connected iOS device" : name,
                ProductType = string.IsNullOrWhiteSpace(productType) ? "iOS device" : productType,
                ProductVersion = string.IsNullOrWhiteSpace(productVersion) ? "Unknown version" : productVersion,
                ConnectionType = connection.StartsWith("Network", StringComparison.OrdinalIgnoreCase)
                    ? "Wi-Fi"
                    : "USB",
                IsPaired = isPaired
            });
        }

        return devices;
    }

    private async Task<IReadOnlyList<ConnectedDevice>> GetLibimobiledeviceDevicesAsync(
        DeviceToolLocation location,
        CancellationToken cancellationToken)
    {
        var listResult = await _processRunner.RunAsync(
            Path.Combine(location.DirectoryPath, "idevice_id.exe"),
            ["-l"],
            location.DirectoryPath,
            cancellationToken: cancellationToken);
        EnsureProcessSuccess(listResult, "Failed to read the device list");

        var udids = listResult.StandardOutput
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(value => value.Length >= 8)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var devices = new List<ConnectedDevice>();
        foreach (var udid in udids)
        {
            var name = await ReadLibimobiledeviceValueAsync(location, udid, "DeviceName", cancellationToken);
            var productType = await ReadLibimobiledeviceValueAsync(location, udid, "ProductType", cancellationToken);
            var productVersion = await ReadLibimobiledeviceValueAsync(location, udid, "ProductVersion", cancellationToken);
            var isPaired = await ValidateLibimobiledevicePairingAsync(location, udid, cancellationToken);

            devices.Add(new ConnectedDevice
            {
                Udid = udid,
                Name = isPaired ? name! : "Connected iOS device",
                ProductType = productType ?? "iOS device",
                ProductVersion = productVersion ?? "Unknown version",
                ConnectionType = "USB",
                IsPaired = isPaired
            });
        }

        return devices;
    }

    private async Task<(bool Success, string? Value)> ReadModernDeviceValueAsync(
        DeviceToolLocation location,
        string udid,
        string key,
        bool requirePairing,
        CancellationToken cancellationToken)
    {
        var arguments = new List<string> { "--udid", udid, "lockdown" };
        if (!requirePairing)
        {
            arguments.Add("--no-session");
        }

        arguments.Add("get");
        arguments.Add(key);
        var result = await _processRunner.RunAsync(
            Path.Combine(location.DirectoryPath, "idevice-tools.exe"),
            arguments,
            location.DirectoryPath,
            cancellationToken: cancellationToken);
        if (!result.IsSuccess ||
            ContainsError(result.CombinedOutput))
        {
            return (false, null);
        }

        var value = ParseModernValue(result.StandardOutput);
        return string.IsNullOrWhiteSpace(value)
            ? (false, null)
            : (true, value);
    }

    private async Task<bool> ValidateLibimobiledevicePairingAsync(
        DeviceToolLocation location,
        string udid,
        CancellationToken cancellationToken)
    {
        var pairTool = Path.Combine(location.DirectoryPath, "idevicepair.exe");
        var result = await _processRunner.RunAsync(
            pairTool,
            ["-u", udid, "validate"],
            location.DirectoryPath,
            cancellationToken: cancellationToken);
        return result.IsSuccess && !ContainsError(result.CombinedOutput);
    }

    private async Task<string?> ReadLibimobiledeviceValueAsync(
        DeviceToolLocation location,
        string udid,
        string key,
        CancellationToken cancellationToken)
    {
        var infoTool = Path.Combine(location.DirectoryPath, "ideviceinfo.exe");
        if (!File.Exists(infoTool))
        {
            return null;
        }

        var result = await _processRunner.RunAsync(
            infoTool,
            ["-u", udid, "-k", key],
            location.DirectoryPath,
            cancellationToken: cancellationToken);
        return result.IsSuccess ? result.StandardOutput.Trim() : null;
    }

    private static string? ParseModernValue(string output)
    {
        var sanitized = RemoveAnsi(output).Trim();
        if (string.IsNullOrWhiteSpace(sanitized))
        {
            return null;
        }

        var stringMatch = ModernStringValuePattern().Match(sanitized);
        if (stringMatch.Success)
        {
            return stringMatch.Groups["value"].Value;
        }

        var line = sanitized
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault(candidate =>
                !candidate.StartsWith("20", StringComparison.Ordinal) &&
                !candidate.Contains("TRACE", StringComparison.OrdinalIgnoreCase));
        return line?.Trim().Trim('"');
    }

    private DeviceToolLocation RequireTools()
    {
        var location = _toolLocationService.ResolveDeviceTools();
        return location.IsAvailable
            ? location
            : throw new FileNotFoundException("iOS device tools have not been installed or selected.");
    }

    internal static IReadOnlyList<string> BuildModernPairArguments(string udid)
    {
        // idevice-tools v0.1.65 resolves the target for `pair` from this positional argument.
        // Its global --udid provider is created first but is intentionally ignored by pair.rs.
        return ["--udid", udid, "pair", udid, "--name", "IPA Bridge"];
    }

    internal static IReadOnlyList<string> BuildLibimobiledeviceInstallArguments(
        string udid,
        string ipaPath,
        bool useLegacySyntax)
    {
        return useLegacySyntax
            ? ["-u", udid, "-i", ipaPath]
            : ["-u", udid, "install", ipaPath];
    }

    private static void EnsureProcessSuccess(ProcessResult result, string operation)
    {
        if (result.IsSuccess && !ContainsError(result.CombinedOutput))
        {
            return;
        }

        throw new InvalidOperationException(
            $"{operation}: " +
            $"{ExtractUsefulError(result.CombinedOutput) ?? "The device tool returned an error status."}");
    }

    private static bool ContainsError(string output)
    {
        return output.Contains("panic", StringComparison.OrdinalIgnoreCase) ||
               output.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
               output.Contains("error:", StringComparison.OrdinalIgnoreCase) ||
               output.Contains("no device", StringComparison.OrdinalIgnoreCase) ||
               output.Contains("device not found", StringComparison.OrdinalIgnoreCase) ||
               output.Contains("unable to", StringComparison.OrdinalIgnoreCase) ||
               output.Contains("error getting value", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool UsesLegacyInstallSyntax(string output)
    {
        var sanitized = RemoveAnsi(output);
        return LegacyInstallSyntaxPattern().IsMatch(sanitized) ||
               LegacyNoModePattern().IsMatch(sanitized);
    }

    private static string? ExtractUsefulError(string output)
    {
        return RemoveAnsi(output)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault(line =>
                line.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("unable", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("device", StringComparison.OrdinalIgnoreCase));
    }

    private static string RemoveAnsi(string value) => AnsiEscapePattern().Replace(value, string.Empty);

    [GeneratedRegex(
        @"UsbmuxdDevice\s*\{\s*connection_type:\s*(?<connection>[^,\r\n]+),\s*udid:\s*""(?<udid>[^""]+)""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex ModernDevicePattern();

    [GeneratedRegex(
        @"(?:String\()?""(?<value>[^""]+)""\)?",
        RegexOptions.CultureInvariant)]
    private static partial Regex ModernStringValuePattern();

    [GeneratedRegex(
        @"(?<percentage>\d{1,3}(?:\.\d+)?)\s*%",
        RegexOptions.CultureInvariant)]
    private static partial Regex ProgressPattern();

    [GeneratedRegex(
        @"(?:unknown|unrecognized)\s+(?:command|subcommand|option).*\binstall\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LegacyInstallSyntaxPattern();

    [GeneratedRegex(
        @"\bno\s+mode/command\s+was\s+supplied\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LegacyNoModePattern();

    [GeneratedRegex(@"\x1B(?:[@-Z\\-_]|\[[0-?]*[ -/]*[@-~])", RegexOptions.CultureInvariant)]
    private static partial Regex AnsiEscapePattern();
}
