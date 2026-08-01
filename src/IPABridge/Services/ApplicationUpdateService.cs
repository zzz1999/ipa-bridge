using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;

namespace IPABridge.Services;

public sealed record ApplicationUpdateResult(
    string ReleaseName,
    string ReleaseTag,
    string ReleaseUrl,
    string ReleaseCommit,
    DateTimeOffset PublishedAt,
    string CurrentRevision,
    bool IsUpdateAvailable,
    bool CanCompareBuilds);

public sealed class ApplicationUpdateService
{
    public const string ReleasesApiUrl =
        "https://api.github.com/repos/zzz1999/ipa-bridge/releases?per_page=30";
    private const string ReleasePathPrefix = "/zzz1999/ipa-bridge/releases/";
    private const string ExecutableAssetName = "IPA-Bridge.exe";
    private readonly HttpClient _httpClient;
    private readonly string _currentRevision;

    public ApplicationUpdateService(
        HttpClient? httpClient = null,
        string? currentRevision = null)
    {
        _httpClient = httpClient ?? new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(20)
        };
        if (!_httpClient.DefaultRequestHeaders.UserAgent.Any())
        {
            _httpClient.DefaultRequestHeaders.UserAgent.Add(
                new ProductInfoHeaderValue("IPA-Bridge", "0.1"));
        }

        _currentRevision = NormalizeRevision(currentRevision ?? ReadCurrentRevision());
    }

    public string CurrentBuildLabel => IsComparableRevision(_currentRevision)
        ? $"Build {_currentRevision[..7]}"
        : "Development build";

    public async Task<ApplicationUpdateResult> CheckAsync(
        CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync(ReleasesApiUrl, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var content = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(
            content,
            cancellationToken: cancellationToken);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("GitHub did not return a release list.");
        }

        foreach (var release in document.RootElement.EnumerateArray())
        {
            if (ReadBoolean(release, "draft") ||
                ReadBoolean(release, "prerelease") ||
                !HasExecutableAsset(release))
            {
                continue;
            }

            var releaseUrl = ReadRequiredString(release, "html_url");
            ValidateReleaseUrl(releaseUrl);
            var releaseCommit = NormalizeRevision(ReadRequiredString(release, "target_commitish"));
            var canCompareBuilds = IsComparableRevision(_currentRevision) &&
                                   IsComparableRevision(releaseCommit);
            return new ApplicationUpdateResult(
                ReadRequiredString(release, "name"),
                ReadRequiredString(release, "tag_name"),
                releaseUrl,
                releaseCommit,
                ReadRequiredDate(release, "published_at"),
                _currentRevision,
                canCompareBuilds &&
                !string.Equals(_currentRevision, releaseCommit, StringComparison.OrdinalIgnoreCase),
                canCompareBuilds);
        }

        throw new InvalidDataException(
            "GitHub did not return a published IPA Bridge release containing IPA-Bridge.exe.");
    }

    private static string ReadCurrentRevision()
    {
        var informationalVersion = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        var separator = informationalVersion?.LastIndexOf('+') ?? -1;
        return separator >= 0 && separator + 1 < informationalVersion!.Length
            ? informationalVersion[(separator + 1)..]
            : string.Empty;
    }

    private static bool HasExecutableAsset(JsonElement release)
    {
        return release.TryGetProperty("assets", out var assets) &&
               assets.ValueKind == JsonValueKind.Array &&
               assets.EnumerateArray().Any(asset => string.Equals(
                   ReadOptionalString(asset, "name"),
                   ExecutableAssetName,
                   StringComparison.Ordinal));
    }

    private static void ValidateReleaseUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) ||
            !uri.AbsolutePath.StartsWith(ReleasePathPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("GitHub returned an invalid IPA Bridge release URL.");
        }
    }

    private static string ReadRequiredString(JsonElement element, string propertyName)
    {
        var value = ReadOptionalString(element, propertyName);
        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidDataException($"GitHub release data is missing {propertyName}.")
            : value;
    }

    private static string ReadOptionalString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) &&
               property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? string.Empty
            : string.Empty;
    }

    private static DateTimeOffset ReadRequiredDate(JsonElement element, string propertyName)
    {
        return DateTimeOffset.TryParse(ReadRequiredString(element, propertyName), out var value)
            ? value
            : throw new InvalidDataException($"GitHub release data contains an invalid {propertyName}.");
    }

    private static bool ReadBoolean(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) &&
               property.ValueKind == JsonValueKind.True;
    }

    private static string NormalizeRevision(string value)
    {
        return value.Trim().ToLowerInvariant();
    }

    private static bool IsComparableRevision(string value)
    {
        return value.Length == 40 && value.All(Uri.IsHexDigit);
    }
}
