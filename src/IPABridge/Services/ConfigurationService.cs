using System.Text.Json;
using IPABridge.Infrastructure;
using IPABridge.Models;

namespace IPABridge.Services;

public sealed class ConfigurationService
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _configurationFile;
    private readonly string _defaultDownloadDirectory;
    private readonly bool _usesApplicationDirectories;

    public ConfigurationService()
        : this(AppPaths.ConfigurationFile, AppPaths.DefaultDownloadDirectory, true)
    {
    }

    internal ConfigurationService(string configurationFile, string defaultDownloadDirectory)
        : this(configurationFile, defaultDownloadDirectory, false)
    {
    }

    private ConfigurationService(
        string configurationFile,
        string defaultDownloadDirectory,
        bool usesApplicationDirectories)
    {
        _configurationFile = Path.GetFullPath(configurationFile);
        _defaultDownloadDirectory = Path.GetFullPath(defaultDownloadDirectory);
        _usesApplicationDirectories = usesApplicationDirectories;
        Current = CreateDefault();
    }

    public AppConfiguration Current { get; private set; }

    public async Task<AppConfiguration> LoadAsync()
    {
        EnsureDirectories();
        if (!File.Exists(_configurationFile))
        {
            Current = CreateDefault();
            return Current;
        }

        try
        {
            await using var stream = File.OpenRead(_configurationFile);
            Current = await JsonSerializer.DeserializeAsync<AppConfiguration>(stream, SerializerOptions)
                      ?? CreateDefault();
        }
        catch (JsonException)
        {
            Current = CreateDefault();
        }

        if (string.IsNullOrWhiteSpace(Current.DownloadDirectory))
        {
            Current.DownloadDirectory = _defaultDownloadDirectory;
        }

        return Current;
    }

    public async Task SaveAsync()
    {
        EnsureDirectories();
        var temporaryFile = $"{_configurationFile}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = File.Create(temporaryFile))
            {
                await JsonSerializer.SerializeAsync(stream, Current, SerializerOptions);
            }

            File.Move(temporaryFile, _configurationFile, overwrite: true);
        }
        catch
        {
            TryDeleteFile(temporaryFile);
            throw;
        }
    }

    private void EnsureDirectories()
    {
        if (_usesApplicationDirectories)
        {
            AppPaths.EnsureDirectories();
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_configurationFile)!);
        Directory.CreateDirectory(_defaultDownloadDirectory);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Preserve the original save failure if the operating system also blocks cleanup.
        }
    }

    private AppConfiguration CreateDefault()
    {
        return new AppConfiguration
        {
            DownloadDirectory = _defaultDownloadDirectory
        };
    }
}
