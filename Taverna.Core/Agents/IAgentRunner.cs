namespace Taverna.Core.Agents;

public enum AgentKind { Claude, Codex, Copilot, ExternalApi }

/// <summary>Chat runs in the project root, Task in an isolated worktree, Summary must not touch files.</summary>
public enum AgentRunMode { Chat, Task, Summary }

public sealed record AgentRequest(string Prompt, string WorkingDirectory)
{
    public string? Model { get; init; }
    public string? Effort { get; init; }
    public AgentPermissionMode? PermissionMode { get; init; }
    /// <summary>Absolute image paths. Runners without confirmed image input reject non-empty lists.</summary>
    public IReadOnlyList<string> Images { get; init; } = [];
    public AgentRunMode Mode { get; init; } = AgentRunMode.Chat;
}

public interface IAgentRunner
{
    AgentKind Kind { get; }
    string Name { get; }
    AgentCapabilities Capabilities { get; }

    /// <summary>Installed (CLI on PATH) or configured (external API).</summary>
    bool IsAvailable();

    IAsyncEnumerable<AgentEvent> RunAsync(AgentRequest request, CancellationToken ct = default);
}
