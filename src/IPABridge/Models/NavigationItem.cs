using IPABridge.Infrastructure;

namespace IPABridge.Models;

public sealed class NavigationItem : ObservableObject
{
    private bool _isActive;

    public required string Label { get; init; }

    public required string IconData { get; init; }

    public required NavigationPage Page { get; init; }

    public bool IsActive
    {
        get => _isActive;
        set => SetProperty(ref _isActive, value);
    }
}
