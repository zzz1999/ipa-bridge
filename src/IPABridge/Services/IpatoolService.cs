using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using IPABridge.Infrastructure;
using IPABridge.Models;

namespace IPABridge.Services;

internal sealed record StagedLocalAccountSessionRemoval(
    string AccountId,
    string OriginalDirectory,
    string StagedDirectory);

public sealed partial class IpatoolService
{
    private const int VersionMetadataConcurrency = 2;
    private const string SessionRemovalStagingDirectoryName = ".pending-session-removals";
    private readonly ToolLocationService _toolLocationService;
    private readonly ProcessRunner _processRunner;
    private readonly ConPtyProcessRunner _conPtyProcessRunner;
    private readonly string _accountSessionsRoot;
    private readonly ConcurrentDictionary<string, StoreAppVersion> _versionMetadataCache =
        new(StringComparer.Ordinal);

    public IpatoolService(
        ToolLocationService toolLocationService,
        ProcessRunner processRunner,
        ConPtyProcessRunner conPtyProcessRunner,
        string? accountSessionsRoot = null)
    {
        _toolLocationService = toolLocationService;
        _processRunner = processRunner;
        _conPtyProcessRunner = conPtyProcessRunner;
        _accountSessionsRoot = Path.GetFullPath(
            accountSessionsRoot ?? AppPaths.IpatoolAccountsDirectory);
    }

    public bool IsAvailable => _toolLocationService.ResolveIpatool() is not null;

