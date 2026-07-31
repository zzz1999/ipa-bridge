using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using IPABridge.Infrastructure;
using IPABridge.Models;

namespace IPABridge.Services;

public sealed partial class IpatoolService
{
    private const int VersionMetadataConcurrency = 2;
    private readonly ToolLocationService _toolLocationService;
    private readonly ProcessRunner _processRunner;
    private readonly ConPtyProcessRunner _conPtyProcessRunner;
    private readonly ConcurrentDictionary<string, StoreAppVersion> _versionMetadataCache =
        new(StringComparer.Ordinal);

    public IpatoolService(
        ToolLocationService toolLocationService,
        ProcessRunner processRunner,
        ConPtyProcessRunner conPtyProcessRunner)
    {
        _toolLocationService = toolLocationService;
        _processRunner = processRunner;
        _conPtyProcessRunner = conPtyProcessRunner;
    }

    public bool IsAvailable => _toolLocationService.ResolveIpatool() is not null;

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
        string email,
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
            ["--format", "json", "auth", "login", "--email", email],
            prompts,
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
                "Enter the passphrase that protects the local ipatool credential vault.");
        }

        if (IpatoolJsonParser.HasSuccessfulLogin(result.Output))
        {
            return new IpatoolLoginResult(true, false, "Apple account connected.");
        }

        var error = IpatoolJsonParser.FindError(result.Output)
                    ?? "Sign-in did not return a success status. Verify the account details and try again.";
        return new IpatoolLoginResult(false, false, error);
    }

    public async Task<IReadOnlyList<StoreApp>> SearchAsync(
        string query,
        string vaultPassphrase,
        CancellationToken cancellationToken = default)
    {
        var result = await RunAuthenticatedAsync(
            ["--format", "json", "search", query, "--limit", "25", "--platform", "iphone"],
            vaultPassphrase,
            cancellationToken: cancellationToken);
        EnsureSuccess(result, "Search failed");
        return IpatoolJsonParser.ParseSearchResults(result.Output);
    }

    public async Task<bool> HasStoredAccountAsync(
        string vaultPassphrase,
        CancellationToken cancellationToken = default)
    {
        var result = await RunAuthenticatedAsync(
            ["--format", "json", "auth", "info"],
            vaultPassphrase,
            cancellationToken: cancellationToken);
        if (result.MissingPromptKey == "vault-passphrase")
        {
            throw new InvalidOperationException(
                "The local credential-vault passphrase is required to check the account.");
        }

        if (result.IsSuccess && IpatoolJsonParser.HasSuccess(result.Output))
        {
            return true;
        }

        var error = IpatoolJsonParser.FindError(result.Output);
        if (error?.Contains("not found", StringComparison.OrdinalIgnoreCase) == true ||
            error?.Contains("no account", StringComparison.OrdinalIgnoreCase) == true)
        {
            return false;
        }

        throw new InvalidOperationException(
            $"Failed to check the saved sign-in: " +
            $"{error ?? "ipatool did not return a recognizable account status."}");
    }

    public async Task<string> DownloadAsync(
        StoreApp app,
        string outputDirectory,
        string vaultPassphrase,
        string? externalVersionIdentifier = null,
        Action<string>? outputReceived = null,
        CancellationToken cancellationToken = default)
    {
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
                "--platform",
                "iphone",
                "--purchase"
            };
            if (!string.IsNullOrWhiteSpace(externalVersionIdentifier))
            {
                arguments.Add("--external-version-id");
                arguments.Add(externalVersionIdentifier);
            }

            var result = await RunAuthenticatedAsync(
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
        StoreApp app,
        string vaultPassphrase,
        IProgress<StoreVersionLookupProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var listArguments = new List<string>
        {
            "--format",
            "json",
            "list-versions"
        };
        AppendAppIdentifier(listArguments, app);
        var result = await RunAuthenticatedAsync(
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
                var cacheKey = BuildVersionMetadataCacheKey(app, identifier);
                if (!_versionMetadataCache.TryGetValue(cacheKey, out var version))
                {
                    try
                    {
                        version = await GetVersionMetadataAsync(
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
            arguments,
            vaultPassphrase,
            cancellationToken: cancellationToken);
        EnsureSuccess(result, $"Failed to read metadata for version {externalVersionIdentifier}");
        return IpatoolJsonParser.ParseVersionMetadata(result.Output, externalVersionIdentifier);
    }

    private async Task<ConPtyResult> RunAuthenticatedAsync(
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
            cancellationToken);
    }

    private static void EnsureSuccess(ConPtyResult result, string operation)
    {
        if (result.MissingPromptKey == "vault-passphrase")
        {
            throw new InvalidOperationException(
                "The local credential-vault passphrase is required to continue.");
        }

        var error = IpatoolJsonParser.FindError(result.Output);
        if (!result.IsSuccess || error is not null)
        {
            throw new InvalidOperationException(
                $"{operation}: {error ?? "ipatool returned an error status."}");
        }
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
        StoreApp app,
        string externalVersionIdentifier)
    {
        var appIdentifier = app.Id > 0
            ? $"id:{app.Id.ToString(CultureInfo.InvariantCulture)}"
            : $"bundle:{app.BundleIdentifier}";
        return $"{appIdentifier}|version:{externalVersionIdentifier}";
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
