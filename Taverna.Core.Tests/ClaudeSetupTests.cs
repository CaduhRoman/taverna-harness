using Taverna.Core.Agents;
using Xunit;

namespace Taverna.Core.Tests;

public sealed class ClaudeSetupTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("taverna-claude-").FullName;
    private string ConfigDir => Path.Combine(root, ".claude");
    private string GlobalConfig => Path.Combine(root, ".claude.json");
    public void Dispose() => Directory.Delete(root, true);

    private const string Catalog = """
        {"version":2,"catalog":{"surface":"cc","config":{"id":"cc","models":[
          {"id":"claude-opus-5-5","name":"Opus 5.5","section":"main","thinking":{"type":"effort","effort_options":[{"id":"low"},{"id":"max"}]}},
          {"id":"claude-haiku-4-5-20251001","name":"Haiku 4.5","section":"overflow","thinking":{"type":"none"}},
          {"id":"claude-fable-5-1","name":"Fable 5.1","section":"main","thinking":{"type":"effort","effort_options":[{"id":"high"}]}}
        ]}}}
        """;

    private ClaudeAgentRunner Runner(string? catalog = Catalog, string? global = null)
    {
        var directory = Path.Combine(ConfigDir, "cache", "model-catalog");
        Directory.CreateDirectory(directory);
        if (catalog is not null) File.WriteAllText(Path.Combine(directory, "org-cc.json"), catalog);
        if (global is not null) File.WriteAllText(GlobalConfig, global);
        return new ClaudeAgentRunner(new ClaudeSetup(ConfigDir, GlobalConfig));
    }

    [Fact]
    public void ListsCatalogModelsMainSectionFirstWithLabels()
    {
        var caps = Runner(global: """{"additionalModelOptionsCache":[{"value":"claude-fable-5-1[1m]","label":"Fable"}]}""").Capabilities;
        Assert.Equal(["claude-opus-5-5", "claude-fable-5-1", "claude-fable-5-1[1m]", "claude-haiku-4-5-20251001"], caps.Models);
        Assert.Equal("Opus 5.5", caps.Label("claude-opus-5-5"));
        Assert.Equal("Fable 5.1 (1M context)", caps.Label("claude-fable-5-1[1m]"));
        Assert.Equal(["high"], caps.EffortsFor("claude-fable-5-1[1m]"));
        Assert.Equal(["low", "high", "max"], caps.Efforts);
        Assert.Empty(caps.EffortsFor("claude-haiku-4-5-20251001")!);
        Assert.Null(caps.EffortsFor("opus"));
    }

    [Fact]
    public void FallsBackToAliasesWithoutCatalog()
    {
        var caps = Runner(catalog: null).Capabilities;
        Assert.Equal(["fable", "opus", "sonnet", "haiku"], caps.Models);
        Assert.Contains("xhigh", caps.Efforts);
    }

    [Theory]
    [InlineData("claude-haiku-4-5-20251001", "low", "does not accept an effort")]
    [InlineData("claude-opus-5-5", "high", "supported: low, max")]
    public async Task RejectsEffortsTheModelDoesNotAccept(string model, string effort, string message)
    {
        var events = new List<AgentEvent>();
        await foreach (var e in Runner().RunAsync(new AgentRequest("hi", root) { Model = model, Effort = effort })) events.Add(e);
        Assert.Contains(events, e => e.Kind == AgentEventKind.Error && e.Text!.Contains(message));
        Assert.DoesNotContain(events, e => e.Kind == AgentEventKind.Status);
    }
}
