using System.Collections.Concurrent;
using System.Diagnostics;
using Taverna.Core.Agents;
using Taverna.Core.Git;
using Taverna.Core.Orchestration;
using Xunit;

namespace Taverna.Core.Tests;

public sealed class TaskOrchestratorTests
{
    private static Dictionary<AgentKind, IAgentRunner> Runners(params ScriptedRunner[] runners) =>
        runners.ToDictionary(r => r.Kind, r => (IAgentRunner)r);

    private static TaskOrchestrator Create(TemporaryGitRepository repo, params ScriptedRunner[] runners) =>
        new(repo.Root, repo.Service, Runners(runners), pollInterval: TimeSpan.FromMilliseconds(50));

    [Fact]
    public async Task Task_RunsInOwnWorktreeOnAgentBranch_AndRecordsResults()
    {
        using var repo = await TemporaryGitRepository.CreateAsync();
        var claude = new ScriptedRunner(AgentKind.Claude, (r, _) => ScriptedRunner.Edit(r, "agent.txt", "hello\n"));
        await using var orchestrator = Create(repo, claude);

        var started = await orchestrator.StartAsync(new TaskRequest("Fix Login Bug!", AgentKind.Claude, "do it"));
        await orchestrator.WaitAsync(started.Id);
        var task = orchestrator.Tasks.Single();

        Assert.Equal("agent/claude/fix-login-bug", task.Branch);
        Assert.Equal("main", task.TargetBranch);
        Assert.Equal(await TemporaryGitRepository.GitAsync(repo.Root, "rev-parse", "HEAD"), task.BaseCommit);
        Assert.Equal(AgentTaskStatus.Done, task.Status);
        Assert.Contains(task.ChangedFiles, f => f.Path == "agent.txt");
        Assert.Equal(new TokenUsage(10, 5), task.Tokens);
        Assert.True(task.DurationMs >= 0);
        // The agent ran in the worktree, never in the project root.
        Assert.Equal(task.WorktreePath, claude.Requests.Single().WorkingDirectory);
        Assert.Equal(AgentRunMode.Task, claude.Requests.Single().Mode);
        Assert.False(File.Exists(Path.Combine(repo.Root, "agent.txt")));
        var log = orchestrator.ReadLog(task.Id);
        Assert.Contains("do it", log);
        Assert.Contains("wrote agent.txt", log);
    }

    [Fact]
    public async Task BranchNameCollision_GetsSuffix()
    {
        using var repo = await TemporaryGitRepository.CreateAsync();
        await TemporaryGitRepository.GitAsync(repo.Root, "branch", "agent/codex/refactor");
        var codex = new ScriptedRunner(AgentKind.Codex, (_, _) => ScriptedRunner.Reply("ok"));
        await using var orchestrator = Create(repo, codex);

        var first = await orchestrator.StartAsync(new TaskRequest("Refactor", AgentKind.Codex, "p"));
        await orchestrator.WaitAsync(first.Id);
        var second = await orchestrator.StartAsync(new TaskRequest("Refactor", AgentKind.Codex, "p"));
        await orchestrator.WaitAsync(second.Id);

        Assert.Equal("agent/codex/refactor-2", first.Branch);
        Assert.Equal("agent/codex/refactor-3", second.Branch);
    }

    [Fact]
    public async Task Limits_OnePerAgent_AndOnlyTaskAgents()
    {
        using var repo = await TemporaryGitRepository.CreateAsync();
        var running = new TaskCompletionSource();
        var claude = new ScriptedRunner(AgentKind.Claude, (_, ct) => ScriptedRunner.UntilCancelled(running, ct));
        var api = new ScriptedRunner(AgentKind.ExternalApi, (_, _) => ScriptedRunner.Reply("x"));
        var copilot = new ScriptedRunner(AgentKind.Copilot, (_, _) => ScriptedRunner.Reply("x"), available: false);
        await using var orchestrator = Create(repo, claude, api, copilot);

        var first = await orchestrator.StartAsync(new TaskRequest("A", AgentKind.Claude, "p"));
        await running.Task;
        await Assert.ThrowsAsync<TaskLimitException>(() => orchestrator.StartAsync(new TaskRequest("B", AgentKind.Claude, "p")));
        await Assert.ThrowsAsync<TaskLimitException>(() => orchestrator.StartAsync(new TaskRequest("C", AgentKind.ExternalApi, "p")));
        await Assert.ThrowsAsync<TaskLimitException>(() => orchestrator.StartAsync(new TaskRequest("D", AgentKind.Copilot, "p")));
        Assert.Single(orchestrator.Tasks);

        orchestrator.Cancel(first.Id);
        await orchestrator.WaitAsync(first.Id);
        Assert.Null(orchestrator.CannotStartReason(AgentKind.Claude));
    }

