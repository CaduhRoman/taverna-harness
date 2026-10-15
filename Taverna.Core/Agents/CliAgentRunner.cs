using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;

namespace Taverna.Core.Agents;

public abstract class CliAgentRunner : IAgentRunner
{
    public abstract AgentKind Kind { get; }
    public abstract string Name { get; }
    public abstract AgentCapabilities Capabilities { get; }
    protected abstract string Command { get; }
    protected abstract IReadOnlyList<string> Arguments(AgentRequest request, string workingDirectory);
    protected abstract IEnumerable<AgentEvent> ParseStdout(string line, object state);
    protected virtual object NewParseState() => new object();

    /// <summary>Text written to stdin before it is closed. Prompts go here to avoid command-line length and quoting limits.</summary>
    protected virtual string? StandardInput(AgentRequest request) => null;

    /// <summary>True when the prompt itself travels as a process argument.</summary>
    protected virtual bool PromptInArguments => false;

    public bool IsAvailable() => CliLocator.Find(Command) is not null;

    public async IAsyncEnumerable<AgentEvent> RunAsync(AgentRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (Validate(request) is { } problem)
        {
            yield return new AgentEvent(AgentEventKind.Error, problem);
            yield return new AgentEvent(AgentEventKind.Exit, ExitCode: -1);
            yield break;
        }

        var directory = Path.GetFullPath(request.WorkingDirectory);
        var executable = CliLocator.Find(Command)!;
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = directory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in Arguments(request, directory)) start.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = start };
        string? startError = null;
        if (ct.IsCancellationRequested)
        {
            yield return new AgentEvent(AgentEventKind.Cancelled);
            yield break;
        }
        try
        {
            if (!process.Start()) startError = "Process.Start returned false.";
            else ProcessJob.Attach(process);
        }
        catch (Exception ex) { startError = ex.Message; }

        if (startError is not null)
        {
            yield return new AgentEvent(AgentEventKind.Error, $"Could not start {Name}: {startError}");
            yield return new AgentEvent(AgentEventKind.Exit, ExitCode: -1);
            yield break;
        }

        yield return new AgentEvent(AgentEventKind.Status, $"{Name} started (PID {process.Id})");

        var channel = Channel.CreateUnbounded<AgentEvent>(new UnboundedChannelOptions { SingleReader = true });
        var state = NewParseState();
        using var cancel = ct.Register(() => KillTree(process));

        var stdin = WriteInputAsync(process, StandardInput(request));
        var stdout = PumpAsync(process.StandardOutput, line => ParseStdout(line, state), channel.Writer);
        var stderr = PumpAsync(process.StandardError, line => [new AgentEvent(AgentEventKind.Log, line)], channel.Writer);
        var completion = CompleteAsync();
        try
        {
            await foreach (var next in channel.Reader.ReadAllAsync(CancellationToken.None)) yield return next;
            await completion;
        }
        finally
        {
            // Abandoning the enumeration must not leave an orphaned agent editing files.
            KillTree(process);
            await completion;
        }

        async Task CompleteAsync()
        {
            try
            {
                await process.WaitForExitAsync(CancellationToken.None);
                await Task.WhenAll(stdin, stdout, stderr);
                channel.Writer.TryWrite(ct.IsCancellationRequested
                    ? new AgentEvent(AgentEventKind.Cancelled)
                    : new AgentEvent(AgentEventKind.Exit, ExitCode: process.ExitCode));
            }
            catch (Exception ex)
            {
                channel.Writer.TryWrite(new AgentEvent(AgentEventKind.Error, $"{Name} process failure: {ex.Message}"));
                channel.Writer.TryWrite(new AgentEvent(AgentEventKind.Exit, ExitCode: -1));
            }
            finally { channel.Writer.TryComplete(); }
        }
    }

    private string? Validate(AgentRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Prompt)) return "Empty prompt.";
        if (!Directory.Exists(request.WorkingDirectory)) return $"Directory not found: {request.WorkingDirectory}";
        if (!AgentCapabilities.IsSafeToken(request.Model)) return $"Invalid model name: {request.Model}";
        if (!AgentCapabilities.IsSafeToken(request.Effort)) return $"Invalid effort: {request.Effort}";
        if (request.Images.Count > 0 && !Capabilities.SupportsImages) return $"{Name} does not accept image attachments.";
        foreach (var image in request.Images)
            if (!File.Exists(image)) return $"Attachment not found: {image}";

        var executable = CliLocator.Find(Command);
        if (executable is null) return $"{Name} is not installed ({Command} not found on PATH).";
        // cmd.exe re-parses arguments of batch shims, so free text such as a prompt could inject commands.
        if (PromptInArguments && OperatingSystem.IsWindows() &&
            Path.GetExtension(executable).ToLowerInvariant() is ".cmd" or ".bat")
            return $"{Name} resolves to a batch shim ({Path.GetFileName(executable)}); refusing to pass the prompt through cmd.exe.";
        return null;
    }

    private static async Task WriteInputAsync(Process process, string? input)
    {
        try
        {
            if (input is not null) await process.StandardInput.WriteAsync(input);
        }
        catch (IOException) { /* The process exited before reading its input; its exit code reports why. */ }
        finally
        {
            try { process.StandardInput.Close(); }
            catch (IOException) { }
        }
    }

    private async Task PumpAsync(StreamReader reader, Func<string, IEnumerable<AgentEvent>> parser, ChannelWriter<AgentEvent> writer)
    {
        try
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    foreach (var item in parser(line)) writer.TryWrite(item);
                }
                catch (Exception ex)
                {
                    // Unparseable output is kept for the log rather than dropped.
                    writer.TryWrite(new AgentEvent(AgentEventKind.Log, $"Unparsed {Name} output ({ex.GetType().Name}): {line}"));
                }
            }
        }
        catch (Exception ex)
        {
            writer.TryWrite(new AgentEvent(AgentEventKind.Log, $"Failed reading {Name} output: {ex.Message}"));
        }
    }

    private static void KillTree(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }
}
