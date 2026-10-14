using System.Text.Json;

namespace Taverna.Core.Agents;

/// <summary>
/// Codex CLI (`codex exec --json`). The prompt is read from stdin via `-`; images use the documented
/// `--image` flag. Codex emits whole agent messages, not token deltas.
/// </summary>
public sealed class CodexAgentRunner : CliAgentRunner
{
    private static readonly AgentCapabilities Caps = new(
        Models: [],
        Efforts: ["minimal", "low", "medium", "high", "xhigh"],
        SupportsImages: true);

    public override AgentKind Kind => AgentKind.Codex;
    public override string Name => "Codex";
    public override AgentCapabilities Capabilities => Caps;
    protected override string Command => "codex";

    protected override IReadOnlyList<string> Arguments(AgentRequest request, string workingDirectory)
    {
        var args = new List<string>
        {
            "exec", "--json", "--skip-git-repo-check",
            "--sandbox", request.Mode == AgentRunMode.Summary ? "read-only" : "workspace-write",
            "-C", workingDirectory
        };
        if (request.Model is { } model) args.AddRange(["-m", model]);
        if (request.Effort is { } effort) args.AddRange(["-c", $"model_reasoning_effort=\"{effort}\""]);
        foreach (var image in request.Images) args.AddRange(["--image", image]);
        args.Add("-");
        return args;
    }

    protected override string StandardInput(AgentRequest request) => request.Prompt;

    protected override IEnumerable<AgentEvent> ParseStdout(string line, object state)
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        var type = JsonEvents.String(root, "type");

        if (type == "item.completed" && JsonEvents.Property(root, "item", out var item))
        {
            var itemType = JsonEvents.String(item, "type");
            if (itemType == "agent_message" && JsonEvents.String(item, "text") is { Length: > 0 } text)
                yield return new AgentEvent(AgentEventKind.Text, text);
            else if (itemType is "command_execution" or "file_change")
                yield return new AgentEvent(AgentEventKind.Status, $"Codex: {itemType}");
        }
        else if (type == "turn.completed")
        {
            if (JsonEvents.Property(root, "usage", out var usage) && JsonEvents.Usage(usage) is { } tokens)
                yield return new AgentEvent(AgentEventKind.Tokens, Usage: tokens);
            yield return new AgentEvent(AgentEventKind.Status, "Codex: turn completed");
        }
        else if (type is "thread.started" or "turn.started")
            yield return new AgentEvent(AgentEventKind.Status, $"Codex: {type}");
        else if (type == "error")
            // Codex reports transient reconnects as "error" too; the exit code decides the outcome.
            yield return new AgentEvent(AgentEventKind.Log, JsonEvents.String(root, "message") ?? line);
        else if (type == "turn.failed")
        {
            var message = JsonEvents.Property(root, "error", out var error) ? JsonEvents.String(error, "message") : null;
            yield return new AgentEvent(AgentEventKind.Error, message ?? "Codex turn failed.");
        }
    }
}
