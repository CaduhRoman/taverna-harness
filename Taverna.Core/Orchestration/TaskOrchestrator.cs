using System.Text;
using Taverna.Core.Agents;
using Taverna.Core.Git;

namespace Taverna.Core.Orchestration;

public sealed class TaskLimitException(string message) : InvalidOperationException(message);

/// <summary>
/// Runs up to three agent tasks in parallel (one per agent), each in its own git worktree on
/// branch agent/&lt;agent&gt;/&lt;slug&gt;. Events fire on background threads.
/// </summary>
public sealed class TaskOrchestrator : IAsyncDisposable
{
    public const int MaxConcurrentTasks = 3;

    private readonly string projectRoot;
    private readonly GitService git;
    private readonly IReadOnlyDictionary<AgentKind, IAgentRunner> runners;
    private readonly TaskStore store;
    private readonly TimeSpan pollInterval;
    private readonly object sync = new();
    private readonly List<AgentTask> tasks = [];
    private readonly Dictionary<string, Running> running = [];
    private readonly SemaphoreSlim persistGate = new(1, 1);
    private bool shuttingDown;

    private sealed record Running(CancellationTokenSource Cancel, Task Completion);

    /// <summary>A task's public state changed (status, files, tokens, merge).</summary>
    public event Action<AgentTask>? TaskChanged;
    /// <summary>A task was removed by Discard.</summary>
    public event Action<string>? TaskRemoved;
    /// <summary>Text appended to a task's log, already formatted.</summary>
    public event Action<string, string>? TaskLog;

    public TaskOrchestrator(string projectRoot, GitService git, IReadOnlyDictionary<AgentKind, IAgentRunner> runners,
        TaskStore? store = null, TimeSpan? pollInterval = null)
    {
        this.projectRoot = Path.GetFullPath(projectRoot);
        this.git = git;
        this.runners = runners;
        this.store = store ?? new TaskStore(this.projectRoot);
        this.pollInterval = pollInterval ?? TimeSpan.FromSeconds(2);
    }

    public IReadOnlyList<AgentTask> Tasks
    {
        get { lock (sync) return tasks.Select(t => t.Clone()).ToList(); }
    }

    public int ActiveCount
    {
        get { lock (sync) return tasks.Count(t => t.IsActive); }
    }

    public async Task LoadAsync(CancellationToken ct = default)
    {
        var loaded = await store.LoadAsync(ct);
        lock (sync)
        {
            tasks.Clear();
            tasks.AddRange(loaded);
        }
    }

    /// <summary>Why a new task for this agent cannot start right now, or null.</summary>
    public string? CannotStartReason(AgentKind agent)
    {
        lock (sync) return CannotStartReasonLocked(agent);
    }

    public Task<AgentTask> StartAsync(TaskRequest request, CancellationToken ct = default) => CreateAsync(request, true, null, ct);

    public Task<AgentTask> EnqueueAsync(TaskRequest request, CancellationToken ct = default) => CreateAsync(request, false, null, ct);

    public Task<AgentTask> StartQueuedAsync(string id, CancellationToken ct = default)
    {
        var task = Find(id);
        return CreateAsync(new TaskRequest(task.Title, task.Agent, task.Prompt)
        { Model = task.Model, Effort = task.Effort, Attachments = task.Attachments }, true, id, ct);
    }

