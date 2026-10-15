using System.Diagnostics;
using Taverna.Core.Agents;
using Taverna.Core.Context;

namespace Taverna.Core.Chat;

public sealed record ChatSendRequest(string Text, AgentKind Agent)
{
    public string? Model { get; init; }
    public string? Effort { get; init; }
    public AgentPermissionMode? PermissionMode { get; init; }
    /// <summary>Paths relative to the project root (see <see cref="AttachmentStore"/>).</summary>
    public IReadOnlyList<string> Attachments { get; init; } = [];
}

public sealed record RepositoryHead(string? Branch, string? Commit);

/// <summary>
/// The single shared conversation of a project. Every agent sees the same history; the agent always
/// runs in the project root. Events fire on background threads.
/// </summary>
public sealed class ChatSession : IAsyncDisposable
{
    private static readonly TimeSpan StreamingSaveInterval = TimeSpan.FromSeconds(2);

    private readonly string projectRoot;
    private readonly IReadOnlyDictionary<AgentKind, IAgentRunner> runners;
    private readonly ChatStore store;
    private readonly ContextBuilder context;
    private readonly Func<CancellationToken, Task<RepositoryHead>> head;
    private readonly object sync = new();
    private ChatHistory history = new();
    private CancellationTokenSource? running;
    private Task current = Task.CompletedTask;

    public event Action<ChatMessage>? MessageAdded;
    /// <summary>Streaming text appended to a reply: (message id, delta).</summary>
    public event Action<string, string>? MessageDelta;
    /// <summary>A reply reached its final state (Complete, Failed, Cancelled).</summary>
    public event Action<ChatMessage>? MessageCompleted;
    /// <summary>What the session is doing ("Preparing", "Responding"), or null when idle.</summary>
    public event Action<string?>? ActivityChanged;

    public ChatSession(string projectRoot, IReadOnlyDictionary<AgentKind, IAgentRunner> runners, ContextBuilder context,
        Func<CancellationToken, Task<RepositoryHead>> head, ChatStore? store = null)
    {
        this.projectRoot = Path.GetFullPath(projectRoot);
        this.runners = runners;
        this.context = context;
        this.head = head;
        this.store = store ?? new ChatStore(this.projectRoot);
    }

    public string ProjectRoot => projectRoot;

    public bool IsBusy
    {
        get { lock (sync) return running is not null; }
    }

    public IReadOnlyList<ChatMessage> Messages
    {
        get { lock (sync) return history.Messages.Select(m => m.Clone()).ToList(); }
    }

    public async Task LoadAsync(CancellationToken ct = default)
    {
        var loaded = await store.LoadAsync(ct);
        lock (sync) history = loaded;
    }

