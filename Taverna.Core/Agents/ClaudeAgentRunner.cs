using System.Text.Json;
using System.Text.Json.Nodes;

namespace Taverna.Core.Agents;

/// <summary>
/// Claude Code in print mode. The prompt and any images go through stdin as one stream-json user
/// message; image content blocks were verified against claude 2.1.296.
/// Models come from the account's cached catalog, as in Claude Code's own picker; the aliases are the fallback.
/// </summary>
public sealed class ClaudeAgentRunner(ClaudeSetup? setup = null) : CliAgentRunner
{
    private static readonly AgentCapabilities Fallback = new(
        Models: ["fable", "opus", "sonnet", "haiku"],
        Efforts: ["low", "medium", "high", "xhigh", "max"],
        SupportsImages: true);

    private readonly ClaudeSetup setup = setup ?? ClaudeSetup.Default();

    public override AgentKind Kind => AgentKind.Claude;
    public override string Name => "Claude";

    public override AgentCapabilities Capabilities =>
        setup.Models is { Count: > 0 } models ? AgentCapabilities.From(models, Fallback.Efforts, supportsImages: true) : Fallback;
    protected override string Command => "claude";

    protected override IReadOnlyList<string> Arguments(AgentRequest request, string workingDirectory)
    {
        var args = new List<string>
        {
            "--print", "--input-format", "stream-json", "--output-format", "stream-json",
            "--include-partial-messages", "--verbose", "--no-session-persistence",
            // Keep restricted file-tool protections. Auto explicitly adds command tools below;
            // these commands are NOT isolated by an OS sandbox.
            "--restricted",
            "--permission-mode", request.Mode == AgentRunMode.Summary ? "dontAsk" : "acceptEdits"
        };
        if (request.Mode == AgentRunMode.Chat && request.PermissionMode == AgentPermissionMode.Auto)
            args.AddRange(["--tools", "Read,Glob,Grep,Edit,Write,Bash,PowerShell",
                "--allowedTools", "Read,Glob,Grep,Edit,Write,Bash,PowerShell",
                "--permission-prompts", "none"]);
        if (request.Model is { } model) args.AddRange(["--model", model]);
        if (request.Effort is { } effort) args.AddRange(["--effort", effort]);
        return args;
    }

    protected override string StandardInput(AgentRequest request)
    {
        var content = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = request.Prompt } };
        foreach (var image in request.Images)
            content.Add(new JsonObject
            {
                ["type"] = "image",
                ["source"] = new JsonObject
                {
                    ["type"] = "base64",
                    ["media_type"] = MediaType(image),
                    ["data"] = Convert.ToBase64String(File.ReadAllBytes(image))
                }
            });
        var message = new JsonObject
        {
            ["type"] = "user",
            ["message"] = new JsonObject { ["role"] = "user", ["content"] = content }
        };
        return message.ToJsonString() + "\n";
    }

    private static string MediaType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        _ => "image/png"
    };

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
        else if (type == "assistant" && JsonEvents.Property(root, "message", out var toolMessage) &&
                 JsonEvents.Property(toolMessage, "content", out var toolBlocks) && toolBlocks.ValueKind == JsonValueKind.Array)
        {
            foreach (var block in toolBlocks.EnumerateArray())
                if (JsonEvents.String(block, "type") == "tool_use")
                    yield return new AgentEvent(AgentEventKind.Status, $"Tool: {JsonEvents.String(block, "name")}");
        }
        else if (type == "result")
        {
            if (JsonEvents.Property(root, "is_error", out var isError) && isError.ValueKind == JsonValueKind.True)
                yield return new AgentEvent(AgentEventKind.Error, JsonEvents.String(root, "result") ?? "Claude reported an error.");
            if (JsonEvents.Tokens(root) is { } tokens)
                yield return new AgentEvent(AgentEventKind.Tokens, Usage: tokens);
        }
        else if (type == "system" && JsonEvents.String(root, "subtype") == "init")
            yield return new AgentEvent(AgentEventKind.Status, $"Session started ({JsonEvents.String(root, "model")})");
    }

    private sealed class State { public bool SeenTextDelta { get; set; } }
}
