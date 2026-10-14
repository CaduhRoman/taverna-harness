namespace Taverna.Core.Agents;

public static class AgentRunnerCatalog
{
    public static IReadOnlyDictionary<AgentKind, IAgentRunner> Create() => new Dictionary<AgentKind, IAgentRunner>
    {
        [AgentKind.Claude] = new ClaudeAgentRunner(),
        [AgentKind.Codex] = new CodexAgentRunner(),
        [AgentKind.Copilot] = new CopilotAgentRunner(),
        [AgentKind.ExternalApi] = new ExternalApiRunner(ExternalApiOptions.FromEnvironment())
    };

    /// <summary>Agents that can work inside a worktree: the external API has no file access.</summary>
    public static readonly IReadOnlyList<AgentKind> TaskAgents = [AgentKind.Claude, AgentKind.Codex, AgentKind.Copilot];
}
