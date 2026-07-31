using System.Formats.Tar;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using IPABridge.Infrastructure;
using IPABridge.Models;

namespace IPABridge.Services;

public sealed class ToolBootstrapService : IDisposable
{
    private const string IpatoolVersion = "v2.3.0";
    private const string IpatoolAmd64Sha256 =
        "eaf208f0fee964a82f14f8eda60c4b0568fe555ad97729bb74277d3d7c0e4d54";
    private const string IpatoolArm64Sha256 =
        "690d94332802f5fca604cce29ac9762089c7271c30a68e64eeb462c605e1fa07";
    private const string IpatoolReleaseBaseUrl =
        "https://github.com/majd/ipatool/releases/download/v2.3.0";
    private const string IdeviceToolsVersion = "v0.1.65";
    private const string IdeviceToolsSha256 = "fbae49be4ca8fbbab716121a5a6d29445ec8b9fd4b5f01c0300bd912fae88356";
    private const string IdeviceToolsDownloadUrl =
        "https://github.com/jkcoxson/idevice/releases/download/v0.1.65/idevice-tools-windows-v0.1.65.zip";

    private readonly HttpClient _httpClient;
    private readonly ConfigurationService _configurationService;
    private readonly ProcessRunner _processRunner = new();

    public ToolBootstrapService(ConfigurationService configurationService)
    {
        _configurationService = configurationService;
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(10)
        };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("IPA-Bridge/0.1");
        _httpClient.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    public async Task<string> InstallIpatoolAsync(
        IProgress<ToolInstallationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        AppPaths.EnsureDirectories();
        var package = GetPinnedIpatoolPackage(RuntimeInformation.OSArchitecture);
        progress?.Report(new ToolInstallationProgress(
            $"Downloading ipatool {package.Version}…",
            0.05));

        var stagingDirectory = CreateStagingDirectory();
        string? installationDirectory = null;
        var previousIpatoolPath = _configurationService.Current.IpatoolPath;
        var installationCommitted = false;
        try
        {
            var archivePath = Path.Combine(stagingDirectory, package.ArchiveName);
            await DownloadAsync(
                package.DownloadUrl,
                archivePath,
                0.08,
                0.73,
                progress,
                cancellationToken);

            progress?.Report(new ToolInstallationProgress(
                "Verifying the pinned ipatool SHA-256 checksum…",
                0.76));
            // The reviewed, embedded checksum must pass before any downloaded code is extracted.
            await VerifySha256Async(archivePath, package.Sha256, cancellationToken);
            var extractedDirectory = Path.Combine(stagingDirectory, "extracted");
            Directory.CreateDirectory(extractedDirectory);
            await using (var archiveStream = File.OpenRead(archivePath))
            await using (var gzipStream = new GZipStream(archiveStream, CompressionMode.Decompress))
            {
                TarFile.ExtractToDirectory(gzipStream, extractedDirectory, overwriteFiles: true);
            }

            var executable = Directory.EnumerateFiles(
                    extractedDirectory,
                    "ipatool*.exe",
                    SearchOption.AllDirectories)
                .FirstOrDefault()
                ?? throw new InvalidDataException(
                    "ipatool.exe was not found in the official archive.");

            installationDirectory = CreateVersionedInstallDirectory(
                AppPaths.IpatoolDirectory,
                package.Version);
            Directory.CreateDirectory(installationDirectory);
            var destination = Path.Combine(installationDirectory, "ipatool.exe");
            File.Copy(executable, destination, overwrite: true);
            var versionCheck = await _processRunner.RunAsync(
                destination,
                ["--version"],
                installationDirectory,
                cancellationToken: cancellationToken);
            var expectedVersion = package.Version.TrimStart('v');
            if (!versionCheck.IsSuccess ||
                !versionCheck.CombinedOutput.Contains(expectedVersion, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "The extracted ipatool version check failed; the active tool was not changed.");
            }

            await WriteSourceManifestAsync(
                installationDirectory,
                "majd/ipatool",
                package.Version,
                package.DownloadUrl,
                package.Sha256,
                cancellationToken);

            _configurationService.Current.IpatoolPath = destination;
            await _configurationService.SaveAsync();
            installationCommitted = true;
            progress?.Report(new ToolInstallationProgress($"ipatool {package.Version} is ready", 1));
            return destination;
        }
        catch
        {
            if (!installationCommitted)
            {
                _configurationService.Current.IpatoolPath = previousIpatoolPath;
            }

            throw;
        }
        finally
        {
            TryDeleteDirectory(stagingDirectory, AppPaths.TemporaryDirectory);
            if (!installationCommitted && installationDirectory is not null)
            {
                TryDeleteDirectory(installationDirectory, AppPaths.IpatoolDirectory);
            }
        }
    }

