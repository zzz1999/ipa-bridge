namespace IPABridge.Models;

public sealed class StoreApp
{
    public long Id { get; init; }

    public string BundleIdentifier { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string Version { get; init; } = string.Empty;

    public double Price { get; init; }

    public string PriceLabel => Price <= 0 ? "Free" : Price.ToString("0.00");

    public string Monogram
    {
        get
        {
            var value = string.IsNullOrWhiteSpace(Name) ? BundleIdentifier : Name;
            return string.IsNullOrWhiteSpace(value) ? "A" : value[..1].ToUpperInvariant();
        }
    }
}
