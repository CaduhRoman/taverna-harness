using System.Reflection;
using Taverna.Core.Agents;
using Taverna.Core.Chat;
using Taverna.Core.Context;
using Xunit;

namespace Taverna.Core.Tests;

public sealed class PermissionTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("taverna-permissions-").FullName;
    public void Dispose() => Directory.Delete(root, true);

    [Theory]
    [InlineData(AgentKind.Claude)]
    [InlineData(AgentKind.Codex)]
    [InlineData(AgentKind.Copilot)]
    [InlineData(AgentKind.ExternalApi)]
    public void ManualIsNotAdvertisedWithoutApprovalTransport(AgentKind kind) =>
        Assert.False(AgentPermissions.Supports(kind, AgentPermissionMode.Manual));

    [Theory]
    [InlineData(AgentKind.Claude)]
    [InlineData(AgentKind.Codex)]
    [InlineData(AgentKind.Copilot)]
    public async Task CliRejectsUnsupportedModesBeforeLaunching(AgentKind kind)
    {
        foreach (var mode in new[] { AgentPermissionMode.Manual, AgentPermissionMode.Plan, (AgentPermissionMode)999 })
        {
            var runner = Runner(kind);
            var events = new List<AgentEvent>();
            await foreach (var e in runner.RunAsync(new AgentRequest("test", root) { PermissionMode = mode })) events.Add(e);
            Assert.Contains(events, e => e.Kind == AgentEventKind.Error && e.Text!.Contains("does not support"));
            Assert.DoesNotContain(events, e => e.Kind == AgentEventKind.Status);
            Assert.Equal(-1, events[^1].ExitCode);
        }
    }

    [Fact]
    public async Task ApiRejectsAutoWithoutIssuingRequest()
    {
        var runner = new ExternalApiRunner(new ExternalApiOptions(null, null, []));
        var events = new List<AgentEvent>();
        await foreach (var e in runner.RunAsync(new AgentRequest("x", root) { PermissionMode = AgentPermissionMode.Auto })) events.Add(e);
        Assert.Contains(events, e => e.Text?.Contains("does not support Auto") == true);
        Assert.True(AgentPermissions.Supports(AgentKind.ExternalApi, AgentPermissionMode.Plan));
    }

    [Fact]
    public async Task PreferenceRoundTripKeepsSeparateAgentsAndRejectsCorruption()
    {
        var path = Path.Combine(root, "permissions.json");
        var store = new PermissionPreferences(path);
        Assert.Empty(store.Load());
        await store.SaveAsync(new Dictionary<AgentKind, AgentPermissionMode>
        {
            [AgentKind.Claude] = AgentPermissionMode.Auto,
            [AgentKind.Codex] = AgentPermissionMode.Manual,
            [AgentKind.ExternalApi] = AgentPermissionMode.Plan
        });
        var reloaded = new PermissionPreferences(path).Load();
        Assert.Equal(2, reloaded.Count);
        Assert.Equal(AgentPermissionMode.Auto, reloaded[AgentKind.Claude]);
        Assert.Equal(AgentPermissionMode.Plan, reloaded[AgentKind.ExternalApi]);
        Assert.False(reloaded.ContainsKey(AgentKind.Codex));
        await File.WriteAllTextAsync(path, "invalid json");
        Assert.Empty(store.Load());
    }

    [Fact]
    public async Task ChatPassesPermissionModelAndEffortAndRejectsUnsupportedBeforeSaving()
    {
        var runner = new ScriptedRunner(AgentKind.Claude, (_, _) => ScriptedRunner.Reply("done"));
        await using var session = new ChatSession(root, new Dictionary<AgentKind, IAgentRunner> { [AgentKind.Claude] = runner },
            new ContextBuilder(), _ => Task.FromResult(new RepositoryHead(null, null)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.SendAsync(new ChatSendRequest("no", AgentKind.Claude)
            { PermissionMode = AgentPermissionMode.Manual }));
        Assert.Empty(session.Messages);
        await session.SendAsync(new ChatSendRequest("yes", AgentKind.Claude)
            { Model = "opus", Effort = "high", PermissionMode = AgentPermissionMode.Auto });
        var request = Assert.Single(runner.Requests);
        Assert.Equal(AgentPermissionMode.Auto, request.PermissionMode);
        Assert.Equal("opus", request.Model);
        Assert.Equal("high", request.Effort);
    }

    [Theory]
    [InlineData(AgentKind.Claude)]
    [InlineData(AgentKind.Codex)]
    [InlineData(AgentKind.Copilot)]
    public void AutoEnablesCommandsWithoutBypassFlagsAndDoesNotChangeTasks(AgentKind kind)
    {
        var runner = Runner(kind);
        var request = new AgentRequest("x", root) { PermissionMode = AgentPermissionMode.Auto, Model = "test-model" };
        var args = Arguments(runner, request);
        Assert.Contains("test-model", args);
        Assert.DoesNotContain(args, a => a.Contains("bypass") || a.Contains("danger") || a.Contains("allow-all"));
        if (kind == AgentKind.Claude)
        {
            Assert.Contains("--restricted", args);
            Assert.Contains("--allowedTools", args);
            Assert.Contains("Read,Glob,Grep,Edit,Write,Bash,PowerShell", args);
        }
        if (kind == AgentKind.Codex)
        {
            Assert.Contains("workspace-write", args);
            Assert.Contains("approval_policy=\"never\"", args);
        }
        if (kind == AgentKind.Copilot) Assert.Contains("read,write,shell", args);
        Assert.Equal(Arguments(runner, request with { Mode = AgentRunMode.Task, PermissionMode = null }),
            Arguments(runner, request with { Mode = AgentRunMode.Task }));
        Assert.Equal(Arguments(runner, request with { Mode = AgentRunMode.Summary, PermissionMode = null }),
            Arguments(runner, request with { Mode = AgentRunMode.Summary }));
    }

    [Fact]
    public async Task PlanDoesNotInvokeCliSummarizerWhenHistoryOverflows()
    {
        var runner = new ScriptedRunner(AgentKind.ExternalApi, (_, _) => ScriptedRunner.Reply(new string('a', 1000)));
        var summarizer = new CountingSummarizer();
        await using var session = new ChatSession(root,
            new Dictionary<AgentKind, IAgentRunner> { [AgentKind.ExternalApi] = runner },
            new ContextBuilder(new ContextOptions { MaxCharacters = 1200 }, summarizer),
            _ => Task.FromResult(new RepositoryHead(null, null)));
        for (var i = 0; i < 3; i++)
            await session.SendAsync(new ChatSendRequest("Analyze", AgentKind.ExternalApi) { PermissionMode = AgentPermissionMode.Plan });
        Assert.Equal(0, summarizer.Calls);
        Assert.Equal(3, runner.Requests.Count);
        Assert.All(runner.Requests, request => Assert.Equal(AgentPermissionMode.Plan, request.PermissionMode));
    }

    private sealed class CountingSummarizer : IContextSummarizer
    {
        public int Calls { get; private set; }
        public bool IsAvailable => true;
        public Task<string?> SummarizeAsync(string conversation, int maxCharacters, CancellationToken ct)
        { Calls++; return Task.FromResult<string?>("summary"); }
    }

    private static CliAgentRunner Runner(AgentKind kind) => kind switch
    {
        AgentKind.Claude => new ClaudeAgentRunner(),
        AgentKind.Codex => new CodexAgentRunner(),
        _ => new CopilotAgentRunner()
    };

    private IReadOnlyList<string> Arguments(CliAgentRunner runner, AgentRequest request) =>
        (IReadOnlyList<string>)runner.GetType().GetMethod("Arguments", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(runner, [request, root])!;
}
