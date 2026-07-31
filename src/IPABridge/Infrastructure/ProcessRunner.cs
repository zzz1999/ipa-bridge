using System.Diagnostics;
using System.Text;

namespace IPABridge.Infrastructure;

public sealed class ProcessRunner
{
    public async Task<ProcessResult> RunAsync(
        string executablePath,
        IEnumerable<string> arguments,
        string? workingDirectory = null,
        Action<string>? outputReceived = null,
        IReadOnlyDictionary<string, string>? environment = null,
        CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = workingDirectory ?? Path.GetDirectoryName(executablePath) ?? AppContext.BaseDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (environment is not null)
        {
            foreach (var pair in environment)
            {
                startInfo.Environment[pair.Key] = pair.Value;
            }
        }

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var output = new StringBuilder();
        var error = new StringBuilder();

        process.OutputDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data is null)
            {
                return;
            }

            output.AppendLine(eventArgs.Data);
            outputReceived?.Invoke(eventArgs.Data);
        };
        process.ErrorDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data is null)
            {
                return;
            }

            error.AppendLine(eventArgs.Data);
            outputReceived?.Invoke(eventArgs.Data);
        };

        if (!process.Start())
        {
            throw new InvalidOperationException($"Could not start {Path.GetFileName(executablePath)}.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(cancellationToken);
            process.WaitForExit();
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            throw;
        }

        return new ProcessResult(process.ExitCode, output.ToString().Trim(), error.ToString().Trim());
    }
}
