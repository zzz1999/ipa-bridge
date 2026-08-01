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
        nameof(AppConfiguration.SchemaVersion),
        nameof(AppConfiguration.IpatoolPath),
        nameof(AppConfiguration.DeviceToolsDirectory),
        nameof(AppConfiguration.DownloadDirectory),
        nameof(AppConfiguration.AppleAccounts),
        nameof(AppConfiguration.SelectedAppleAccountId),
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
                var encryptedResult = await LoadEncryptedConfigurationAsync();
                Current = encryptedResult.Configuration;
                if (File.Exists(_legacyConfigurationFile))
                {
                    var legacyResult = await LoadLegacyConfigurationAsync();
                    if (!ConfigurationsMatch(
                            Current,
                            legacyResult.Configuration,
                            encryptedResult.MigratedLegacyAccount ||
                            legacyResult.MigratedLegacyAccount))
                    {
                        throw new InvalidDataException(
                            "Encrypted and legacy settings both exist with different values. " +
                            "IPA Bridge preserved both files to prevent data loss. Move the unwanted file aside and restart.");
                    }

                    File.Delete(_legacyConfigurationFile);
                }

                if (encryptedResult.RequiresRewrite)
                {
                    await SaveCoreAsync(allowKeyCreation: false);
                }
            }
            else if (File.Exists(_legacyConfigurationFile))
            {
                Current = (await LoadLegacyConfigurationAsync()).Configuration;
                await _dataProtectionService.EnsureKeyExistsAsync();
                await SaveCoreAsync(allowKeyCreation: true);

                var verifiedConfiguration =
                    (await LoadEncryptedConfigurationAsync()).Configuration;
                if (!ConfigurationsMatch(
                        Current,
                        verifiedConfiguration,
                        allowDifferentProfileIds: false))
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
        _ = NormalizeConfiguration(Current, "current");
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

    private async Task<ConfigurationReadResult> LoadEncryptedConfigurationAsync()
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

    private async Task<ConfigurationReadResult> LoadLegacyConfigurationAsync()
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

    private ConfigurationReadResult DeserializeConfiguration(
        byte[] plaintext,
        string sourceDescription)
    {
        AppConfiguration configuration;
        var hasCurrentSchemaShape = false;
        try
        {
            using var document = JsonDocument.Parse(plaintext);
            hasCurrentSchemaShape = HasCurrentSchemaShape(document.RootElement);
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

        var normalization = NormalizeConfiguration(configuration, sourceDescription);
        return new ConfigurationReadResult(
            configuration,
            !hasCurrentSchemaShape || normalization.Changed,
            normalization.MigratedLegacyAccount);
    }

    private static bool HasCurrentSchemaShape(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty(nameof(AppConfiguration.SchemaVersion), out var schemaVersion) ||
            schemaVersion.ValueKind != JsonValueKind.Number ||
            !schemaVersion.TryGetInt32(out var version) ||
            version != AppConfiguration.CurrentSchemaVersion ||
            !root.TryGetProperty(nameof(AppConfiguration.AppleAccounts), out var profiles) ||
            profiles.ValueKind != JsonValueKind.Array ||
            !root.TryGetProperty(nameof(AppConfiguration.SelectedAppleAccountId), out var selectedId) ||
            selectedId.ValueKind != JsonValueKind.String ||
            root.TryGetProperty(nameof(AppConfiguration.AppleAccountEmail), out _))
        {
            return false;
        }

        return profiles.EnumerateArray().All(profile =>
            profile.ValueKind == JsonValueKind.Object &&
            profile.TryGetProperty(nameof(AppleAccountProfile.Id), out var id) &&
            id.ValueKind == JsonValueKind.String &&
            profile.TryGetProperty(nameof(AppleAccountProfile.Email), out var email) &&
            email.ValueKind == JsonValueKind.String &&
            profile.TryGetProperty(nameof(AppleAccountProfile.LocalVaultKey), out var localVaultKey) &&
            localVaultKey.ValueKind == JsonValueKind.String);
    }

    private static ConfigurationNormalizationResult NormalizeConfiguration(
        AppConfiguration configuration,
        string sourceDescription)
    {
        if (configuration.SchemaVersion is < 1 or > AppConfiguration.CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"The {sourceDescription} settings file uses unsupported schema version " +
                $"{configuration.SchemaVersion}.");
        }

        var changed = configuration.SchemaVersion != AppConfiguration.CurrentSchemaVersion ||
                      configuration.AppleAccounts is null ||
                      configuration.AppleAccountEmail is not null;
        var originalSelectedAccountId = configuration.SelectedAppleAccountId;
        var selectedAccountId = NormalizeSelectedAccountId(
            configuration.SelectedAppleAccountId);
        var profiles = configuration.AppleAccounts ?? [];
        var normalizedProfiles = new List<AppleAccountProfile>(profiles.Count + 1);
        var profilesByEmail = new Dictionary<string, AppleAccountProfile>(
            StringComparer.OrdinalIgnoreCase);
        var profileIds = new HashSet<string>(StringComparer.Ordinal);
        var selectedDuplicateReplacementId = string.Empty;
        var migratedLegacyAccount = false;

        foreach (var profile in profiles)
        {
            if (profile is null)
            {
                changed = true;
                continue;
            }

            var originalEmail = profile.Email;
            var email = (originalEmail ?? string.Empty).Trim();
            if (email.Length == 0)
            {
                changed = true;
                continue;
            }

            var originalId = profile.Id;
            var id = NormalizeProfileId(profile.Id, sourceDescription);
            var originalLocalVaultKey = profile.LocalVaultKey;
            var localVaultKey = NormalizeLocalVaultKey(
                profile.LocalVaultKey,
                sourceDescription);
            changed |= !string.Equals(originalEmail, email, StringComparison.Ordinal) ||
                       !string.Equals(originalId, id, StringComparison.Ordinal) ||
                       !string.Equals(
                           originalLocalVaultKey,
                           localVaultKey,
                           StringComparison.Ordinal);
            if (profilesByEmail.TryGetValue(email, out var existingProfile))
            {
                changed = true;
                if (string.Equals(selectedAccountId, id, StringComparison.Ordinal))
                {
                    selectedDuplicateReplacementId = existingProfile.Id;
                }

                continue;
            }

            if (!profileIds.Add(id))
            {
                throw new InvalidDataException(
                    $"The {sourceDescription} settings file contains duplicate Apple Account profile IDs.");
            }

            profile.Id = id;
            profile.Email = email;
            profile.LocalVaultKey = localVaultKey;
            profilesByEmail.Add(email, profile);
            normalizedProfiles.Add(profile);
        }

        var legacyEmail = configuration.AppleAccountEmail?.Trim();
        if (!string.IsNullOrWhiteSpace(legacyEmail))
        {
            migratedLegacyAccount = true;
            if (!profilesByEmail.TryGetValue(legacyEmail, out var migratedProfile))
            {
                migratedProfile = new AppleAccountProfile
                {
                    Email = legacyEmail
                };
                normalizedProfiles.Add(migratedProfile);
                profilesByEmail.Add(legacyEmail, migratedProfile);
            }

            if (selectedAccountId.Length == 0)
            {
                selectedAccountId = migratedProfile.Id;
            }
        }

        if (selectedDuplicateReplacementId.Length > 0)
        {
            selectedAccountId = selectedDuplicateReplacementId;
        }

        if (normalizedProfiles.Count == 0)
        {
            selectedAccountId = string.Empty;
        }
        else if (selectedAccountId.Length == 0 ||
                 normalizedProfiles.All(profile =>
                     !string.Equals(profile.Id, selectedAccountId, StringComparison.Ordinal)))
        {
            selectedAccountId = normalizedProfiles[0].Id;
        }

        configuration.SchemaVersion = AppConfiguration.CurrentSchemaVersion;
        configuration.AppleAccounts = normalizedProfiles;
        configuration.SelectedAppleAccountId = selectedAccountId;
        configuration.AppleAccountEmail = null;
        changed |= !string.Equals(
            originalSelectedAccountId,
            selectedAccountId,
            StringComparison.Ordinal);
        return new ConfigurationNormalizationResult(changed, migratedLegacyAccount);
    }

    private static string NormalizeProfileId(string? profileId, string sourceDescription)
    {
        var candidate = profileId?.Trim();
        if (string.IsNullOrEmpty(candidate))
        {
            return Guid.NewGuid().ToString("N");
        }

        if (!Guid.TryParseExact(candidate, "N", out var parsedId) ||
            parsedId == Guid.Empty)
        {
            throw new InvalidDataException(
                $"The {sourceDescription} settings file contains an invalid Apple Account profile ID.");
        }

        return parsedId.ToString("N");
    }

    private static string NormalizeSelectedAccountId(string? selectedAccountId)
    {
        var candidate = selectedAccountId?.Trim();
        return candidate is not null &&
               Guid.TryParseExact(candidate, "N", out var parsedId) &&
               parsedId != Guid.Empty
            ? parsedId.ToString("N")
            : string.Empty;
    }

    private static string NormalizeLocalVaultKey(string? value, string sourceDescription)
    {
        var candidate = value?.Trim();
        if (string.IsNullOrEmpty(candidate))
        {
            return string.Empty;
        }

        byte[] keyBytes;
        try
        {
            keyBytes = Convert.FromBase64String(candidate);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException(
                $"The {sourceDescription} settings file contains an invalid local vault key.",
                exception);
        }

        try
        {
            if (keyBytes.Length != LocalDataProtectionService.MasterKeySize)
            {
                throw new InvalidDataException(
                    $"The {sourceDescription} settings file contains a local vault key with an invalid length.");
            }

            return Convert.ToBase64String(keyBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(keyBytes);
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
        Directory.CreateDirectory(Path.GetDirectoryName(_legacyConfigurationFile)!);
        Directory.CreateDirectory(_defaultDownloadDirectory);
    }

    private static bool ConfigurationsMatch(
        AppConfiguration first,
        AppConfiguration second,
        bool allowDifferentProfileIds)
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
               first.SchemaVersion == second.SchemaVersion &&
               ProfilesMatch(
                   first.AppleAccounts,
                   second.AppleAccounts,
                   allowDifferentProfileIds) &&
               SelectedProfilesMatch(first, second, allowDifferentProfileIds) &&
               first.AutomaticallyRefreshDevices == second.AutomaticallyRefreshDevices;
    }

    private static bool ProfilesMatch(
        IReadOnlyList<AppleAccountProfile> first,
        IReadOnlyList<AppleAccountProfile> second,
        bool allowDifferentProfileIds)
    {
        if (first.Count != second.Count)
        {
            return false;
        }

        for (var index = 0; index < first.Count; index++)
        {
            if ((!allowDifferentProfileIds &&
                 !string.Equals(first[index].Id, second[index].Id, StringComparison.Ordinal)) ||
                !string.Equals(
                    first[index].Email,
                    second[index].Email,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(
                    first[index].LocalVaultKey,
                    second[index].LocalVaultKey,
                    StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static bool SelectedProfilesMatch(
        AppConfiguration first,
        AppConfiguration second,
        bool allowDifferentProfileIds)
    {
        if (!allowDifferentProfileIds)
        {
            return string.Equals(
                first.SelectedAppleAccountId,
                second.SelectedAppleAccountId,
                StringComparison.Ordinal);
        }

        var firstSelectedEmail = first.AppleAccounts.FirstOrDefault(profile =>
            string.Equals(
                profile.Id,
                first.SelectedAppleAccountId,
                StringComparison.Ordinal))?.Email;
        var secondSelectedEmail = second.AppleAccounts.FirstOrDefault(profile =>
            string.Equals(
                profile.Id,
                second.SelectedAppleAccountId,
                StringComparison.Ordinal))?.Email;
        return string.Equals(
            firstSelectedEmail,
            secondSelectedEmail,
            StringComparison.OrdinalIgnoreCase);
    }

    private AppConfiguration CreateDefault()
    {
        return new AppConfiguration
        {
            DownloadDirectory = _defaultDownloadDirectory
        };
    }

    private sealed record ConfigurationReadResult(
        AppConfiguration Configuration,
        bool RequiresRewrite,
        bool MigratedLegacyAccount);

    private sealed record ConfigurationNormalizationResult(
        bool Changed,
        bool MigratedLegacyAccount);

    private enum ConfigurationLoadState
    {
        NotLoaded,
        Loading,
        Loaded,
        Failed
    }
}
