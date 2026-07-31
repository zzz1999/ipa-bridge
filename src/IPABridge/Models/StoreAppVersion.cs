namespace IPABridge.Models;

public sealed class StoreAppVersion
{
    public string ExternalVersionIdentifier { get; init; } = string.Empty;

    public string DisplayVersion { get; init; } = string.Empty;

    public DateTimeOffset? ReleaseDate { get; init; }

    public string? MetadataError { get; init; }

    public bool HasMetadata =>
        !string.IsNullOrWhiteSpace(DisplayVersion) && ReleaseDate is not null;

    public bool RequiresLicense =>
        MetadataError?.Contains("license is required", StringComparison.OrdinalIgnoreCase) == true;

    public string VersionLabel => string.IsNullOrWhiteSpace(DisplayVersion)
        ? "Version unavailable"
        : DisplayVersion.StartsWith("v", StringComparison.OrdinalIgnoreCase)
            ? DisplayVersion
            : $"v{DisplayVersion}";

    public string ReleaseDateLabel => ReleaseDate is null
        ? "Release date unavailable"
        : $"Released {ReleaseDate:yyyy-MM-dd}";

    public string IdentifierLabel => $"Version identifier {ExternalVersionIdentifier}";

    public static StoreAppVersion Unresolved(string externalVersionIdentifier, string error)
    {
        return new StoreAppVersion
        {
            ExternalVersionIdentifier = externalVersionIdentifier,
            MetadataError = error
        };
    }
}

public sealed record StoreVersionLookupProgress(
    int Index,
    int Completed,
    int Total,
    StoreAppVersion Version);