    public void RemoveLocalAccountSession(AppleAccountProfile account)
    {
        var accountDirectory = GetAccountHomeDirectory(account.Id);
        var accountsRoot = Path.TrimEndingDirectorySeparator(_accountSessionsRoot);
        if (!accountDirectory.StartsWith(
                accountsRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The Apple Account session path is outside the managed account directory.");
        }

        if (Directory.Exists(accountDirectory))
        {
            Directory.Delete(accountDirectory, recursive: true);
        }

        InvalidateAccountCache(account);
    }

    internal StagedLocalAccountSessionRemoval? StageLocalAccountSessionRemoval(
        AppleAccountProfile account)
    {
        var accountDirectory = GetAccountHomeDirectory(account.Id);
        if (!Directory.Exists(accountDirectory))
        {
            InvalidateAccountCache(account);
            return null;
        }

        var stagingRoot = GetSessionRemovalStagingRoot();
        Directory.CreateDirectory(stagingRoot);
        var operationId = Guid.NewGuid().ToString("N");
        var stagedDirectory = Path.Combine(stagingRoot, $"{account.Id}.{operationId}");
        Directory.Move(accountDirectory, stagedDirectory);
        InvalidateAccountCache(account);
        return new StagedLocalAccountSessionRemoval(
            account.Id,
            accountDirectory,
            stagedDirectory);
    }

    internal void CommitLocalAccountSessionRemoval(
        StagedLocalAccountSessionRemoval? removal)
    {
        if (removal is null)
        {
            return;
        }

        ValidateStagedLocalAccountSessionRemoval(removal);
        if (Directory.Exists(removal.StagedDirectory))
        {
            Directory.Delete(removal.StagedDirectory, recursive: true);
        }

        TryDeleteEmptySessionRemovalStagingRoot();
    }

    internal void RollbackLocalAccountSessionRemoval(
        StagedLocalAccountSessionRemoval? removal)
    {
        if (removal is null)
        {
            return;
        }

        ValidateStagedLocalAccountSessionRemoval(removal);
        if (!Directory.Exists(removal.StagedDirectory))
        {
            throw new DirectoryNotFoundException(
                "The staged Apple Account session is no longer available for rollback.");
        }

        if (Directory.Exists(removal.OriginalDirectory))
        {
            throw new IOException(
                "The Apple Account session rollback target already exists.");
        }

        Directory.Move(removal.StagedDirectory, removal.OriginalDirectory);
        TryDeleteEmptySessionRemovalStagingRoot();
    }

    internal IReadOnlyList<string> RecoverStagedLocalAccountSessionRemovals(
        IEnumerable<string> activeAccountIds)
    {
        var failures = new List<string>();
        var stagingRoot = GetSessionRemovalStagingRoot();
        if (!Directory.Exists(stagingRoot))
        {
            return failures;
        }

        var activeIds = activeAccountIds.ToHashSet(StringComparer.Ordinal);
        foreach (var stagedDirectory in Directory.EnumerateDirectories(
                     stagingRoot,
                     "*",
                     SearchOption.TopDirectoryOnly))
        {
            var directoryName = Path.GetFileName(stagedDirectory);
            var separatorIndex = directoryName.IndexOf('.');
            var accountId = separatorIndex > 0
                ? directoryName[..separatorIndex]
                : string.Empty;
            var operationId = separatorIndex > 0
                ? directoryName[(separatorIndex + 1)..]
                : string.Empty;
            if (!Guid.TryParseExact(accountId, "N", out _) ||
                !Guid.TryParseExact(operationId, "N", out _))
            {
                failures.Add(
                    $"Preserved an unrecognized staged account-session directory: {directoryName}");
                continue;
            }

            try
            {
                if (activeIds.Contains(accountId))
                {
                    var originalDirectory = GetAccountHomeDirectory(accountId);
                    if (Directory.Exists(originalDirectory))
                    {
                        throw new IOException(
                            "Both the active and staged Apple Account session directories exist.");
                    }

                    Directory.Move(stagedDirectory, originalDirectory);
                }
                else
                {
                    Directory.Delete(stagedDirectory, recursive: true);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                failures.Add(
                    $"Could not recover staged account session {accountId}: {exception.Message}");
            }
        }

        TryDeleteEmptySessionRemovalStagingRoot();
        return failures;
    }

    public void InvalidateAccountCache(AppleAccountProfile account)
    {
        var prefix = $"account:{account.Id}|";
        foreach (var cacheKey in _versionMetadataCache.Keys.Where(key =>
                     key.StartsWith(prefix, StringComparison.Ordinal)))
        {
            _versionMetadataCache.TryRemove(cacheKey, out _);
        }
    }

    public async Task<string?> GetVersionAsync(CancellationToken cancellationToken = default)
    {
        var executable = _toolLocationService.ResolveIpatool();
        if (executable is null)
        {
            return null;
        }

        var result = await _processRunner.RunAsync(
            executable,
            ["--version"],
            cancellationToken: cancellationToken);
        if (!result.IsSuccess)
        {
            return null;
        }

        var match = VersionPattern().Match(result.StandardOutput);
        return match.Success ? match.Value : result.StandardOutput.Trim();
    }

    public async Task<IpatoolLoginResult> LoginAsync(
        AppleAccountProfile account,
        string applePassword,
        string? twoFactorCode,
        string vaultPassphrase,
        CancellationToken cancellationToken = default)
    {
        var executable = RequireExecutable();
        var prompts = new[]
        {
            new ConPtyPrompt("apple-password", "enter password:", applePassword),
            new ConPtyPrompt("two-factor", "enter 2FA code:", twoFactorCode),
            new ConPtyPrompt("vault-passphrase", "enter passphrase to unlock", vaultPassphrase)
        };
        var result = await _conPtyProcessRunner.RunAsync(
            executable,
            ["--format", "json", "auth", "login", "--email", account.Email],
            prompts,
            environment: GetAccountEnvironment(account),
            cancellationToken: cancellationToken);

        if (result.MissingPromptKey == "two-factor")
        {
            return new IpatoolLoginResult(
                false,
                true,
                "An Apple two-factor authentication code is required. Enter the code and sign in again.");
        }

        if (result.MissingPromptKey == "vault-passphrase")
        {
            return new IpatoolLoginResult(
                false,
                false,
                "IPA Bridge could not provide the protected local session key. Retry sign-in or reset this local profile.");
        }

        if (IpatoolJsonParser.HasSuccessfulLogin(result.Output))
        {
            var accountInfo = IpatoolJsonParser.ParseAccountInfo(result.Output);
            return new IpatoolLoginResult(
                true,
                false,
                "Apple Account connected in its isolated local session.",
                accountInfo);
        }

        var error = IpatoolJsonParser.FindError(result.Output)
                    ?? "Sign-in did not return a success status. Verify the account details and try again.";
        return new IpatoolLoginResult(false, false, error);
    }

    public async Task<IReadOnlyList<StoreApp>> SearchAsync(
        AppleAccountProfile account,
        string query,
        string vaultPassphrase,
        CancellationToken cancellationToken = default)
    {
        await EnsureAccountMatchesAsync(account, vaultPassphrase, cancellationToken);
        var result = await RunAuthenticatedAsync(
            account,
            ["--format", "json", "search", query, "--limit", "25"],
            vaultPassphrase,
            cancellationToken: cancellationToken);
        EnsureSuccess(result, "Search failed");
        return IpatoolJsonParser.ParseSearchResults(result.Output);
    }

    public async Task<IpatoolAccountInfo?> GetStoredAccountAsync(
        AppleAccountProfile account,
        string vaultPassphrase,
        CancellationToken cancellationToken = default)
    {
        var result = await RunAuthenticatedAsync(
            account,
            ["--format", "json", "auth", "info"],
            vaultPassphrase,
            cancellationToken: cancellationToken);
        if (result.MissingPromptKey == "vault-passphrase")
        {
            throw new InvalidOperationException(
                "IPA Bridge could not unlock the protected local account session. Reconnect this profile once and try again.");
        }

        if (result.IsSuccess && IpatoolJsonParser.HasSuccess(result.Output))
        {
            return IpatoolJsonParser.ParseAccountInfo(result.Output);
        }

        var error = IpatoolJsonParser.FindError(result.Output);
        if (IsAccountNotFoundError(error))
        {
            return null;
        }

        throw new InvalidOperationException(
            $"Failed to check the saved sign-in: " +
            $"{error ?? "ipatool did not return a recognizable account status."}");
    }

    public async Task<string> DownloadAsync(
        AppleAccountProfile account,
        StoreApp app,
        string outputDirectory,
        string vaultPassphrase,
        string? externalVersionIdentifier = null,
        Action<string>? outputReceived = null,
        CancellationToken cancellationToken = default)
    {
        await EnsureAccountMatchesAsync(account, vaultPassphrase, cancellationToken);
        outputDirectory = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(outputDirectory);
        var fileName = $"{SanitizeFileName(app.BundleIdentifier)}_{DateTime.Now:yyyyMMdd_HHmmss}.ipa";
        var requestedPath = Path.Combine(outputDirectory, fileName);
        var stagingRoot = Path.Combine(outputDirectory, ".ipa-bridge-staging");
        var stagingDirectory = Path.Combine(stagingRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stagingDirectory);
        var stagedPath = Path.Combine(stagingDirectory, fileName);
        try
        {
            var arguments = new List<string>
            {
                "--format",
                "json",
                "download",
                "--bundle-identifier",
                app.BundleIdentifier,
                "--output",
                stagedPath,
                "--purchase"
            };
            if (!string.IsNullOrWhiteSpace(externalVersionIdentifier))
            {
                arguments.Add("--external-version-id");
                arguments.Add(externalVersionIdentifier);
            }

            var result = await RunAuthenticatedAsync(
                account,
                arguments,
                vaultPassphrase,
                outputReceived,
                cancellationToken);
            EnsureSuccess(result, "Download failed");

            var reportedPath = IpatoolJsonParser.FindDownloadedPath(result.Output);
            if (reportedPath is not null && !PathsEqual(reportedPath, stagedPath))
            {
                throw new InvalidDataException(
                    "ipatool returned a download path outside the isolated staging directory.");
            }

            if (!File.Exists(stagedPath))
            {
                throw new InvalidDataException("ipatool did not produce the requested IPA file.");
            }

            File.Move(stagedPath, requestedPath);
            return requestedPath;
        }
        finally
        {
            TryDeleteDownloadStagingDirectory(stagingDirectory, stagingRoot);
        }
    }

    public async Task<IReadOnlyList<StoreAppVersion>> ListVersionsAsync(
        AppleAccountProfile account,
        StoreApp app,
        string vaultPassphrase,
        IProgress<StoreVersionLookupProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await EnsureAccountMatchesAsync(account, vaultPassphrase, cancellationToken);
        var listArguments = new List<string>
        {
            "--format",
            "json",
            "list-versions"
        };
        AppendAppIdentifier(listArguments, app);
        var result = await RunAuthenticatedAsync(
            account,
            listArguments,
            vaultPassphrase,
            cancellationToken: cancellationToken);
        EnsureSuccess(result, "Failed to read version history");
        var identifiers = IpatoolJsonParser.ParseVersionIdentifiers(result.Output);
        if (identifiers.Count == 0)
        {
            return [];
        }

        var versions = new StoreAppVersion[identifiers.Count];
        var completed = 0;
        using var throttle = new SemaphoreSlim(VersionMetadataConcurrency);
        var tasks = identifiers.Select(async (identifier, index) =>
        {
            await throttle.WaitAsync(cancellationToken);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var cacheKey = BuildVersionMetadataCacheKey(account, app, identifier);
                if (!_versionMetadataCache.TryGetValue(cacheKey, out var version))
                {
                    try
                    {
                        version = await GetVersionMetadataAsync(
                            account,
                            app,
                            identifier,
                            vaultPassphrase,
                            cancellationToken);
                        _versionMetadataCache.TryAdd(cacheKey, version);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        version = StoreAppVersion.Unresolved(identifier, exception.Message);
                    }
                }

                versions[index] = version;
                var completedCount = Interlocked.Increment(ref completed);
                progress?.Report(new StoreVersionLookupProgress(
                    index,
                    completedCount,
                    identifiers.Count,
                    version));
            }
            finally
            {
                throttle.Release();
            }
        }).ToArray();

        await Task.WhenAll(tasks);
        return versions;
    }

    private async Task<StoreAppVersion> GetVersionMetadataAsync(
        AppleAccountProfile account,
        StoreApp app,
        string externalVersionIdentifier,
        string vaultPassphrase,
        CancellationToken cancellationToken)
    {
        var arguments = new List<string>
        {
            "--format",
            "json",
            "get-version-metadata"
        };
        AppendAppIdentifier(arguments, app);
        arguments.Add("--external-version-id");
        arguments.Add(externalVersionIdentifier);

        var result = await RunAuthenticatedAsync(
            account,
            arguments,
            vaultPassphrase,
            cancellationToken: cancellationToken);
        EnsureSuccess(result, $"Failed to read metadata for version {externalVersionIdentifier}");
        return IpatoolJsonParser.ParseVersionMetadata(result.Output, externalVersionIdentifier);
    }

    private async Task<ConPtyResult> RunAuthenticatedAsync(
        AppleAccountProfile account,
        IEnumerable<string> arguments,
        string vaultPassphrase,
        Action<string>? outputReceived = null,
        CancellationToken cancellationToken = default)
    {
        var prompts = new[]
        {
            new ConPtyPrompt("vault-passphrase", "enter passphrase to unlock", vaultPassphrase)
        };
        return await _conPtyProcessRunner.RunAsync(
            RequireExecutable(),
            arguments,
            prompts,
            outputReceived,
            GetAccountEnvironment(account),
            cancellationToken);
    }

    private async Task EnsureAccountMatchesAsync(
        AppleAccountProfile account,
        string vaultPassphrase,
        CancellationToken cancellationToken)
    {
        var accountInfo = await GetStoredAccountAsync(
            account,
            vaultPassphrase,
            cancellationToken);
        if (accountInfo is null)
        {
            throw new IpatoolAccountSessionException(
                $"Sign in to {account.Email} before using its App Store region.");
        }

        if (!string.Equals(
                accountInfo.Email,
                account.Email,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new IpatoolAccountSessionException(
                $"The selected profile is {account.Email}, but its isolated ipatool session " +
                $"contains {accountInfo.Email}. Reconnect the selected account before continuing.");
        }
    }

    private IReadOnlyDictionary<string, string> GetAccountEnvironment(
        AppleAccountProfile account)
    {
        var homeDirectory = GetAccountHomeDirectory(account.Id);
        Directory.CreateDirectory(homeDirectory);
        return BuildAccountEnvironment(homeDirectory);
    }

    private string GetAccountHomeDirectory(string accountId)
    {
        if (!Guid.TryParseExact(accountId, "N", out _))
        {
            throw new ArgumentException("The Apple Account profile ID is invalid.", nameof(accountId));
        }

        return Path.GetFullPath(Path.Combine(_accountSessionsRoot, accountId));
    }

    private string GetSessionRemovalStagingRoot()
    {
        return Path.GetFullPath(Path.Combine(
            _accountSessionsRoot,
            SessionRemovalStagingDirectoryName));
    }

    private void ValidateStagedLocalAccountSessionRemoval(
        StagedLocalAccountSessionRemoval removal)
    {
        var expectedOriginalDirectory = GetAccountHomeDirectory(removal.AccountId);
        var stagingRoot = Path.TrimEndingDirectorySeparator(
            GetSessionRemovalStagingRoot());
        var stagedDirectory = Path.GetFullPath(removal.StagedDirectory);
        if (!string.Equals(
                expectedOriginalDirectory,
                Path.GetFullPath(removal.OriginalDirectory),
                StringComparison.OrdinalIgnoreCase) ||
            !stagedDirectory.StartsWith(
                stagingRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The staged Apple Account session path is outside the managed account directory.");
        }
    }

    private void TryDeleteEmptySessionRemovalStagingRoot()
    {
        try
        {
            var stagingRoot = GetSessionRemovalStagingRoot();
            if (Directory.Exists(stagingRoot) &&
                !Directory.EnumerateFileSystemEntries(stagingRoot).Any())
            {
                Directory.Delete(stagingRoot);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A later startup recovery pass will retry harmless empty-directory cleanup.
        }
    }

    internal static IReadOnlyDictionary<string, string> BuildAccountEnvironment(
        string homeDirectory)
    {
        homeDirectory = Path.GetFullPath(homeDirectory);
        var homeDrive = Path.GetPathRoot(homeDirectory)?.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        if (string.IsNullOrWhiteSpace(homeDrive) ||
            homeDirectory.Length <= homeDrive.Length)
        {
            throw new ArgumentException(
                "The Apple Account session directory must be an absolute local path.",
                nameof(homeDirectory));
        }

        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["HOME"] = homeDirectory,
            ["USERPROFILE"] = homeDirectory,
            ["HOMEDRIVE"] = homeDrive,
            ["HOMEPATH"] = homeDirectory[homeDrive.Length..]
        };
    }

    private static void EnsureSuccess(ConPtyResult result, string operation)
    {
        if (result.MissingPromptKey == "vault-passphrase")
        {
            throw new InvalidOperationException(
                "IPA Bridge could not unlock the protected local account session. Reconnect this profile once and try again.");
        }

        var error = IpatoolJsonParser.FindError(result.Output);
        if (!result.IsSuccess || error is not null)
        {
            throw new InvalidOperationException(
                $"{operation}: {error ?? "ipatool returned an error status."}");
        }
    }

    internal static bool IsAccountNotFoundError(string? error)
    {
        return error?.Contains("not found", StringComparison.OrdinalIgnoreCase) == true ||
               error?.Contains("could not be found", StringComparison.OrdinalIgnoreCase) == true ||
               error?.Contains("no account", StringComparison.OrdinalIgnoreCase) == true;
    }

    private string RequireExecutable()
    {
        return _toolLocationService.ResolveIpatool()
               ?? throw new FileNotFoundException("ipatool.exe has not been installed or selected.");
    }

    private static void AppendAppIdentifier(ICollection<string> arguments, StoreApp app)
    {
        if (app.Id > 0)
        {
            arguments.Add("--app-id");
            arguments.Add(app.Id.ToString(CultureInfo.InvariantCulture));
            return;
        }

        if (!string.IsNullOrWhiteSpace(app.BundleIdentifier))
        {
            arguments.Add("--bundle-identifier");
            arguments.Add(app.BundleIdentifier);
            return;
        }

        throw new ArgumentException(
            "The app has neither an App Store ID nor a bundle identifier.",
            nameof(app));
    }

    private static string BuildVersionMetadataCacheKey(
        AppleAccountProfile account,
        StoreApp app,
        string externalVersionIdentifier)
    {
        var appIdentifier = app.Id > 0
            ? $"id:{app.Id.ToString(CultureInfo.InvariantCulture)}"
            : $"bundle:{app.BundleIdentifier}";
        return $"account:{account.Id}|{appIdentifier}|version:{externalVersionIdentifier}";
    }

    private static string SanitizeFileName(string value)
    {
        var invalidCharacters = Path.GetInvalidFileNameChars();
        return string.Concat(value.Select(character =>
            invalidCharacters.Contains(character) ? '_' : character));
    }

    private static bool PathsEqual(string first, string second)
    {
        return string.Equals(
            Path.GetFullPath(first),
            Path.GetFullPath(second),
            StringComparison.OrdinalIgnoreCase);
    }

    private static void TryDeleteDownloadStagingDirectory(
        string stagingDirectory,
        string stagingRoot)
    {
        try
        {
            var fullDirectory = Path.GetFullPath(stagingDirectory);
            var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(stagingRoot));
            var rootPrefix = fullRoot + Path.DirectorySeparatorChar;
            if (!fullDirectory.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (Directory.Exists(fullDirectory))
            {
                Directory.Delete(fullDirectory, recursive: true);
            }

            if (Directory.Exists(fullRoot) && !Directory.EnumerateFileSystemEntries(fullRoot).Any())
            {
                Directory.Delete(fullRoot);
            }
        }
        catch
        {
            // Residue remains isolated below the staging directory and is never scanned as a library IPA.
        }
    }

    [GeneratedRegex(@"v?\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();
}
