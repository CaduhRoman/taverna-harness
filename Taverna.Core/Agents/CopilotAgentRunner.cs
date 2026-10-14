using System.Runtime.CompilerServices;

namespace Taverna.Core.Agents;

public sealed class CopilotAgentRunner : IAgentRunner
{
    public string Name => "GitHub Copilot CLI";
    public bool IsInstalled() => CliLocator.Find("copilot") is not null;

    public async IAsyncEnumerable<AgentEvent> RunAsync(
        string prompt, string workingDirectory, [EnumeratorCancellation] CancellationToken ct = default)
    {
        // The executable is absent in the validation environment. No unverified invocation flags are used.
        await Task.CompletedTask;
        ct.ThrowIfCancellationRequested();
        yield return new AgentEvent(AgentEventKind.Error,
            IsInstalled() ? "Copilot instalado, mas modo não interativo e isolamento ainda não validados nesta máquina."
                          : "GitHub Copilot CLI não está instalada; execução indisponível até validação local de --help.");
    }
}
