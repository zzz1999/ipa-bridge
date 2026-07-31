namespace IPABridge.Models;

public sealed class ActivityEntry
{
    public required string Title { get; init; }

    public required string Detail { get; init; }

    public DateTime CreatedAt { get; init; } = DateTime.Now;

    public bool IsSuccess { get; init; }

    public string TimeLabel => CreatedAt.ToString("HH:mm");
}
