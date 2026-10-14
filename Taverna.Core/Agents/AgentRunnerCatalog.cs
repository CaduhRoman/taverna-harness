namespace Taverna.Core.Agents;

public static class AgentRunnerCatalog
{
    public static IReadOnlyList<IAgentRunner> Create() =>
        [new ClaudeAgentRunner(), new CodexAgentRunner(), new CopilotAgentRunner()];
}