    public async Task<string> InstallIdeviceToolsAsync(
        IProgress<ToolInstallationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        AppPaths.EnsureDirectories();
        progress?.Report(new ToolInstallationProgress(
            $"Downloading idevice-tools {IdeviceToolsVersion}…",
            0.05));

        var stagingDirectory = CreateStagingDirectory();
        string? installationDirectory = null;
        var previousDeviceToolsDirectory = _configurationService.Current.DeviceToolsDirectory;
        var installationCommitted = false;
        try
        {
            var archivePath = Path.Combine(stagingDirectory, "idevice-tools.zip");
            await DownloadAsync(
                IdeviceToolsDownloadUrl,
                archivePath,
                0.05,
                0.78,
                progress,
                cancellationToken);

            progress?.Report(new ToolInstallationProgress(
                "Verifying the device tools SHA-256 checksum…",
                0.82));
            // The pinned archive must match the reviewed checksum before extraction.
            await VerifySha256Async(archivePath, IdeviceToolsSha256, cancellationToken);

            var extractedDirectory = Path.Combine(stagingDirectory, "extracted");
            ZipFile.ExtractToDirectory(archivePath, extractedDirectory, overwriteFiles: true);
            var modernTool = Directory.EnumerateFiles(
                    extractedDirectory,
                    "idevice-tools.exe",
                    SearchOption.AllDirectories)
                .FirstOrDefault()
                ?? throw new InvalidDataException(
                    "idevice-tools.exe was not found in the device tools archive.");
            var identifierTool = Directory.EnumerateFiles(
                    extractedDirectory,
                    "idevice_id.exe",
                    SearchOption.AllDirectories)
                .FirstOrDefault()
                ?? throw new InvalidDataException(
                    "idevice_id.exe was not found in the device tools archive.");

            installationDirectory = CreateVersionedInstallDirectory(
                AppPaths.DeviceToolsDirectory,
                IdeviceToolsVersion);
            Directory.CreateDirectory(installationDirectory);
            foreach (var executable in Directory.EnumerateFiles(
                         Path.GetDirectoryName(modernTool)!,
                         "*.exe",
                         SearchOption.TopDirectoryOnly))
            {
                File.Copy(
                    executable,
                    Path.Combine(installationDirectory, Path.GetFileName(executable)),
                    overwrite: true);
            }

            if (!File.Exists(Path.Combine(installationDirectory, Path.GetFileName(identifierTool))))
            {
                File.Copy(
                    identifierTool,
                    Path.Combine(installationDirectory, Path.GetFileName(identifierTool)),
                    overwrite: true);
            }

            if (!File.Exists(Path.Combine(installationDirectory, "idevice-tools.exe")) ||
                !File.Exists(Path.Combine(installationDirectory, "idevice_id.exe")))
            {
                throw new InvalidDataException(
                    "The copied device tools are incomplete; the active tool version was not changed.");
            }

            var versionCheck = await _processRunner.RunAsync(
                Path.Combine(installationDirectory, "idevice-tools.exe"),
                ["--version"],
                installationDirectory,
                cancellationToken: cancellationToken);
            if (!versionCheck.IsSuccess ||
                !versionCheck.CombinedOutput.Contains(
                    IdeviceToolsVersion.TrimStart('v'),
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "The device tools version check failed; the active tool version was not changed.");
            }

            await WriteSourceManifestAsync(
                installationDirectory,
                "jkcoxson/idevice",
                IdeviceToolsVersion,
                IdeviceToolsDownloadUrl,
                IdeviceToolsSha256,
                cancellationToken);

            _configurationService.Current.DeviceToolsDirectory = installationDirectory;
            await _configurationService.SaveAsync();
            installationCommitted = true;
            progress?.Report(new ToolInstallationProgress(
                $"idevice-tools {IdeviceToolsVersion} is ready",
                1));
            return installationDirectory;
        }
        catch
        {
            if (!installationCommitted)
            {
                _configurationService.Current.DeviceToolsDirectory = previousDeviceToolsDirectory;
            }

            throw;
        }
        finally
        {
            TryDeleteDirectory(stagingDirectory, AppPaths.TemporaryDirectory);
            if (!installationCommitted && installationDirectory is not null)
            {
                TryDeleteDirectory(installationDirectory, AppPaths.DeviceToolsDirectory);
            }
        }
    }

