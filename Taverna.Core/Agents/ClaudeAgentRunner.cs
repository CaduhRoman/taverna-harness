using System.Text.Json;

namespace Taverna.Core.Agents;

public sealed class ClaudeAgentRunner : CliAgentRunner
{
    public override string Name => "Claude Code";
    protected override string Command => "claude";

    protected override IReadOnlyList<string> Arguments(string prompt, string workingDirectory) =>
    [
        "--print", "--output-format", "stream-json", "--include-partial-messages",
        "--verbose", "--restricted", "--permission-mode", "dontAsk", "--", prompt
    ];

    protected override object NewParseState() => new State();

    protected override IEnumerable<AgentEvent> ParseStdout(string line, object state)
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        var type = JsonEvents.String(root, "type");
        var streaming = (State)state;
        if (type == "stream_event" && JsonEvents.Property(root, "event", out var evt) &&
            JsonEvents.String(evt, "type") == "content_block_delta" &&
            JsonEvents.Property(evt, "delta", out var delta) &&
            JsonEvents.String(delta, "type") == "text_delta")
        {
            streaming.SeenTextDelta = true;
            var chunk = JsonEvents.String(delta, "text");
            if (!string.IsNullOrEmpty(chunk)) yield return new AgentEvent(AgentEventKind.Text, chunk);
        }
        else if (type == "assistant" && !streaming.SeenTextDelta &&
                 JsonEvents.Property(root, "message", out var message) &&
                 JsonEvents.Property(message, "content", out var blocks) &&
                 blocks.ValueKind == JsonValueKind.Array)
        {
            foreach (var block in blocks.EnumerateArray())
            {
                if (JsonEvents.String(block, "type") != "text") continue;
                var text = JsonEvents.String(block, "text");
                if (!string.IsNullOrEmpty(text)) yield return new AgentEvent(AgentEventKind.Text, text);
            }
        }
        else if (type == "result")
        {
            if (JsonEvents.Property(root, "is_error", out var isError) && isError.ValueKind == JsonValueKind.True)
                yield return new AgentEvent(AgentEventKind.Error, JsonEvents.String(root, "result") ?? "Claude retornou erro.");
            if (JsonEvents.Tokens(root) is { } tokens)
                yield return new AgentEvent(AgentEventKind.Tokens, Usage: tokens);
        }
        else if (type == "system")
        {
            var subtype = JsonEvents.String(root, "subtype");
            if (subtype == "init") yield return new AgentEvent(AgentEventKind.Status, "Claude: sessão iniciada");
        }
    }

    private sealed class State { public bool SeenTextDelta { get; set; } }
}