    /// <summary>Sends a message and streams the reply. Completes when the reply is final.</summary>
    public Task SendAsync(ChatSendRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Text) && request.Attachments.Count == 0)
            throw new ArgumentException("Message is empty.");
        if (!runners.TryGetValue(request.Agent, out var runner) || !runner.IsAvailable())
            throw new InvalidOperationException($"{AgentNames.Display(request.Agent)} is not available.");
        if (request.Attachments.Count > 0 && !runner.Capabilities.SupportsImages)
            throw new InvalidOperationException($"{AgentNames.Display(request.Agent)} does not accept image attachments.");
        if (AgentPermissions.Validate(request.Agent, request.PermissionMode) is { } permissionError)
            throw new InvalidOperationException(permissionError);

        var cancel = new CancellationTokenSource();
        lock (sync)
        {
            if (running is not null) throw new InvalidOperationException("An agent is already responding.");
            running = cancel;
            current = RunAsync(request, runner, cancel);
            return current;
        }
    }

    public void Cancel()
    {
        lock (sync) running?.Cancel();
    }

    public async ValueTask DisposeAsync()
    {
        Cancel();
        Task pending;
        lock (sync) pending = current;
        try { await pending; }
        catch (Exception) { /* Recorded on the message. */ }
    }

    private async Task RunAsync(ChatSendRequest request, IAgentRunner runner, CancellationTokenSource cancel)
    {
        var ct = cancel.Token;
        ChatMessage? reply = null;
        try
        {
            RepositoryHead repo;
            try { repo = await head(ct); }
            catch (Exception) when (!ct.IsCancellationRequested) { repo = new RepositoryHead(null, null); }

            List<ChatMessage> previous;
            SummaryCache? cache;
            lock (sync)
            {
                previous = history.Messages.Select(m => m.Clone()).ToList();
                cache = history.Summary;
            }

            var user = new ChatMessage
            {
                Id = NewId(), Author = ChatAuthor.User, Timestamp = DateTimeOffset.Now,
                Branch = repo.Branch, Commit = repo.Commit, Content = request.Text.Trim(),
                Attachments = [.. request.Attachments]
            };
            Add(user);
            await SaveAsync();

            ActivityChanged?.Invoke("Preparing");
            // Plan must not indirectly start a CLI summarizer with development tools.
            var builder = request.PermissionMode == AgentPermissionMode.Plan ? new ContextBuilder(context.Options) : context;
            var built = await builder.BuildAsync(previous, user.Content, user.Attachments, cache, ct);
            if (built.Summary is not null && built.Summary != cache)
                lock (sync) history.Summary = built.Summary;

            reply = new ChatMessage
            {
                Id = NewId(), Author = ChatMessage.AuthorFor(request.Agent), Timestamp = DateTimeOffset.Now,
                Branch = repo.Branch, Commit = repo.Commit, ReplyToId = user.Id,
                Model = request.Model, Effort = request.Effort, State = ChatMessageState.Streaming
            };
            Add(reply);
            ActivityChanged?.Invoke("Responding");

            var agentRequest = new AgentRequest(built.Prompt, projectRoot)
            {
                Model = request.Model,
                Effort = request.Effort,
                PermissionMode = request.PermissionMode,
                Images = request.Attachments.Select(a => Path.GetFullPath(Path.Combine(projectRoot, a))).ToList(),
                Mode = AgentRunMode.Chat
            };
            await StreamAsync(runner, agentRequest, reply, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            if (reply is not null) Finish(reply, ChatMessageState.Cancelled, null);
        }
        catch (Exception ex)
        {
            if (reply is not null) Finish(reply, ChatMessageState.Failed, ex.Message);
            else throw;
        }
        finally
        {
            lock (sync) running = null;
            cancel.Dispose();
            ActivityChanged?.Invoke(null);
            await SaveAsync();
        }
    }

    private async Task StreamAsync(IAgentRunner runner, AgentRequest request, ChatMessage reply, CancellationToken ct)
    {
        var lastSave = Stopwatch.StartNew();
        int? exitCode = null;
        var cancelled = false;
        string? error = null;
        await foreach (var e in runner.RunAsync(request, ct).WithCancellation(CancellationToken.None))
        {
            switch (e.Kind)
            {
                case AgentEventKind.Text:
                    lock (sync) reply.Content += e.Text;
                    MessageDelta?.Invoke(reply.Id, e.Text!);
                    // Partial text survives a crash: persist periodically while streaming.
                    if (lastSave.Elapsed > StreamingSaveInterval)
                    {
                        lastSave.Restart();
                        await SaveAsync();
                    }
                    break;
                case AgentEventKind.Tokens:
                    lock (sync) reply.Tokens = TokenUsage.Add(reply.Tokens, e.Usage);
                    break;
                case AgentEventKind.Error:
                    error = e.Text;
                    break;
                case AgentEventKind.Exit:
                    exitCode = e.ExitCode;
                    break;
                case AgentEventKind.Cancelled:
                    cancelled = true;
                    break;
            }
        }

        if (cancelled || ct.IsCancellationRequested) Finish(reply, ChatMessageState.Cancelled, null);
        else if (exitCode == 0) Finish(reply, ChatMessageState.Complete, null);
        else Finish(reply, ChatMessageState.Failed, error ?? $"Exit code {exitCode?.ToString() ?? "unknown"}");
    }

    private void Finish(ChatMessage reply, ChatMessageState state, string? error)
    {
        ChatMessage snapshot;
        lock (sync)
        {
            if (reply.State != ChatMessageState.Streaming) return;
            reply.State = state;
            reply.Error = error;
            snapshot = reply.Clone();
        }
        MessageCompleted?.Invoke(snapshot);
    }

    private void Add(ChatMessage message)
    {
        ChatMessage snapshot;
        lock (sync)
        {
            history.Messages.Add(message);
            snapshot = message.Clone();
        }
        MessageAdded?.Invoke(snapshot);
    }

    private async Task SaveAsync()
    {
        ChatHistory snapshot;
        lock (sync)
            snapshot = new ChatHistory
            {
                Messages = history.Messages.Select(m => m.Clone()).ToList(),
                Summary = history.Summary
            };
        try { await store.SaveAsync(snapshot); }
        catch (IOException) { /* Retried on the next save. */ }
    }

    private static string NewId() => Guid.NewGuid().ToString("N")[..12];
}
