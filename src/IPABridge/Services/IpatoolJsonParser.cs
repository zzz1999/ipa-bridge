using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using IPABridge.Models;

namespace IPABridge.Services;

public static partial class IpatoolJsonParser
{
    public static IReadOnlyList<StoreApp> ParseSearchResults(string output)
    {
        foreach (var root in ParseElements(output).Reverse())
        {
            if (!root.TryGetProperty("count", out var count) ||
                count.ValueKind != JsonValueKind.Number ||
                !root.TryGetProperty("apps", out var apps) ||
                apps.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            return apps.EnumerateArray()
                .Select(app => new StoreApp
                {
                    Id = ReadInt64(app, "id"),
                    BundleIdentifier = ReadString(app, "bundleID"),
                    Name = ReadString(app, "name"),
                    Version = ReadString(app, "version"),
                    Price = ReadDouble(app, "price")
                })
                .Where(app => !string.IsNullOrWhiteSpace(app.BundleIdentifier))
                .ToArray();
        }

        throw new InvalidDataException("ipatool did not return recognizable search results.");
    }

    public static bool HasSuccessfulLogin(string output)
    {
        return TryParseAccountInfo(output) is not null;
    }

    public static IpatoolAccountInfo ParseAccountInfo(string output)
    {
        return TryParseAccountInfo(output)
               ?? throw new InvalidDataException(
                   "ipatool did not return recognizable Apple Account information.");
    }

    public static bool HasSuccess(string output)
    {
        return ParseElements(output).Any(root => ReadBoolean(root, "success"));
    }

    private static IpatoolAccountInfo? TryParseAccountInfo(string output)
    {
        foreach (var root in ParseElements(output).Reverse())
        {
            if (!ReadBoolean(root, "success"))
            {
                continue;
            }

            var email = ReadString(root, "email").Trim();
            if (string.IsNullOrWhiteSpace(email))
            {
                continue;
            }

            return new IpatoolAccountInfo(email, ReadString(root, "name").Trim());
        }

        return null;
    }

    public static string? FindDownloadedPath(string output)
    {
        foreach (var root in ParseElements(output).Reverse())
        {
            if (ReadBoolean(root, "success") &&
                root.TryGetProperty("output", out var path) &&
                path.ValueKind == JsonValueKind.String)
            {
                return path.GetString();
            }
        }

        return null;
    }

    public static IReadOnlyList<string> ParseVersionIdentifiers(string output)
    {
        foreach (var root in ParseElements(output).Reverse())
        {
            if (!ReadBoolean(root, "success") ||
                !root.TryGetProperty("externalVersionIdentifiers", out var versions) ||
                versions.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            return versions.EnumerateArray()
                .Select(version => version.ToString())
                .Where(version => !string.IsNullOrWhiteSpace(version))
                .ToArray();
        }

        throw new InvalidDataException("ipatool did not return a recognizable version history.");
    }

    public static StoreAppVersion ParseVersionMetadata(
        string output,
        string expectedExternalVersionIdentifier)
    {
        foreach (var root in ParseElements(output).Reverse())
        {
            if (!ReadBoolean(root, "success") ||
                !string.Equals(
                    ReadString(root, "externalVersionID"),
                    expectedExternalVersionIdentifier,
                    StringComparison.Ordinal))
            {
                continue;
            }

            var displayVersion = ReadString(root, "displayVersion");
            var releaseDateText = ReadString(root, "releaseDate");
            if (string.IsNullOrWhiteSpace(displayVersion) ||
                !DateTimeOffset.TryParse(
                    releaseDateText,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out var releaseDate))
            {
                continue;
            }

            return new StoreAppVersion
            {
                ExternalVersionIdentifier = expectedExternalVersionIdentifier,
                DisplayVersion = displayVersion,
                ReleaseDate = releaseDate
            };
        }

        throw new InvalidDataException(
            $"ipatool did not return recognizable metadata for version identifier " +
            $"{expectedExternalVersionIdentifier}.");
    }

    public static string? FindError(string output)
    {
        foreach (var root in ParseElements(output).Reverse())
        {
            if (root.TryGetProperty("error", out var error) &&
                error.ValueKind == JsonValueKind.String)
            {
                return error.GetString();
            }

            if (root.TryGetProperty("message", out var message) &&
                message.ValueKind == JsonValueKind.String &&
                string.Equals(ReadString(root, "level"), "error", StringComparison.OrdinalIgnoreCase))
            {
                return message.GetString();
            }
        }

        return null;
    }

    private static IReadOnlyList<JsonElement> ParseElements(string output)
    {
        var elements = new List<JsonElement>();
        var sanitized = AnsiEscapePattern().Replace(output, string.Empty);
        foreach (var line in sanitized.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var start = line.IndexOf('{');
            if (start < 0)
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(line[start..]);
                elements.Add(document.RootElement.Clone());
            }
            catch (JsonException)
            {
                // ipatool may interleave terminal messages and JSON; ignore non-JSON lines.
            }
        }

        return elements;
    }

    private static string ReadString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property)
            ? property.ToString()
            : string.Empty;
    }

    private static long ReadInt64(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) &&
               property.TryGetInt64(out var value)
            ? value
            : 0;
    }

    private static double ReadDouble(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) &&
               property.TryGetDouble(out var value)
            ? value
            : 0;
    }

    private static bool ReadBoolean(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) &&
               property.ValueKind == JsonValueKind.True;
    }

    [GeneratedRegex(@"\x1B(?:[@-Z\\-_]|\[[0-?]*[ -/]*[@-~])", RegexOptions.CultureInvariant)]
    private static partial Regex AnsiEscapePattern();
}
