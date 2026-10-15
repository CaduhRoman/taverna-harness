using System.Net;
using System.Net.Sockets;
using System.Text;
using Taverna.Core.Agents;
using Taverna.Core.Chat;
using Xunit;

namespace Taverna.Core.Tests;

public sealed class AttachmentsAndApiTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("taverna-attach-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); }
        catch (IOException) { }
    }

    [Fact]
    public async Task AttachmentStore_SavesUnderTavernaAttachments_WithRelativePath()
    {
        var relative = await AttachmentStore.SaveAsync(root, [1, 2, 3], ".PNG");

        Assert.StartsWith(Path.Combine(".taverna", "attachments"), relative);
        Assert.EndsWith(".png", relative);
        Assert.Equal([1, 2, 3], await File.ReadAllBytesAsync(Path.Combine(root, relative)));
    }

    [Theory]
    [InlineData(".exe")]
    [InlineData(".svg")]
    public async Task AttachmentStore_RejectsNonImages(string extension) =>
        await Assert.ThrowsAsync<ArgumentException>(() => AttachmentStore.SaveAsync(root, [1], extension));

    [Fact]
    public async Task AttachmentStore_RejectsEmptyAndOversizedImages()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => AttachmentStore.SaveAsync(root, [], ".png"));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            AttachmentStore.SaveAsync(root, new byte[AttachmentStore.MaxImageBytes + 1], ".png"));
    }

    [Fact]
    public async Task Chat_PassesAttachmentsAsAbsoluteImagePaths_ToImageCapableAgents()
    {
        var relative = await AttachmentStore.SaveAsync(root, [9], ".png");
        var claude = new ScriptedRunner(AgentKind.Claude, (_, _) => ScriptedRunner.Reply("seen"), images: true);
        await using var session = new ChatSession(root, new Dictionary<AgentKind, IAgentRunner> { [AgentKind.Claude] = claude },
            new Context.ContextBuilder(), _ => Task.FromResult(new RepositoryHead(null, null)));

        await session.SendAsync(new ChatSendRequest("what is this?", AgentKind.Claude) { Attachments = [relative] });

        var image = Assert.Single(claude.Requests.Single().Images);
        Assert.Equal(Path.GetFullPath(Path.Combine(root, relative)), image);
        Assert.Equal([relative], session.Messages[0].Attachments);
        Assert.Contains("[attachments: ", claude.Requests.Single().Prompt);
    }

    [Theory]
    [InlineData(typeof(CopilotAgentRunner))]
    [InlineData(typeof(ExternalApiRunner))]
    public void AgentsWithoutConfirmedImageInput_DoNotAdvertiseImages(Type runnerType)
    {
        IAgentRunner runner = runnerType == typeof(ExternalApiRunner)
            ? new ExternalApiRunner(new ExternalApiOptions(null, null, []))
            : new CopilotAgentRunner();
        Assert.False(runner.Capabilities.SupportsImages);
    }

    [Fact]
    public async Task CliRunner_RejectsImagesForAgentWithoutSupport_BeforeStartingProcess()
    {
        var image = Path.Combine(root, "a.png");
        await File.WriteAllBytesAsync(image, [1]);
        var events = new List<AgentEvent>();
        await foreach (var e in new CopilotAgentRunner().RunAsync(new AgentRequest("x", root) { Images = [image] })) events.Add(e);

        Assert.Contains(events, e => e.Kind == AgentEventKind.Error);
        Assert.Equal(-1, events[^1].ExitCode);
        Assert.DoesNotContain(events, e => e.Kind == AgentEventKind.Status);
    }

    [Theory]
    [InlineData("gpt 5; rm -rf /")]
    [InlineData("opus & calc")]
    public async Task CliRunner_RejectsUnsafeModelNames(string model)
    {
        var events = new List<AgentEvent>();
        await foreach (var e in new ClaudeAgentRunner().RunAsync(new AgentRequest("x", root) { Model = model })) events.Add(e);
        Assert.Contains(events, e => e.Kind == AgentEventKind.Error && e.Text!.Contains("Invalid model"));
    }

    [Fact]
    public async Task ExternalApi_StreamsDeltasAndUsage_FromSse()
    {
        string? captured = null;
        var body =
            "data: {\"choices\":[{\"delta\":{\"content\":\"Hel\"}}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{\"content\":\"lo\"}}]}\n\n" +
            "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":7,\"completion_tokens\":2}}\n\n" +
            "data: [DONE]\n\n";
        await using var server = await FakeHttpServer.StartAsync("200 OK", "text/event-stream", body, request => captured = request);
        var runner = new ExternalApiRunner(new ExternalApiOptions(server.BaseUrl, "secret-key", ["model-a", "model-b"]));

        var events = new List<AgentEvent>();
        await foreach (var e in runner.RunAsync(new AgentRequest("hi", root) { Model = "model-b", Effort = "low" })) events.Add(e);

        Assert.Equal("Hello", string.Concat(events.Where(e => e.Kind == AgentEventKind.Text).Select(e => e.Text)));
        Assert.Equal(new TokenUsage(7, 2), events.Single(e => e.Kind == AgentEventKind.Tokens).Usage);
        Assert.Equal(0, events[^1].ExitCode);
        Assert.Contains("POST /v1/chat/completions", captured);
        Assert.Contains("Authorization: Bearer secret-key", captured);
        Assert.Contains("\"model\":\"model-b\"", captured);
        Assert.Contains("\"reasoning_effort\":\"low\"", captured);
        Assert.Contains("\"stream\":true", captured);
    }

    [Fact]
    public async Task ExternalApi_ReportsHttpErrors()
    {
        await using var server = await FakeHttpServer.StartAsync("401 Unauthorized", "application/json", "{\"error\":\"bad key\"}");
        var runner = new ExternalApiRunner(new ExternalApiOptions(server.BaseUrl, "k", ["m"]));

        var events = new List<AgentEvent>();
        await foreach (var e in runner.RunAsync(new AgentRequest("hi", root))) events.Add(e);

        Assert.Contains(events, e => e.Kind == AgentEventKind.Error && e.Text!.Contains("401") && e.Text.Contains("bad key"));
        Assert.Equal(401, events[^1].ExitCode);
    }

    [Fact]
    public void ExternalApi_IsUnavailableWithoutConfiguration()
    {
        Assert.False(new ExternalApiRunner(new ExternalApiOptions("https://x", null, ["m"])).IsAvailable());
        Assert.False(new ExternalApiRunner(new ExternalApiOptions("https://x", "k", [])).IsAvailable());
        Assert.True(new ExternalApiRunner(new ExternalApiOptions("https://x", "k", ["m"])).IsAvailable());
    }

    /// <summary>Minimal one-shot HTTP/1.1 server on loopback; avoids HttpListener URL ACLs on Windows.</summary>
    private sealed class FakeHttpServer : IAsyncDisposable
    {
        private readonly TcpListener listener;
        private readonly Task serving;
        public string BaseUrl { get; }

        private FakeHttpServer(TcpListener listener, Task serving)
        {
            this.listener = listener;
            this.serving = serving;
            BaseUrl = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/v1";
        }

        public static Task<FakeHttpServer> StartAsync(string status, string contentType, string body, Action<string>? onRequest = null)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var serving = Task.Run(async () =>
            {
                using var client = await listener.AcceptTcpClientAsync();
                using var stream = client.GetStream();
                var request = await ReadRequestAsync(stream);
                onRequest?.Invoke(request);
                var payload = Encoding.UTF8.GetBytes(body);
                var head = $"HTTP/1.1 {status}\r\nContent-Type: {contentType}\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
                await stream.WriteAsync(payload);
            });
            return Task.FromResult(new FakeHttpServer(listener, serving));
        }

        private static async Task<string> ReadRequestAsync(NetworkStream stream)
        {
            var buffer = new byte[65536];
            var text = new StringBuilder();
            int headerEnd;
            while ((headerEnd = text.ToString().IndexOf("\r\n\r\n", StringComparison.Ordinal)) < 0)
            {
                var read = await stream.ReadAsync(buffer);
                if (read == 0) return text.ToString();
                text.Append(Encoding.UTF8.GetString(buffer, 0, read));
            }
            var headers = text.ToString()[..headerEnd];
            var lengthLine = headers.Split("\r\n").FirstOrDefault(l => l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
            var length = lengthLine is null ? 0 : int.Parse(lengthLine.Split(':')[1].Trim());
            while (Encoding.UTF8.GetByteCount(text.ToString()[(headerEnd + 4)..]) < length)
            {
                var read = await stream.ReadAsync(buffer);
                if (read == 0) break;
                text.Append(Encoding.UTF8.GetString(buffer, 0, read));
            }
            return text.ToString();
        }

        public async ValueTask DisposeAsync()
        {
            listener.Stop();
            try { await serving; }
            catch (Exception) { }
        }
    }
}
