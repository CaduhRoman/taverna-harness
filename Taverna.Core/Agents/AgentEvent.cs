namespace Taverna.Core.Agents;

public enum AgentEventKind { Text, Status, Error, Tokens }

public sealed record TokenUsage(long Input, long Output, long? CachedInput = null);

public sealed record AgentEvent(AgentEventKind Kind, string? Text = null, TokenUsage? Usage = null);
