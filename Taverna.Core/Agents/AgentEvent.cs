namespace Taverna.Core.Agents;

/// <summary>
/// Text, Status and Tokens describe progress. Error is a failure reported by the agent itself;
/// Log carries diagnostics (stderr). Exit and Cancelled are terminal: exactly one of them ends a run.
/// </summary>
public enum AgentEventKind { Text, Status, Error, Tokens, Log, Exit, Cancelled }

public sealed record TokenUsage(long Input, long Output, long? CachedInput = null)
{
    public static TokenUsage? Add(TokenUsage? left, TokenUsage? right) =>
        left is null ? right : right is null ? left
            : new TokenUsage(left.Input + right.Input, left.Output + right.Output,
                left.CachedInput is null && right.CachedInput is null ? null : (left.CachedInput ?? 0) + (right.CachedInput ?? 0));
}

public sealed record AgentEvent(AgentEventKind Kind, string? Text = null, TokenUsage? Usage = null, int? ExitCode = null);