    [Fact]
    public async Task ThreeAgents_RunConcurrently()
    {
        using var repo = await TemporaryGitRepository.CreateAsync();
        var gates = Enumerable.Range(0, 3).Select(_ => new TaskCompletionSource()).ToArray();
        var runners = new[] { AgentKind.Claude, AgentKind.Codex, AgentKind.Copilot }
            .Select((kind, i) => new ScriptedRunner(kind, (_, ct) => ScriptedRunner.UntilCancelled(gates[i], ct))).ToArray();
        await using var orchestrator = Create(repo, runners);

        var started = new List<AgentTask>();
        foreach (var kind in new[] { AgentKind.Claude, AgentKind.Codex, AgentKind.Copilot })
            started.Add(await orchestrator.StartAsync(new TaskRequest($"Task {kind}", kind, "p")));
        await Task.WhenAll(gates.Select(g => g.Task));

        Assert.Equal(3, orchestrator.Tasks.Count(t => t.Status == AgentTaskStatus.Working));
        Assert.Equal(3, started.Select(t => t.WorktreePath).Distinct().Count());
        foreach (var task in started) orchestrator.Cancel(task.Id);
        foreach (var task in started) await orchestrator.WaitAsync(task.Id);
    }

    [Fact]
    public async Task StateTransitions_WorkingToDone_AndFailureToError()
    {
        using var repo = await TemporaryGitRepository.CreateAsync();
        var claude = new ScriptedRunner(AgentKind.Claude, (_, _) => ScriptedRunner.Reply("ok"));
        var codex = new ScriptedRunner(AgentKind.Codex, (_, _) => Failing());
        await using var orchestrator = Create(repo, claude, codex);
        var seen = new ConcurrentQueue<(string Id, AgentTaskStatus Status)>();
        orchestrator.TaskChanged += t => seen.Enqueue((t.Id, t.Status));

        var ok = await orchestrator.StartAsync(new TaskRequest("ok", AgentKind.Claude, "p"));
        var bad = await orchestrator.StartAsync(new TaskRequest("bad", AgentKind.Codex, "p"));
        await orchestrator.WaitAsync(ok.Id);
        await orchestrator.WaitAsync(bad.Id);

        Assert.Equal(AgentTaskStatus.Working, ok.Status);
        Assert.Equal([AgentTaskStatus.Working, AgentTaskStatus.Done], Distinct(seen.Where(s => s.Id == ok.Id).Select(s => s.Status)));
        var failed = orchestrator.Tasks.Single(t => t.Id == bad.Id);
        Assert.Equal(AgentTaskStatus.Error, failed.Status);
        Assert.Equal("model unavailable", failed.Reason);

        static async IAsyncEnumerable<AgentEvent> Failing()
        {
            await Task.Yield();
            yield return new AgentEvent(AgentEventKind.Error, "model unavailable");
            yield return new AgentEvent(AgentEventKind.Exit, ExitCode: 1);
        }
    }

