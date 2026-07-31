namespace IPABridge.Infrastructure;

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool IsSuccess => ExitCode == 0;

    public string CombinedOutput =>
        string.Join(Environment.NewLine, new[] { StandardOutput, StandardError }
            .Where(value => !string.IsNullOrWhiteSpace(value)));
}
