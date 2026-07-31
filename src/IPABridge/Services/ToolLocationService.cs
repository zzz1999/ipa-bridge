using IPABridge.Infrastructure;
using IPABridge.Models;

namespace IPABridge.Services;

public sealed class ToolLocationService
{
    private readonly ConfigurationService _configurationService;

    public ToolLocationService(ConfigurationService configurationService)
    {
        _configurationService = configurationService;
    }

    public string? ResolveIpatool()
    {
        var configured = _configurationService.Current.IpatoolPath;
        var candidates = new[]
        {
            configured,
            Path.Combine(AppPaths.IpatoolDirectory, "ipatool.exe"),
            Path.Combine(AppContext.BaseDirectory, "Tools", "ipatool.exe"),
            FindOnPath("ipatool.exe")
        };

        return candidates.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path));
    }

    public DeviceToolLocation ResolveDeviceTools()
    {
        var configuredDirectories = new[]
        {
            _configurationService.Current.DeviceToolsDirectory,
            AppPaths.DeviceToolsDirectory,
            Path.Combine(AppContext.BaseDirectory, "Tools", "idevice")
        }.Where(path => !string.IsNullOrWhiteSpace(path));

        foreach (var directory in configuredDirectories.Concat(EnumeratePathDirectories()))
        {
            if (File.Exists(Path.Combine(directory, "idevice-tools.exe")) &&
                File.Exists(Path.Combine(directory, "idevice_id.exe")))
            {
                return new DeviceToolLocation(DeviceBackend.ModernIdeviceTools, directory);
            }

            if (File.Exists(Path.Combine(directory, "ideviceinstaller.exe")) &&
                File.Exists(Path.Combine(directory, "idevice_id.exe")) &&
                File.Exists(Path.Combine(directory, "idevicepair.exe")) &&
                File.Exists(Path.Combine(directory, "ideviceinfo.exe")))
            {
                return new DeviceToolLocation(DeviceBackend.Libimobiledevice, directory);
            }
        }

        return DeviceToolLocation.Missing;
    }

    private static string? FindOnPath(string fileName)
    {
        var pathValue = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathValue))
        {
            return null;
        }

        foreach (var directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(directory.Trim(), fileName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (ArgumentException)
            {
                // Ignore malformed PATH entries.
            }
        }

        return null;
    }

    private static IEnumerable<string> EnumeratePathDirectories()
    {
        var pathValue = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathValue))
        {
            yield break;
        }

        foreach (var entry in pathValue.Split(
                     Path.PathSeparator,
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string? directory = null;
            try
            {
                directory = Path.GetFullPath(entry.Trim('"'));
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
            {
                // Ignore malformed PATH entries.
            }

            if (directory is not null && Directory.Exists(directory))
            {
                yield return directory;
            }
        }
    }
}
