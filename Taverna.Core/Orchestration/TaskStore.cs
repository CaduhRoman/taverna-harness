using System.Text.Json;
using Taverna.Core.IO;

namespace Taverna.Core.Orchestration;

/// <summary>Reads and atomically writes .taverna/tasks.json.</summary>
public sealed class TaskStore(string projectRoot)
{
    private sealed class TaskFile
    {
        public int Version { get; set; } = 1;
        public List<AgentTask> Tasks { get; set; } = [];
    }

    private readonly SemaphoreSlim gate = new(1, 1);

    public string FilePath { get; } = Path.Combine(projectRoot, ".taverna", "tasks.json");
    public string ProjectRoot { get; } = projectRoot;

    /// <summary>
    /// Loads tasks. Anything still marked Idle/Working was cut off by a previous exit: it becomes
    /// Error/Interrupted and is never restarted. The corrected state is written back.
    /// </summary>
    public async Task<List<AgentTask>> LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(FilePath)) return [];
        List<AgentTask> tasks;
        try
        {
            var json = await File.ReadAllTextAsync(FilePath, ct);
            tasks = JsonSerializer.Deserialize<TaskFile>(json, TavernaJson.Options)?.Tasks ?? [];
        }
        catch (JsonException)
        {
            TavernaJson.QuarantineCorrupt(FilePath);
            return [];
        }
        if (RecoverInterrupted(tasks, DateTimeOffset.Now) > 0) await SaveAsync(tasks, ct);
        return tasks;
    }

    public static int RecoverInterrupted(IEnumerable<AgentTask> tasks, DateTimeOffset now)
    {
        var count = 0;
        foreach (var task in tasks.Where(t => t.IsActive))
        {
            task.Status = AgentTaskStatus.Error;
            task.Reason = AgentTask.InterruptedReason;
            task.FinishedAt ??= now;
            count++;
        }
        return count;
    }

    public async Task SaveAsync(IEnumerable<AgentTask> tasks, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(new TaskFile { Tasks = [.. tasks] }, TavernaJson.Options);
        await gate.WaitAsync(ct);
        try { await AtomicFile.WriteAllTextAsync(FilePath, json, ct); }
        finally { gate.Release(); }
    }
}
