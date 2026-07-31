namespace IPABridge.Models;

public sealed class LocalIpa
{
    public string FilePath { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public long SizeBytes { get; init; }

    public DateTime ModifiedAt { get; init; }

    public string SizeLabel
    {
        get
        {
            string[] units = ["B", "KB", "MB", "GB"];
            var value = (double)SizeBytes;
            var unit = 0;
            while (value >= 1024 && unit < units.Length - 1)
            {
                value /= 1024;
                unit++;
            }

            return $"{value:0.#} {units[unit]}";
        }
    }
}