    [Fact]
    public async Task Cancel_MarksErrorCancelled_AndKeepsPartialLog()
    {
        using var repo = await TemporaryGitRepository.CreateAsync();
        var running = new TaskCompletionSource();
        var claude = new ScriptedRunner(AgentKind.Claude, (_, ct) => ScriptedRunner.UntilCancelled(running, ct));
        await using var orchestrator = Create(repo, claude);

        var task = await orchestrator.StartAsync(new TaskRequest("long", AgentKind.Claude, "p"));
        await running.Task;
        Assert.True(orchestrator.Cancel(task.Id));
        await orchestrator.WaitAsync(task.Id);

        var result = orchestrator.Tasks.Single();
        Assert.Equal(AgentTaskStatus.Error, result.Status);
        Assert.Equal(AgentTask.CancelledReason, result.Reason);
        Assert.Contains("partial", orchestrator.ReadLog(task.Id));
        Assert.False(orchestrator.Cancel(task.Id));
    }

    [Fact]
    public async Task Merge_CommitsWorktreeChangesIntoCurrentBranch()
    {
        using var repo = await TemporaryGitRepository.CreateAsync();
        var claude = new ScriptedRunner(AgentKind.Claude, (r, _) => ScriptedRunner.Edit(r, "feature.txt", "feature\n"));
        await using var orchestrator = Create(repo, claude);
        var task = await orchestrator.StartAsync(new TaskRequest("feature", AgentKind.Claude, "p"));
        await orchestrator.WaitAsync(task.Id);

        var result = await orchestrator.MergeAsync(task.Id);

        Assert.True(result.Merged);
        Assert.Equal("feature\n", await File.ReadAllTextAsync(Path.Combine(repo.Root, "feature.txt")));
        Assert.Equal(TaskMergeState.Merged, orchestrator.Tasks.Single().MergeState);
        Assert.True(Directory.Exists(task.WorktreePath));
        Assert.False(await orchestrator.DiscardNeedsConfirmationAsync(task.Id));
    }

    [Fact]
    public async Task MergeConflict_AbortsAndKeepsWorktreeForRetry()
    {
        using var repo = await TemporaryGitRepository.CreateAsync();
        var claude = new ScriptedRunner(AgentKind.Claude, (r, _) => ScriptedRunner.Edit(r, "shared.txt", "agent\n"));
        await using var orchestrator = Create(repo, claude);
        var task = await orchestrator.StartAsync(new TaskRequest("conflict", AgentKind.Claude, "p"));
        await orchestrator.WaitAsync(task.Id);
        await File.WriteAllTextAsync(Path.Combine(repo.Root, "shared.txt"), "user\n");
        await TemporaryGitRepository.GitAsync(repo.Root, "commit", "-am", "user edit");

        var result = await orchestrator.MergeAsync(task.Id);

        Assert.True(result.HadConflicts);
        var recorded = orchestrator.Tasks.Single();
        Assert.Equal(TaskMergeState.Conflict, recorded.MergeState);
        Assert.Contains("shared.txt", recorded.ConflictFiles);
        Assert.Equal("user\n", await File.ReadAllTextAsync(Path.Combine(repo.Root, "shared.txt")));
        Assert.False(File.Exists(Path.Combine(repo.Root, ".git", "MERGE_HEAD")));
        Assert.True(Directory.Exists(task.WorktreePath));
    }

    [Fact]
    public async Task Merge_WithUntrackedFileInProject_IsAllowedWhenNotOverwritten()
    {
        using var repo = await TemporaryGitRepository.CreateAsync();
        await File.WriteAllTextAsync(Path.Combine(repo.Root, "notes.local"), "mine\n");
        var claude = new ScriptedRunner(AgentKind.Claude, (r, _) => ScriptedRunner.Edit(r, "feature.txt", "x\n"));
        await using var orchestrator = Create(repo, claude);
        var task = await orchestrator.StartAsync(new TaskRequest("feature", AgentKind.Claude, "p"));
        await orchestrator.WaitAsync(task.Id);

        Assert.True((await orchestrator.MergeAsync(task.Id)).Merged);
        Assert.Equal("mine\n", await File.ReadAllTextAsync(Path.Combine(repo.Root, "notes.local")));
    }

