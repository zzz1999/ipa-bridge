namespace IPABridge.Infrastructure;

public sealed record ConPtyResult(int ExitCode, string Output, string? MissingPromptKey)
{
    public bool IsSuccess => ExitCode == 0 && MissingPromptKey is null;
}
