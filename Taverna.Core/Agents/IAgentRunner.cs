namespace Taverna.Core.Agents;

public interface IAgentRunner
{
    string Name { get; }
    bool IsInstalled();
    IAsyncEnumerable<AgentEvent> RunAsync(string prompt, string workingDirectory, CancellationToken ct = default);
}