    [Fact]
    public async Task Merge_WouldOverwriteUntrackedFile_FailsWithoutTouchingIt()
    {
        using var repo = await TemporaryGitRepository.CreateAsync();
        var claude = new ScriptedRunner(AgentKind.Claude, (r, _) => ScriptedRunner.Edit(r, "clash.txt", "agent\n"));
        await using var orchestrator = Create(repo, claude);
        var task = await orchestrator.StartAsync(new TaskRequest("clash", AgentKind.Claude, "p"));
        await orchestrator.WaitAsync(task.Id);
        await File.WriteAllTextAsync(Path.Combine(repo.Root, "clash.txt"), "user\n");

        var result = await orchestrator.MergeAsync(task.Id);

        Assert.False(result.Merged);
        Assert.False(result.HadConflicts);
        Assert.Equal(TaskMergeState.Failed, orchestrator.Tasks.Single().MergeState);
        Assert.Equal("user\n", await File.ReadAllTextAsync(Path.Combine(repo.Root, "clash.txt")));
    }

    [Fact]
    public async Task Discard_RequiresConfirmationForUnmergedWork()
    {
        using var repo = await TemporaryGitRepository.CreateAsync();
        var claude = new ScriptedRunner(AgentKind.Claude, (r, _) => ScriptedRunner.Edit(r, "wip.txt", "wip\n"));
        await using var orchestrator = Create(repo, claude);
        var task = await orchestrator.StartAsync(new TaskRequest("wip", AgentKind.Claude, "p"));
        await orchestrator.WaitAsync(task.Id);

        Assert.True(await orchestrator.DiscardNeedsConfirmationAsync(task.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => orchestrator.DiscardAsync(task.Id, confirmed: false));
        Assert.True(Directory.Exists(task.WorktreePath));

        await orchestrator.DiscardAsync(task.Id, confirmed: true);
        Assert.False(Directory.Exists(task.WorktreePath));
        Assert.Empty(await TemporaryGitRepository.GitAsync(repo.Root, "branch", "--list", task.Branch));
        Assert.Empty(orchestrator.Tasks);
    }

    [Fact]
    public async Task Tasks_PersistAndRecoverInterruptedOnReload()
    {
        using var repo = await TemporaryGitRepository.CreateAsync();
        var running = new TaskCompletionSource();
        var claude = new ScriptedRunner(AgentKind.Claude, (_, ct) => ScriptedRunner.UntilCancelled(running, ct));
        var codex = new ScriptedRunner(AgentKind.Codex, (_, _) => ScriptedRunner.Reply("ok"));
        var orchestrator = Create(repo, claude, codex);
        var done = await orchestrator.StartAsync(new TaskRequest("done", AgentKind.Codex, "p"));
        await orchestrator.WaitAsync(done.Id);
        var live = await orchestrator.StartAsync(new TaskRequest("live", AgentKind.Claude, "p"));
        await running.Task;

        // Simulate the app dying: copy the file while the task is still Working.
        var file = Path.Combine(repo.Root, ".taverna", "tasks.json");
        var snapshot = await File.ReadAllTextAsync(file);
        await orchestrator.DisposeAsync();
        await File.WriteAllTextAsync(file, snapshot);

        var reopened = Create(repo, claude, codex);
        await reopened.LoadAsync();
        var tasks = reopened.Tasks;
        var recovered = tasks.Single(t => t.Id == live.Id);
        Assert.Equal(AgentTaskStatus.Error, recovered.Status);
        Assert.Equal(AgentTask.InterruptedReason, recovered.Reason);
        Assert.Equal(AgentTaskStatus.Done, tasks.Single(t => t.Id == done.Id).Status);
        Assert.Single(claude.Requests); // Not restarted.
        Assert.Contains("\"interrupted\"", (await File.ReadAllTextAsync(file)).ToLowerInvariant());
    }

    [Fact]
    public async Task TaskStore_RoundTripsAllFields()
    {
        var root = Directory.CreateTempSubdirectory("taverna-store-").FullName;
        try
        {
            var store = new TaskStore(root);
            var original = new AgentTask
            {
                Id = "abc", Title = "T", Prompt = "P", Agent = AgentKind.Codex, Model = "m", Effort = "high",
                RepositoryRoot = root, WorktreePath = "w", Branch = "agent/codex/t", BaseCommit = "1234", TargetBranch = "main",
                Status = AgentTaskStatus.Done, ChangedFiles = [new GitFileChange("a.cs", "M")], Tokens = new TokenUsage(1, 2, 3),
                CreatedAt = DateTimeOffset.Now, DurationMs = 42, LogPath = ".taverna/tasks/abc.log",
                MergeState = TaskMergeState.Conflict, ConflictFiles = ["a.cs"]
            };
            await store.SaveAsync([original]);
            var loaded = (await store.LoadAsync()).Single();

            Assert.Equal(original.Branch, loaded.Branch);
            Assert.Equal(original.Tokens, loaded.Tokens);
            Assert.Equal("a.cs", loaded.ChangedFiles.Single().Path);
            Assert.Equal(TaskMergeState.Conflict, loaded.MergeState);
            Assert.Equal(AgentKind.Codex, loaded.Agent);
            Assert.Equal(42, loaded.DurationMs);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task TaskStore_CorruptFile_IsQuarantinedNotOverwritten()
    {
        var root = Directory.CreateTempSubdirectory("taverna-store-").FullName;
        try
        {
            var store = new TaskStore(root);
            Directory.CreateDirectory(Path.GetDirectoryName(store.FilePath)!);
            await File.WriteAllTextAsync(store.FilePath, "{ not json");
            Assert.Empty(await store.LoadAsync());
            Assert.Single(Directory.GetFiles(Path.GetDirectoryName(store.FilePath)!, "tasks.json.corrupt-*"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task CliRunner_CancelKillsWholeProcessTree()
    {
        if (!OperatingSystem.IsWindows()) return; // The script below is PowerShell.
        var dir = Directory.CreateTempSubdirectory("taverna-kill-").FullName;
        try
        {
            var pidFile = Path.Combine(dir, "child.pid");
            var runner = new SleepingCliRunner(pidFile);
            using var cts = new CancellationTokenSource();
            int? parentPid = null;
            var events = new List<AgentEvent>();
            await foreach (var e in runner.RunAsync(new AgentRequest("x", dir), cts.Token))
            {
                events.Add(e);
                if (e.Kind == AgentEventKind.Status && e.Text!.Contains("PID"))
                {
                    parentPid = int.Parse(e.Text.Split("PID ")[1].TrimEnd(')'));
                    for (var i = 0; i < 100 && !File.Exists(pidFile); i++) await Task.Delay(100);
                    await Task.Delay(300);
                    cts.Cancel();
                }
            }
            var childPid = int.Parse((await File.ReadAllTextAsync(pidFile)).Trim());

            Assert.Equal(AgentEventKind.Cancelled, events[^1].Kind);
            Assert.True(Exited(parentPid!.Value));
            Assert.True(Exited(childPid));
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }

        static bool Exited(int pid)
        {
            try { using var p = Process.GetProcessById(pid); return p.WaitForExit(5000); }
            catch (ArgumentException) { return true; }
        }
    }

    private sealed class SleepingCliRunner(string pidFile) : CliAgentRunner
    {
        public override AgentKind Kind => AgentKind.Claude;
        public override string Name => "sleeper";
        public override AgentCapabilities Capabilities => new([], [], false);
        protected override string Command => "powershell";
        protected override IReadOnlyList<string> Arguments(AgentRequest request, string workingDirectory) =>
        [
            "-NoProfile", "-Command",
            $"$p = Start-Process ping -ArgumentList '-n','120','127.0.0.1' -PassThru -WindowStyle Hidden; " +
            $"Set-Content -Path '{pidFile}' -Value $p.Id; Wait-Process -Id $p.Id"
        ];
        protected override IEnumerable<AgentEvent> ParseStdout(string line, object state) => [];
    }

    private static List<AgentTaskStatus> Distinct(IEnumerable<AgentTaskStatus> statuses)
    {
        var result = new List<AgentTaskStatus>();
        foreach (var status in statuses)
            if (result.Count == 0 || result[^1] != status) result.Add(status);
        return result;
    }
}