    public void Dispose() => _httpClient.Dispose();

    public static PinnedToolPackage GetPinnedIpatoolPackage(Architecture architecture)
    {
        var architectureName = architecture switch
        {
            Architecture.X64 => "amd64",
            Architecture.Arm64 => "arm64",
            _ => throw new PlatformNotSupportedException(
                $"ipatool {IpatoolVersion} is not available for Windows {architecture}.")
        };
        var sha256 = architecture == Architecture.Arm64
            ? IpatoolArm64Sha256
            : IpatoolAmd64Sha256;
        var archiveName = $"ipatool-{IpatoolVersion.TrimStart('v')}-windows-{architectureName}.tar.gz";
        return new PinnedToolPackage(
            IpatoolVersion,
            archiveName,
            $"{IpatoolReleaseBaseUrl}/{archiveName}",
            sha256);
    }

    private async Task DownloadAsync(
        string url,
        string destination,
        double startPercentage,
        double endPercentage,
        IProgress<ToolInstallationProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(
            url,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        var contentLength = response.Content.Headers.ContentLength;
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var target = new FileStream(
            destination,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            81920,
            useAsync: true);
        var buffer = new byte[81920];
        long totalRead = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            totalRead += read;
            if (contentLength > 0)
            {
                var ratio = (double)totalRead / contentLength.Value;
                var percentage = startPercentage + (endPercentage - startPercentage) * ratio;
                progress?.Report(new ToolInstallationProgress("Downloading component…", percentage));
            }
        }
    }

    private static async Task VerifySha256Async(
        string filePath,
        string expectedChecksum,
        CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(filePath);
        var checksum = await SHA256.HashDataAsync(stream, cancellationToken);
        var actual = Convert.ToHexString(checksum).ToLowerInvariant();
        if (!string.Equals(actual, expectedChecksum.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Component integrity verification failed. " +
                $"Expected {expectedChecksum.ToLowerInvariant()}, but calculated {actual}.");
        }
    }

    private static string CreateStagingDirectory()
    {
        var directory = Path.Combine(AppPaths.TemporaryDirectory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static string CreateVersionedInstallDirectory(string rootDirectory, string version)
    {
        var safeVersion = string.Concat(version.Select(character =>
            char.IsLetterOrDigit(character) || character is '.' or '-' or '_'
                ? character
                : '_'));
        return Path.Combine(
            rootDirectory,
            $"{safeVersion}-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}");
    }

    private static async Task WriteSourceManifestAsync(
        string destinationDirectory,
        string repository,
        string version,
        string downloadUrl,
        string sha256,
        CancellationToken cancellationToken)
    {
        var manifest = new
        {
            repository,
            version,
            downloadUrl,
            sha256 = sha256.ToLowerInvariant(),
            installedAtUtc = DateTime.UtcNow
        };
        var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(
            Path.Combine(destinationDirectory, "SOURCE.json"),
            json,
            cancellationToken);
    }

    private static void TryDeleteDirectory(string directory, string allowedRoot)
    {
        try
        {
            var fullPath = Path.GetFullPath(directory);
            var rootPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(allowedRoot)) +
                           Path.DirectorySeparatorChar;
            if (fullPath.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase) &&
                Directory.Exists(fullPath))
            {
                Directory.Delete(fullPath, recursive: true);
            }
        }
        catch
        {
            // Cleanup is best-effort. A failed installation is never selected in configuration.
        }
    }

    public sealed record PinnedToolPackage(
        string Version,
        string ArchiveName,
        string DownloadUrl,
        string Sha256);
}
