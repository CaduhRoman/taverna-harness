using System.Diagnostics;

namespace Taverna.Core.Git;

public sealed record GitCommandResult(int ExitCode, string Output, string Error)
{
    public void ThrowIfFailed(string operation)
    {
        if (ExitCode != 0)
            throw new GitCommandException($"Git {operation} falhou ({ExitCode}): {Error.Trim()} {Output.Trim()}");
    }
}

public sealed class GitCommandException(string message) : Exception(message);

internal static class GitProcess
{
    public static async Task<GitCommandResult> RunAsync(string directory, CancellationToken ct, params string[] args)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = directory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in args) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        if (!process.Start()) throw new GitCommandException("Não foi possível iniciar Git.");
        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            return new GitCommandResult(process.ExitCode, await stdout, await stderr);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            throw;
        }
    }
}
