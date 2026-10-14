using Taverna.Core.Agents;
using Taverna.Core.Git;

namespace Taverna.Core.Orchestration;

public enum AgentTaskStatus { Idle, Working, Done, Error }

public enum TaskMergeState { None, Merged, Conflict, Failed }

/// <summary>Persisted record of one orchestrated task. The orchestrator hands out clones, never the live instance.</summary>
public sealed class AgentTask
{
    public const string InterruptedReason = "Interrupted";
    public const string CancelledReason = "Cancelled";

    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Prompt { get; set; } = "";
    public AgentKind Agent { get; set; }
    public string? Model { get; set; }
    public string? Effort { get; set; }

    public string RepositoryRoot { get; set; } = "";
    public string WorktreePath { get; set; } = "";
    public string Branch { get; set; } = "";
    public string BaseCommit { get; set; } = "";
    public string TargetBranch { get; set; } = "";

    public AgentTaskStatus Status { get; set; } = AgentTaskStatus.Idle;
    /// <summary>Why the task is in Error: Interrupted, Cancelled, or the failure message.</summary>
    public string? Reason { get; set; }

    public List<GitFileChange> ChangedFiles { get; set; } = [];
    public TokenUsage? Tokens { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public long DurationMs { get; set; }

    /// <summary>Relative to the project root: .taverna/tasks/&lt;id&gt;.log holds the full execution log.</summary>
    public string LogPath { get; set; } = "";

    public TaskMergeState MergeState { get; set; }
    public List<string> ConflictFiles { get; set; } = [];
    public string? MergeMessage { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsActive => Status is AgentTaskStatus.Idle or AgentTaskStatus.Working;

    public ManagedWorktree ToWorktree() => new(RepositoryRoot, WorktreePath, Branch, BaseCommit, TargetBranch);

    public AgentTask Clone()
    {
        var copy = (AgentTask)MemberwiseClone();
        copy.ChangedFiles = [.. ChangedFiles];
        copy.ConflictFiles = [.. ConflictFiles];
        return copy;
    }
}

public sealed record TaskRequest(string Title, AgentKind Agent, string Prompt)
{
    public string? Model { get; init; }
    public string? Effort { get; init; }
}
