using System.Runtime.CompilerServices;
using Taverna.Core.Agents;
using Taverna.Core.Chat;
using Taverna.Core.Context;
using Xunit;

namespace Taverna.Core.Tests;

public sealed class ChatSessionTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("taverna-chat-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); }
        catch (IOException) { }
    }

    private ChatSession Create(params ScriptedRunner[] runners) =>
        new(root, runners.ToDictionary(r => r.Kind, r => (IAgentRunner)r), new ContextBuilder(),
            _ => Task.FromResult(new RepositoryHead("main", "abc1234")));

    private static async IAsyncEnumerable<AgentEvent> Stream(params string[] chunks)
    {
        foreach (var chunk in chunks)
        {
            await Task.Yield();
            yield return new AgentEvent(AgentEventKind.Text, chunk);
        }
        yield return new AgentEvent(AgentEventKind.Tokens, Usage: new TokenUsage(3, 4));
        yield return new AgentEvent(AgentEventKind.Exit, ExitCode: 0);
    }

    [Fact]
    public async Task Send_StreamsReplyAndRecordsMetadata()
    {
        var claude = new ScriptedRunner(AgentKind.Claude, (_, _) => Stream("Hel", "lo"));
        await using var session = Create(claude);
        var deltas = new List<string>();
        session.MessageDelta += (_, d) => { lock (deltas) deltas.Add(d); };

        await session.SendAsync(new ChatSendRequest("hi", AgentKind.Claude) { Model = "opus", Effort = "high" });

        var messages = session.Messages;
        Assert.Equal(2, messages.Count);
        var user = messages[0];
        var reply = messages[1];
        Assert.Equal(ChatAuthor.User, user.Author);
        Assert.Equal("main", user.Branch);
        Assert.Equal("abc1234", user.Commit);
        Assert.Equal(ChatAuthor.Claude, reply.Author);
        Assert.Equal(user.Id, reply.ReplyToId);
        Assert.Equal("Hello", reply.Content);
        Assert.Equal(ChatMessageState.Complete, reply.State);
        Assert.Equal("opus", reply.Model);
        Assert.Equal(new TokenUsage(3, 4), reply.Tokens);
        Assert.Equal(["Hel", "lo"], deltas);
        // The agent runs in the project root, with the assembled context.
        var request = claude.Requests.Single();
        Assert.Equal(Path.GetFullPath(root), request.WorkingDirectory);
        Assert.Equal(AgentRunMode.Chat, request.Mode);
        Assert.EndsWith("## Current request\nhi\n", request.Prompt);
        Assert.Equal("high", request.Effort);
    }

    [Fact]
    public async Task SwitchingAgents_KeepsOneSharedHistory()
    {
        var claude = new ScriptedRunner(AgentKind.Claude, (_, _) => Stream("from claude"));
        var codex = new ScriptedRunner(AgentKind.Codex, (_, _) => Stream("from codex"));
        await using var session = Create(claude, codex);

        await session.SendAsync(new ChatSendRequest("first", AgentKind.Claude));
        await session.SendAsync(new ChatSendRequest("second", AgentKind.Codex));

        Assert.Equal([ChatAuthor.User, ChatAuthor.Claude, ChatAuthor.User, ChatAuthor.Codex], session.Messages.Select(m => m.Author));
        var codexPrompt = codex.Requests.Single().Prompt;
        Assert.Contains("from claude", codexPrompt);
        Assert.Contains("first", codexPrompt);
    }

    [Fact]
    public async Task Failure_KeepsPartialContent_AndRecordsError()
    {
        var claude = new ScriptedRunner(AgentKind.Claude, (_, _) => FailAfterPartial());
        await using var session = Create(claude);

        await session.SendAsync(new ChatSendRequest("hi", AgentKind.Claude));

        var reply = session.Messages[1];
        Assert.Equal(ChatMessageState.Failed, reply.State);
        Assert.Equal("partial answ", reply.Content);
        Assert.Equal("rate limited", reply.Error);
        Assert.False(session.IsBusy);

        static async IAsyncEnumerable<AgentEvent> FailAfterPartial()
        {
            await Task.Yield();
            yield return new AgentEvent(AgentEventKind.Text, "partial answ");
            yield return new AgentEvent(AgentEventKind.Error, "rate limited");
            yield return new AgentEvent(AgentEventKind.Exit, ExitCode: 1);
        }
    }

    [Fact]
    public async Task Cancel_StopsReply_KeepsPartial_AndAllowsNextSend()
    {
        var started = new TaskCompletionSource();
        var claude = new ScriptedRunner(AgentKind.Claude, (_, ct) => ScriptedRunner.UntilCancelled(started, ct));
        await using var session = Create(claude);

        var sending = session.SendAsync(new ChatSendRequest("long", AgentKind.Claude));
        await started.Task;
        Assert.True(session.IsBusy);
        Assert.Throws<InvalidOperationException>(() => { _ = session.SendAsync(new ChatSendRequest("again", AgentKind.Claude)); });
        session.Cancel();
        await sending;

        var reply = session.Messages[1];
        Assert.Equal(ChatMessageState.Cancelled, reply.State);
        Assert.Equal("partial ", reply.Content);
        Assert.False(session.IsBusy);
    }

    [Fact]
    public async Task History_PersistsAtomically_AndReloads()
    {
        var claude = new ScriptedRunner(AgentKind.Claude, (_, _) => Stream("persisted"));
        await using (var session = Create(claude))
            await session.SendAsync(new ChatSendRequest("save me", AgentKind.Claude));

        var folder = Path.Combine(root, ".taverna");
        Assert.True(File.Exists(Path.Combine(folder, "chat.json")));
        Assert.Empty(Directory.GetFiles(folder, "*.tmp"));

        await using var reopened = Create(claude);
        await reopened.LoadAsync();
        Assert.Equal(["save me", "persisted"], reopened.Messages.Select(m => m.Content));
    }

    [Fact]
    public async Task Load_MarksReplyCutOffMidStreamAsInterrupted()
    {
        var store = new ChatStore(root);
        await store.SaveAsync(new ChatHistory
        {
            Messages =
            [
                new ChatMessage { Id = "u", Author = ChatAuthor.User, Content = "q" },
                new ChatMessage { Id = "a", Author = ChatAuthor.Codex, Content = "half", ReplyToId = "u", State = ChatMessageState.Streaming }
            ]
        });

        var loaded = await store.LoadAsync();

        Assert.Equal(ChatMessageState.Failed, loaded.Messages[1].State);
        Assert.Equal("Interrupted", loaded.Messages[1].Error);
        Assert.Equal("half", loaded.Messages[1].Content);
    }

    [Fact]
    public async Task Send_RejectsAttachmentsForAgentsWithoutImageSupport()
    {
        var copilot = new ScriptedRunner(AgentKind.Copilot, (_, _) => Stream("x"), images: false);
        await using var session = Create(copilot);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            session.SendAsync(new ChatSendRequest("look", AgentKind.Copilot) { Attachments = [".taverna/attachments/a.png"] }));
        Assert.Empty(copilot.Requests);
    }
}
