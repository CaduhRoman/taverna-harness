using System.Net;
using System.Net.Sockets;
using Taverna.Core.Agents;
using Xunit;

namespace Taverna.Core.Tests;

public sealed class CodexSetupTests : IDisposable
{
    private readonly string home = Directory.CreateTempSubdirectory("taverna-codex-").FullName;
    public void Dispose() => Directory.Delete(home, true);

    private const string Cache = """
        {"models":[
          {"slug":"gpt-6-sol","visibility":"list","supported_reasoning_levels":[{"effort":"low"},{"effort":"xhigh"}]},
          {"slug":"gpt-reserve","visibility":"hide","supported_reasoning_levels":[{"effort":"low"}]},
          {"slug":"chatgpt-web/gpt-6-sol","visibility":"list","supported_reasoning_levels":[{"effort":"medium"},{"effort":"high"}]}
        ]}
        """;

    private void Write(string config, string? cache = Cache)
    {
        File.WriteAllText(Path.Combine(home, "config.toml"), config);
        if (cache is not null) File.WriteAllText(Path.Combine(home, "models_cache.json"), cache);
    }

    [Fact]
    public void ListsVisibleModelsWithConfiguredFirst()
    {
        Write("model = \"chatgpt-web/gpt-6-sol\"\nopenai_base_url = \"http://127.0.0.1:1/v1\"\n[features]\nmodel = \"ignored\"\n");
        var runner = new CodexAgentRunner(new CodexSetup(home));
        Assert.Equal(["chatgpt-web/gpt-6-sol", "gpt-6-sol"], runner.Capabilities.Models);
        Assert.Equal(["low", "medium", "high", "xhigh"], runner.Capabilities.Efforts);
        Assert.Equal(new Uri("http://127.0.0.1:1/v1"), new CodexSetup(home).BaseUrl);
    }

    [Fact]
    public void FallsBackWithoutCache()
    {
        Write("model = 'custom-model'\n", cache: null);
        var caps = new CodexAgentRunner(new CodexSetup(home)).Capabilities;
        Assert.Equal(["custom-model"], caps.Models);
        Assert.Contains("minimal", caps.Efforts);
    }

    [Fact]
    public async Task RejectsEffortTheModelDoesNotAccept()
    {
        Write("model = \"chatgpt-web/gpt-6-sol\"\n");
        var events = await Run(new AgentRequest("hi", home) { Effort = "low" });
        Assert.Contains(events, e => e.Kind == AgentEventKind.Error && e.Text!.Contains("supported: medium, high"));
        Assert.DoesNotContain(events, e => e.Kind == AgentEventKind.Status);
    }

    [Fact]
    public async Task ReportsUnreachableLocalBridgeBeforeLaunching()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        Write($"openai_base_url = \"http://127.0.0.1:{port}/v1\"\n");

        var events = await Run(new AgentRequest("hi", home));
        Assert.Contains(events, e => e.Kind == AgentEventKind.Error && e.Text!.Contains("nothing is listening"));
        Assert.DoesNotContain(events, e => e.Kind == AgentEventKind.Status);
        Assert.Equal(-1, events[^1].ExitCode);
    }

    [Fact]
    public async Task ReachableLocalBridgePassesPreflight()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            Write($"openai_base_url = \"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/v1\"\n");
            Assert.Null(await new CodexSetup(home).CheckLocalEndpointAsync(CancellationToken.None));
        }
        finally { listener.Stop(); }
    }

    private async Task<List<AgentEvent>> Run(AgentRequest request)
    {
        var events = new List<AgentEvent>();
        await foreach (var e in new CodexAgentRunner(new CodexSetup(home)).RunAsync(request)) events.Add(e);
        return events;
    }
}
