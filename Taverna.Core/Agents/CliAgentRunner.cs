using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Taverna.Core.Agents;

public abstract class CliAgentRunner : IAgentRunner
{
    public abstract string Name { get; }
    protected abstract string Command { get; }
    protected abstract IReadOnlyList<string> Arguments(string prompt, string workingDirectory);
    protected abstract IEnumerable<AgentEvent> ParseStdout(string line, object state);
    protected virtual object NewParseState() => new object();

    public bool IsInstalled() => CliLocator.Find(Command) is not null;

    public async IAsyncEnumerable<AgentEvent> RunAsync(
        string prompt, string workingDirectory, [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            yield return new AgentEvent(AgentEventKind.Error, "Prompt vazio.");
            yield break;
        }

        var directory = Path.GetFullPath(workingDirectory);
        if (!Directory.Exists(directory))
        {
            yield return new AgentEvent(AgentEventKind.Error, $"Diretório inexistente: {directory}");
            yield break;
        }

        var executable = CliLocator.Find(Command);
        if (executable is null)
        {
            yield return new AgentEvent(AgentEventKind.Error, $"CLI ausente: {Command}");
            yield break;
        }

        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = directory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in Arguments(prompt, directory)) start.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = start };
        try
        {
            ct.ThrowIfCancellationRequested();
            if (!process.Start()) throw new InvalidOperationException("Process.Start retornou false.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            yield return new AgentEvent(AgentEventKind.Error, $"Não foi possível iniciar {Name}: {ex.Message}");
            yield break;
        }

        yield return new AgentEvent(AgentEventKind.Status, $"{Name}: iniciado (PID {process.Id})");

        var channel = Channel.CreateUnbounded<AgentEvent>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
        var state = NewParseState();
        using var cancel = ct.Register(() => KillTree(process));

        var stdout = PumpAsync(process.StandardOutput, line => ParseStdout(line, state), channel.Writer);
        var stderr = PumpAsync(process.StandardError,
            line => [new AgentEvent(AgentEventKind.Error, $"{Name} stderr: {line}")], channel.Writer);

        var completion = CompleteAsync();
        try
        {
            await foreach (var next in channel.Reader.ReadAllAsync(CancellationToken.None)) yield return next;
            await completion;
        }
        finally
        {
            KillTree(process);
            await completion;
        }

        async Task CompleteAsync()
        {
            try
            {
                await process.WaitForExitAsync(CancellationToken.None);
                await Task.WhenAll(stdout, stderr);
                if (ct.IsCancellationRequested)
                    channel.Writer.TryWrite(new AgentEvent(AgentEventKind.Status, $"{Name}: cancelado"));
                else if (process.ExitCode != 0)
                    channel.Writer.TryWrite(new AgentEvent(AgentEventKind.Error, $"{Name}: processo terminou com código {process.ExitCode}."));
                else
                    channel.Writer.TryWrite(new AgentEvent(AgentEventKind.Status, $"{Name}: concluído"));
            }
            catch (Exception ex)
            {
                channel.Writer.TryWrite(new AgentEvent(AgentEventKind.Error, $"{Name}: falha no processo: {ex.Message}"));
            }
            finally { channel.Writer.TryComplete(); }
        }
    }

    private static async Task PumpAsync(StreamReader reader,
        Func<string, IEnumerable<AgentEvent>> parser, ChannelWriter<AgentEvent> writer)
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
                    writer.TryWrite(new AgentEvent(AgentEventKind.Error, $"Falha ao interpretar saída da CLI: {ex.Message}"));
                }
            }
        }
        catch (Exception ex)
        {
            writer.TryWrite(new AgentEvent(AgentEventKind.Error, $"Falha ao ler saída: {ex.Message}"));
        }
    }

    private static void KillTree(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }
}
