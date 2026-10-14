using System.Text.Json;

namespace Taverna.Core.Agents;

public sealed class CodexAgentRunner : CliAgentRunner
{
    public override string Name => "Codex CLI";
    protected override string Command => "codex";

    protected override IReadOnlyList<string> Arguments(string prompt, string workingDirectory) =>
        ["exec", "--json", "--sandbox", "workspace-write", "-C", workingDirectory, prompt];

    protected override IEnumerable<AgentEvent> ParseStdout(string line, object state)
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        var type = JsonEvents.String(root, "type");

        if (type == "item.completed" && JsonEvents.Property(root, "item", out var item) &&
            JsonEvents.String(item, "type") == "agent_message")
        {
            var text = JsonEvents.String(item, "text");
            if (!string.IsNullOrEmpty(text)) yield return new AgentEvent(AgentEventKind.Text, text);
        }
        else if (type == "turn.completed")
        {
            if (JsonEvents.Property(root, "usage", out var usageRoot) &&
                JsonEvents.Tokens(WrapUsage(usageRoot)) is { } tokens)
                yield return new AgentEvent(AgentEventKind.Tokens, Usage: tokens);
            yield return new AgentEvent(AgentEventKind.Status, "Codex: turno concluído");
        }
        else if (type is "thread.started" or "turn.started")
            yield return new AgentEvent(AgentEventKind.Status, $"Codex: {type}");
        else if (type is "error" or "turn.failed")
        {
            var message = JsonEvents.String(root, "message");
            if (message is null && JsonEvents.Property(root, "error", out var error))
                message = JsonEvents.String(error, "message");
            yield return new AgentEvent(AgentEventKind.Error, message ?? "Codex retornou erro.");
        }
    }

    private static JsonElement WrapUsage(JsonElement usage)
    {
        // JsonEvents expects a top-level usage property; the Codex event nests it at turn.completed.usage.
        using var wrapper = JsonDocument.Parse("{\"usage\":" + usage.GetRawText() + "}");
        return wrapper.RootElement.Clone();
    }
}
