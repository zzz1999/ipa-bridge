using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using IPABridge.Infrastructure;
using IPABridge.Models;

namespace IPABridge.Services;

public sealed class ConfigurationService
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private static readonly HashSet<string> LegacyPropertyNames =
    [
        nameof(AppConfiguration.IpatoolPath),
        nameof(AppConfiguration.DeviceToolsDirectory),
        nameof(AppConfiguration.DownloadDirectory),
        nameof(AppConfiguration.AppleAccountEmail),
        nameof(AppConfiguration.AutomaticallyRefreshDevices)
    ];

    private readonly string _configurationFile;
    private readonly string _legacyConfigurationFile;
    private readonly string _defaultDownloadDirectory;
    private readonly LocalDataProtectionService _dataProtectionService;
    private readonly bool _usesApplicationDirectories;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private ConfigurationLoadState _loadState;

    public ConfigurationService()
        : this(
            AppPaths.ConfigurationFile,
            AppPaths.LegacyConfigurationFile,
            AppPaths.LocalDataKeyFile,
            AppPaths.DefaultDownloadDirectory,
            new WindowsCurrentUserKeyProtector(),
            new CryptographicRandomByteGenerator(),
            true)
    {
    }

    internal ConfigurationService(string configurationFile, string defaultDownloadDirectory)
        : this(
            configurationFile,
            Path.Combine(Path.GetDirectoryName(Path.GetFullPath(configurationFile))!, "settings.legacy.json"),
            Path.Combine(Path.GetDirectoryName(Path.GetFullPath(configurationFile))!, "master-key.v1"),
            defaultDownloadDirectory,
            new WindowsCurrentUserKeyProtector(),
            new CryptographicRandomByteGenerator(),
            false)
    {
    }

    internal ConfigurationService(
        string configurationFile,
        string legacyConfigurationFile,
        string keyFile,
        string defaultDownloadDirectory,
        ILocalDataKeyProtector keyProtector,
        IRandomByteGenerator randomByteGenerator)
        : this(
            configurationFile,
            legacyConfigurationFile,
            keyFile,
            defaultDownloadDirectory,
            keyProtector,
            randomByteGenerator,
            false)
    {
    }

    private ConfigurationService(
        string configurationFile,
        string legacyConfigurationFile,
        string keyFile,
        string defaultDownloadDirectory,
        ILocalDataKeyProtector keyProtector,
        IRandomByteGenerator randomByteGenerator,
        bool usesApplicationDirectories)
    {
        _configurationFile = Path.GetFullPath(configurationFile);
        _legacyConfigurationFile = Path.GetFullPath(legacyConfigurationFile);
        _defaultDownloadDirectory = Path.GetFullPath(defaultDownloadDirectory);
        _dataProtectionService = new LocalDataProtectionService(
            keyFile,
            keyProtector,
            randomByteGenerator);
        _usesApplicationDirectories = usesApplicationDirectories;
        Current = CreateDefault();
    }

    public AppConfiguration Current { get; private set; }

    public async Task<AppConfiguration> LoadAsync()
    {
        await _operationGate.WaitAsync();
        _loadState = ConfigurationLoadState.Loading;
        try
        {
            EnsureDirectories();
            if (File.Exists(_configurationFile))
            {
                Current = await LoadEncryptedConfigurationAsync();
                if (File.Exists(_legacyConfigurationFile))
                {
                    var legacyConfiguration = await LoadLegacyConfigurationAsync();
                    if (!ConfigurationsMatch(Current, legacyConfiguration))
                    {
                        throw new InvalidDataException(
                            "Encrypted and legacy settings both exist with different values. " +
                            "IPA Bridge preserved both files to prevent data loss. Move the unwanted file aside and restart.");
                    }

                    File.Delete(_legacyConfigurationFile);
                }
            }
            else if (File.Exists(_legacyConfigurationFile))
            {
                Current = await LoadLegacyConfigurationAsync();
                await _dataProtectionService.EnsureKeyExistsAsync();
                await SaveCoreAsync(allowKeyCreation: true);

                var verifiedConfiguration = await LoadEncryptedConfigurationAsync();
                if (!ConfigurationsMatch(Current, verifiedConfiguration))
                {
                    throw new InvalidDataException(
                        "The encrypted settings migration could not be verified. The legacy settings were preserved.");
                }

                File.Delete(_legacyConfigurationFile);
                Current = verifiedConfiguration;
            }
            else
            {
                await _dataProtectionService.EnsureKeyExistsAsync();
                Current = CreateDefault();
            }

            _loadState = ConfigurationLoadState.Loaded;
            return Current;
        }
        catch
        {
            _loadState = ConfigurationLoadState.Failed;
            throw;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task SaveAsync()
    {
        await _operationGate.WaitAsync();
        try
        {
            if (_loadState != ConfigurationLoadState.Loaded)
            {
                throw new InvalidOperationException(
                    "Settings cannot be saved until IPA Bridge has loaded them successfully. " +
                    "Resolve the initialization error and restart before changing settings.");
            }

            EnsureDirectories();
            await SaveCoreAsync(allowKeyCreation: !File.Exists(_configurationFile));
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private async Task SaveCoreAsync(bool allowKeyCreation)
    {
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(Current, SerializerOptions);
        byte[]? encryptedEnvelope = null;
        try
        {
            encryptedEnvelope = await _dataProtectionService.EncryptAsync(
                plaintext,
                allowKeyCreation);
            await AtomicFile.WriteAllBytesAsync(
                _configurationFile,
                encryptedEnvelope,
                overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            if (encryptedEnvelope is not null)
            {
                CryptographicOperations.ZeroMemory(encryptedEnvelope);
            }
        }
    }

    private async Task<AppConfiguration> LoadEncryptedConfigurationAsync()
    {
        var encryptedEnvelope = await File.ReadAllBytesAsync(_configurationFile);
        byte[]? plaintext = null;
        try
        {
            plaintext = await _dataProtectionService.DecryptAsync(encryptedEnvelope);
            return DeserializeConfiguration(plaintext, "encrypted");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encryptedEnvelope);
            if (plaintext is not null)
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
    }

    private async Task<AppConfiguration> LoadLegacyConfigurationAsync()
    {
        var plaintext = await File.ReadAllBytesAsync(_legacyConfigurationFile);
        try
        {
            try
            {
                using var document = JsonDocument.Parse(plaintext);
                if (document.RootElement.ValueKind != JsonValueKind.Object ||
                    !document.RootElement.EnumerateObject().Any(
                        property => LegacyPropertyNames.Contains(property.Name)))
                {
                    throw new InvalidDataException(
                        "The legacy settings file does not contain a recognized IPA Bridge configuration.");
                }
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException("The legacy settings file is not valid JSON.", exception);
            }

            return DeserializeConfiguration(plaintext, "legacy");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private AppConfiguration DeserializeConfiguration(byte[] plaintext, string sourceDescription)
    {
        AppConfiguration configuration;
        try
        {
            configuration = JsonSerializer.Deserialize<AppConfiguration>(plaintext, SerializerOptions)
                            ?? throw new InvalidDataException(
                                $"The {sourceDescription} settings file is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"The {sourceDescription} settings file contains invalid data.",
                exception);
        }

        if (string.IsNullOrWhiteSpace(configuration.DownloadDirectory))
        {
            configuration.DownloadDirectory = _defaultDownloadDirectory;
        }

        return configuration;
    }

    private void EnsureDirectories()
    {
        if (_usesApplicationDirectories)
        {
            AppPaths.EnsureDirectories();
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_configurationFile)!);
        Directory.CreateDirectory(Path.GetDirectoryName(_legacyConfigurationFile)!);
        Directory.CreateDirectory(_defaultDownloadDirectory);
    }

    private static bool ConfigurationsMatch(AppConfiguration first, AppConfiguration second)
    {
        return string.Equals(first.IpatoolPath, second.IpatoolPath, StringComparison.Ordinal) &&
               string.Equals(
                   first.DeviceToolsDirectory,
                   second.DeviceToolsDirectory,
                   StringComparison.Ordinal) &&
               string.Equals(
                   first.DownloadDirectory,
                   second.DownloadDirectory,
                   StringComparison.Ordinal) &&
               string.Equals(
                   first.AppleAccountEmail,
                   second.AppleAccountEmail,
                   StringComparison.Ordinal) &&
               first.AutomaticallyRefreshDevices == second.AutomaticallyRefreshDevices;
    }

    private AppConfiguration CreateDefault()
    {
        return new AppConfiguration
        {
            DownloadDirectory = _defaultDownloadDirectory
        };
    }

    private enum ConfigurationLoadState
    {
        NotLoaded,
        Loading,
        Loaded,
        Failed
    }
}