    private async Task<AgentTask> CreateAsync(TaskRequest request, bool start, string? queuedId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Title)) throw new ArgumentException("Title is required.");
        if (string.IsNullOrWhiteSpace(request.Prompt)) throw new ArgumentException("Prompt is required.");
        if (!AgentRunnerCatalog.TaskAgents.Contains(request.Agent) || !runners.TryGetValue(request.Agent, out var runner))
            throw new TaskLimitException("Unsupported task agent.");
        if (request.Attachments.Count > 0 && !runner.Capabilities.SupportsImages)
            throw new ArgumentException("This agent does not support image context.");

        var task = new AgentTask
        {
            Id = Guid.NewGuid().ToString("N")[..12],
            Title = request.Title.Trim(),
            Prompt = request.Prompt,
            Agent = request.Agent,
            Model = request.Model,
            Effort = request.Effort,
            Attachments = [.. request.Attachments],
            CreatedAt = DateTimeOffset.Now,
            Status = start ? AgentTaskStatus.Idle : AgentTaskStatus.Queued
        };
        task.LogPath = Path.Combine(".taverna", "tasks", $"{task.Id}.log");

        // Reserve the slot before the slow worktree creation so concurrent starts respect the limits.
        lock (sync)
        {
            if (start && CannotStartReasonLocked(request.Agent) is { } reason) throw new TaskLimitException(reason);
            if (queuedId is not null)
            {
                task = tasks.FirstOrDefault(t => t.Id == queuedId && t.Status == AgentTaskStatus.Queued)
                    ?? throw new InvalidOperationException("Task is no longer queued.");
                task.Status = AgentTaskStatus.Idle;
            }
            else tasks.Add(task);
        }

        if (!start) { await PersistAsync(); Raise(task); return Snapshot(task); }

        try
        {
            var worktree = await git.CreateTaskWorktreeAsync(projectRoot, AgentNames.Slug(request.Agent), request.Title, ct);
            lock (sync)
            {
                task.RepositoryRoot = worktree.RepositoryRoot;
                task.WorktreePath = worktree.Directory;
                task.Branch = worktree.Branch;
                task.BaseCommit = worktree.BaseCommit;
                task.TargetBranch = worktree.TargetBranch;
                task.Status = AgentTaskStatus.Working;
                task.StartedAt = DateTimeOffset.Now;
            }
        }
        catch (Exception ex)
        {
            lock (sync)
            {
                if (queuedId is not null) task.Status = AgentTaskStatus.Queued;
                else tasks.Remove(task);
            }
            throw new InvalidOperationException($"Could not create worktree: {ex.Message}", ex);
        }

        await AppendLogAsync(task, $"Task \"{task.Title}\" · {AgentNames.Display(task.Agent)}\n" +
            $"Branch {task.Branch} from {task.TargetBranch} @ {task.BaseCommit[..Math.Min(12, task.BaseCommit.Length)]}\n" +
            $"Worktree {task.WorktreePath}\n\n--- Prompt ---\n{task.Prompt}\n--- Output ---\n");
        await PersistAsync();
        Raise(task);

        var cancel = new CancellationTokenSource();
        var registered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = Task.Run(async () =>
        {
            await registered.Task;
            await ExecuteAsync(task, cancel.Token);
        }, CancellationToken.None);
        // Registered before the run may finish, so its own removal always finds the entry.
        lock (sync) running[task.Id] = new Running(cancel, completion);
        registered.SetResult();
        return Snapshot(task);
    }

    public bool Cancel(string id)
    {
        lock (sync)
        {
            if (!running.TryGetValue(id, out var run)) return false;
            run.Cancel.Cancel();
            return true;
        }
    }

    /// <summary>Waits for a task's background run to finish; for tests and shutdown.</summary>
    public Task WaitAsync(string id)
    {
        lock (sync) return running.TryGetValue(id, out var run) ? run.Completion : Task.CompletedTask;
    }

    public async Task<GitMergeResult> MergeAsync(string id, CancellationToken ct = default)
    {
        var task = Find(id);
        if (task.IsActive) throw new InvalidOperationException("Task is still running.");
        if (!Directory.Exists(task.WorktreePath)) throw new DirectoryNotFoundException("The task worktree no longer exists.");

        GitMergeResult result;
        try { result = await git.MergeAsync(task.ToWorktree(), ct); }
        catch (Exception ex) when (ex is InvalidOperationException or GitCommandException)
        {
            result = new GitMergeResult(false, false, [], ex.Message);
        }

        lock (sync)
        {
            task.MergeState = result.Merged ? TaskMergeState.Merged : result.HadConflicts ? TaskMergeState.Conflict : TaskMergeState.Failed;
            task.ConflictFiles = [.. result.ConflictPaths];
            task.MergeMessage = result.Merged ? $"Merged into {task.TargetBranch}" : result.Message;
        }
        await AppendLogAsync(task, $"\n--- Merge: {task.MergeState} ---\n{task.MergeMessage}\n" +
            (result.ConflictPaths.Count > 0 ? string.Join("\n", result.ConflictPaths) + "\n" : ""));
        await RefreshChangesAsync(task, compareWithBase: true, CancellationToken.None);
        await PersistAsync();
        Raise(task);
        return result;
    }

    /// <summary>True when Discard would throw away work not yet in the project; ask the user first.</summary>
    public async Task<bool> DiscardNeedsConfirmationAsync(string id, CancellationToken ct = default)
    {
        var task = Find(id);
        if (string.IsNullOrEmpty(task.Branch)) return false;
        return await git.HasUnmergedWorkAsync(task.ToWorktree(), ct);
    }

    /// <summary>Removes worktree, branch and record. Unmerged work is only dropped when <paramref name="confirmed"/>.</summary>
    public async Task DiscardAsync(string id, bool confirmed, CancellationToken ct = default)
    {
        var task = Find(id);
        if (task.IsActive) throw new InvalidOperationException("Cancel the task before discarding it.");
        if (!string.IsNullOrEmpty(task.Branch))
        {
            if (await git.HasUnmergedWorkAsync(task.ToWorktree(), ct))
            {
                if (!confirmed) throw new InvalidOperationException("The task has unmerged changes; confirmation required.");
                await git.DiscardWorktreeAsync(task.ToWorktree(), ct);
            }
            else if (Directory.Exists(task.WorktreePath))
                await git.RemoveWorktreeAsync(task.ToWorktree(), ct);
            else
                await git.DiscardWorktreeAsync(task.ToWorktree(), ct); // Only prunes registration and a fully merged branch.
        }

        lock (sync) tasks.Remove(task);
        try { File.Delete(Path.Combine(projectRoot, task.LogPath)); }
        catch (IOException) { }
        await PersistAsync();
        TaskRemoved?.Invoke(id);
    }

    /// <summary>The log, or only its last <paramref name="maxBytes"/> bytes when given.</summary>
    public string ReadLog(string id, int? maxBytes = null)
    {
        var path = Path.Combine(projectRoot, Find(id).LogPath);
        try
        {
            if (!File.Exists(path)) return "";
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (maxBytes is { } limit && stream.Length > limit) stream.Seek(-limit, SeekOrigin.End);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd();
        }
        catch (IOException) { return ""; }
    }

    /// <summary>Stops every running task. They are recorded as Interrupted, like after a crash, and never resumed.</summary>
    public async ValueTask DisposeAsync()
    {
        List<Running> active;
        lock (sync)
        {
            shuttingDown = true;
            active = [.. running.Values];
        }
        foreach (var run in active) run.Cancel.Cancel();
        try { await Task.WhenAll(active.Select(r => r.Completion)); }
        catch (Exception) { /* Each run records its own failure. */ }
    }

    // ---------- Execution ----------

    private async Task ExecuteAsync(AgentTask task, CancellationToken ct)
    {
        var runner = runners[task.Agent];
        var request = new AgentRequest(task.Prompt, task.WorktreePath)
        {
            Model = task.Model,
            Effort = task.Effort,
            Images = task.Attachments.Select(a => Path.GetFullPath(Path.Combine(projectRoot, a))).ToList(),
            Mode = AgentRunMode.Task
        };
        using var pollStop = new CancellationTokenSource();
        var poll = PollAsync(task, pollStop.Token);
        int? exitCode = null;
        var cancelled = false;
        string? lastError = null;

        try
        {
            await foreach (var e in runner.RunAsync(request, ct).WithCancellation(CancellationToken.None))
            {
                switch (e.Kind)
                {
                    case AgentEventKind.Text:
                        await AppendLogAsync(task, e.Text!);
                        break;
                    case AgentEventKind.Tokens:
                        lock (sync) task.Tokens = TokenUsage.Add(task.Tokens, e.Usage);
                        Raise(task);
                        break;
                    case AgentEventKind.Error:
                        lastError = e.Text;
                        await AppendLogAsync(task, $"\n[error] {e.Text}\n");
                        break;
                    case AgentEventKind.Status:
                    case AgentEventKind.Log:
                        await AppendLogAsync(task, $"\n[{e.Kind.ToString().ToLowerInvariant()}] {e.Text}\n");
                        break;
                    case AgentEventKind.Exit:
                        exitCode = e.ExitCode;
                        break;
                    case AgentEventKind.Cancelled:
                        cancelled = true;
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            lastError = ex.Message;
            await AppendLogAsync(task, $"\n[error] {ex.Message}\n");
        }
        finally
        {
            pollStop.Cancel();
            try { await poll; }
            catch (OperationCanceledException) { }
        }

        await RefreshChangesAsync(task, compareWithBase: true, CancellationToken.None);
        lock (sync)
        {
            task.FinishedAt = DateTimeOffset.Now;
            task.DurationMs = (long)(task.FinishedAt.Value - task.StartedAt!.Value).TotalMilliseconds;
            if (cancelled || ct.IsCancellationRequested)
            {
                task.Status = AgentTaskStatus.Error;
                task.Reason = shuttingDown ? AgentTask.InterruptedReason : AgentTask.CancelledReason;
            }
            else if (exitCode == 0)
            {
                task.Status = AgentTaskStatus.Done;
                task.Reason = null;
            }
            else
            {
                task.Status = AgentTaskStatus.Error;
                task.Reason = lastError ?? $"Exit code {exitCode?.ToString() ?? "unknown"}";
            }
            running.Remove(task.Id);
        }
        await AppendLogAsync(task, $"\n--- {task.Status}{(task.Reason is null ? "" : $": {task.Reason}")} · {TimeSpan.FromMilliseconds(task.DurationMs):hh\\:mm\\:ss} ---\n");
        await PersistAsync();
        Raise(task);
    }

    private async Task PollAsync(AgentTask task, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(pollInterval, ct);
            lock (sync) task.DurationMs = (long)(DateTimeOffset.Now - task.StartedAt!.Value).TotalMilliseconds;
            await RefreshChangesAsync(task, compareWithBase: false, ct);
            await PersistAsync();
            Raise(task);
        }
    }

    /// <summary>During a run `git status --porcelain` (untracked included); at the end, the full diff against the base commit.</summary>
    private async Task RefreshChangesAsync(AgentTask task, bool compareWithBase, CancellationToken ct)
    {
        if (!Directory.Exists(task.WorktreePath)) return;
        try
        {
            var changes = compareWithBase
                ? (await git.CompareWithBaseAsync(task.ToWorktree(), ct)).Changes
                : await git.GetChangedFilesAsync(task.ToWorktree(), ct);
            lock (sync) task.ChangedFiles = [.. changes];
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            await AppendLogAsync(task, $"\n[log] Could not read changed files: {ex.Message}\n");
        }
    }

    // ---------- Helpers ----------

    private string? CannotStartReasonLocked(AgentKind agent)
    {
        if (!AgentRunnerCatalog.TaskAgents.Contains(agent)) return $"{AgentNames.Display(agent)} cannot run tasks.";
        if (!runners.TryGetValue(agent, out var runner) || !runner.IsAvailable()) return $"{AgentNames.Display(agent)} is not available.";
        if (tasks.Count(t => t.IsActive) >= MaxConcurrentTasks) return $"At most {MaxConcurrentTasks} tasks can run at once.";
        if (tasks.Any(t => t.IsActive && t.Agent == agent)) return $"{AgentNames.Display(agent)} is already running a task.";
        return null;
    }

    private AgentTask Find(string id)
    {
        lock (sync) return tasks.FirstOrDefault(t => t.Id == id) ?? throw new KeyNotFoundException($"Task {id} not found.");
    }

    private AgentTask Snapshot(AgentTask task)
    {
        lock (sync) return task.Clone();
    }

    private void Raise(AgentTask task) => TaskChanged?.Invoke(Snapshot(task));

    private readonly SemaphoreSlim logGate = new(1, 1);

    private async Task AppendLogAsync(AgentTask task, string text)
    {
        var path = Path.Combine(projectRoot, task.LogPath);
        await logGate.WaitAsync();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.AppendAllTextAsync(path, text, Encoding.UTF8);
        }
        catch (IOException) { /* The live stream below still carries the text. */ }
        finally { logGate.Release(); }
        TaskLog?.Invoke(task.Id, text);
    }

    private async Task PersistAsync()
    {
        await persistGate.WaitAsync();
        try
        {
            List<AgentTask> snapshot;
            lock (sync) snapshot = tasks.Select(t => t.Clone()).ToList();
            await store.SaveAsync(snapshot);
        }
        catch (IOException) { /* Next state change retries. */ }
        finally { persistGate.Release(); }
    }
}
