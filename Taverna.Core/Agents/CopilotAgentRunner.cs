namespace Taverna.Core.Agents;

/// <summary>
/// GitHub Copilot CLI in programmatic mode (`copilot -p`), following its public documentation.
/// UNVERIFIED: the CLI was not installed where this was written, so flags and plain-text output
/// could not be checked. Images are disabled until an input mechanism is confirmed.
/// </summary>
public sealed class CopilotAgentRunner : CliAgentRunner
{
    private static readonly AgentCapabilities Caps = new(Models: [], Efforts: [], SupportsImages: false);

    public override AgentKind Kind => AgentKind.Copilot;
    public override string Name => "Copilot";
    public override AgentCapabilities Capabilities => Caps;
    protected override string Command => "copilot";
    protected override bool PromptInArguments => true;

    protected override IReadOnlyList<string> Arguments(AgentRequest request, string workingDirectory)
    {
        var args = new List<string> { "-p", request.Prompt, "--no-color" };
        // Auto grants development tools, retaining path/URL checks and explicit deny rules.
        if (request.Mode == AgentRunMode.Chat && request.PermissionMode == AgentPermissionMode.Auto)
            args.AddRange(["--allow-tool", "read,write,shell"]);
        else if (request.Mode != AgentRunMode.Summary) args.AddRange(["--allow-tool", "write"]);
        if (request.Model is { } model) args.AddRange(["--model", model]);
        return args;
    }

    protected override IEnumerable<AgentEvent> ParseStdout(string line, object state) =>
        [new AgentEvent(AgentEventKind.Text, line + "\n")];
}
