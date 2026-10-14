using System.Diagnostics;
using System.Runtime.CompilerServices;
using Taverna.Core.Agents;
using Taverna.Core.Git;

namespace Taverna.Core.Tests;

/// <summary>An agent whose behaviour is a test-supplied script.</summary>
internal sealed class ScriptedRunner(AgentKind kind, Func<AgentRequest, CancellationToken, IAsyncEnumerable<AgentEvent>> script,
    bool available = true, bool images = false) : IAgentRunner
{
    public AgentKind Kind => kind;
    public string Name => kind.ToString();
    public AgentCapabilities Capabilities => new([], [], images);
    public bool IsAvailable() => available;
    public List<AgentRequest> Requests { get; } = [];

    public IAsyncEnumerable<AgentEvent> RunAsync(AgentRequest request, CancellationToken ct = default)
    {
        lock (Requests) Requests.Add(request);
        return script(request, ct);
    }

    public static async IAsyncEnumerable<AgentEvent> Reply(string text, int exitCode = 0)
    {
        await Task.Yield();
        yield return new AgentEvent(AgentEventKind.Text, text);
        yield return new AgentEvent(AgentEventKind.Exit, ExitCode: exitCode);
    }

    /// <summary>Writes a file in the working directory, then succeeds.</summary>
    public static async IAsyncEnumerable<AgentEvent> Edit(AgentRequest request, string file, string content)
    {
        await File.WriteAllTextAsync(Path.Combine(request.WorkingDirectory, file), content);
        yield return new AgentEvent(AgentEventKind.Text, $"wrote {file}");
        yield return new AgentEvent(AgentEventKind.Tokens, Usage: new TokenUsage(10, 5));
        yield return new AgentEvent(AgentEventKind.Exit, ExitCode: 0);
    }

    /// <summary>Streams until cancelled, like a long-running CLI.</summary>
    public static async IAsyncEnumerable<AgentEvent> UntilCancelled(TaskCompletionSource started, [EnumeratorCancellation] CancellationToken ct)
    {
        yield return new AgentEvent(AgentEventKind.Text, "partial ");
        started.TrySetResult();
        var cancelled = false;
        try { await Task.Delay(Timeout.Infinite, ct); }
        catch (OperationCanceledException) { cancelled = true; }
        if (cancelled) yield return new AgentEvent(AgentEventKind.Cancelled);
    }
}

internal sealed class TemporaryGitRepository : IDisposable
{
    public string Root { get; }
    public string WorktreeRoot { get; }
    public GitService Service { get; }
    private readonly string parent;

    private TemporaryGitRepository()
    {
        parent = Path.Combine(Path.GetTempPath(), "taverna-tests", Guid.NewGuid().ToString("N"));
        Root = Path.Combine(parent, "repository");
        WorktreeRoot = Path.Combine(parent, "worktrees");
        Directory.CreateDirectory(Root);
        Service = new GitService(WorktreeRoot);
    }

    public static async Task<TemporaryGitRepository> CreateAsync()
    {
        var repo = new TemporaryGitRepository();
        try
        {
            await GitAsync(repo.Root, "init", "-b", "main");
            await GitAsync(repo.Root, "config", "user.name", "Taverna Test");
            await GitAsync(repo.Root, "config", "user.email", "test@taverna.local");
            await File.WriteAllTextAsync(Path.Combine(repo.Root, ".gitignore"), ".taverna/\n");
            await File.WriteAllTextAsync(Path.Combine(repo.Root, "shared.txt"), "first\n");
            await GitAsync(repo.Root, "add", ".");
            await GitAsync(repo.Root, "commit", "-m", "initial");
            return repo;
        }
        catch { repo.Dispose(); throw; }
    }

    public static async Task<string> GitAsync(string directory, params string[] args)
    {
        var info = new ProcessStartInfo("git")
        {
            WorkingDirectory = directory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in args) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Git unavailable.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new Exception($"git {string.Join(' ', args)}: {await error}");
        return (await output).Trim();
    }

    public void Dispose()
    {
        try
        {
            // Git marks pack files read-only, which blocks recursive delete on Windows.
            foreach (var file in Directory.EnumerateFiles(parent, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(parent, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
