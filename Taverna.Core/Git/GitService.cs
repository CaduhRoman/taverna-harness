using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Taverna.Core.Git;

public sealed record GitRepository(string RootPath, string Branch, string ShortCommit);
public sealed record ManagedWorktree(string RepositoryRoot, string Directory, string Branch, string BaseCommit, string TargetBranch);
public sealed record GitFileChange(string Path, string Status);
public sealed record GitComparison(string Patch, IReadOnlyList<GitFileChange> Changes);
public sealed record GitMergeResult(bool Merged, bool HadConflicts, IReadOnlyList<string> ConflictPaths, string? Message = null);

public sealed class GitService
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> RepositoryLocks = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    private readonly string worktreeStorageRoot;

    public GitService(string? worktreeStorageRoot = null)
    {
        this.worktreeStorageRoot = Path.GetFullPath(worktreeStorageRoot ??
            Path.Combine(Path.GetTempPath(), "taverna-managed-worktrees"));
    }

    public async Task<GitRepository?> DetectRepositoryAsync(string directory, CancellationToken ct = default)
    {
        if (!Directory.Exists(directory)) return null;
        var root = await GitProcess.RunAsync(directory, ct, "rev-parse", "--show-toplevel");
        if (root.ExitCode != 0) return null;
        var branch = await RequiredAsync(directory, ct, "branch", "--show-current");
        var commit = await RequiredAsync(directory, ct, "rev-parse", "--short", "HEAD");
        return new GitRepository(Path.GetFullPath(root.Output.Trim()),
            string.IsNullOrWhiteSpace(branch) ? "(detached)" : branch, commit);
    }

    public Task<ManagedWorktree> CreateWorktreeAsync(string directory, string agentName, CancellationToken ct = default)
    {
        var label = Slugify(agentName, "agent", 32);
        var suffix = Guid.NewGuid().ToString("N")[..12];
        return CreateWorktreeCoreAsync(directory, _ => Task.FromResult($"taverna/{label}-{suffix}"), $"{label}-{suffix}", ct);
    }

    /// <summary>
    /// Creates a worktree on branch agent/&lt;agent&gt;/&lt;slug&gt; from the current branch, appending -2, -3...
    /// when that branch already exists.
    /// </summary>
    public Task<ManagedWorktree> CreateTaskWorktreeAsync(string directory, string agentSlug, string title, CancellationToken ct = default)
    {
        var agent = Slugify(agentSlug, "agent", 16);
        var slug = Slugify(title, "task", 40);
        return CreateWorktreeCoreAsync(directory, async root =>
        {
            for (var attempt = 1; ; attempt++)
            {
                var name = attempt == 1 ? $"agent/{agent}/{slug}" : $"agent/{agent}/{slug}-{attempt}";
                var exists = await GitProcess.RunAsync(root, ct, "rev-parse", "--verify", "--quiet", $"refs/heads/{name}");
                if (exists.ExitCode != 0) return name;
            }
        }, $"{agent}-{slug}-{Guid.NewGuid().ToString("N")[..6]}", ct);
    }

    public static string Slugify(string text, string fallback, int maxLength)
    {
        var slug = new string(text.ToLowerInvariant().Select(c =>
            (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') ? c : '-').ToArray());
        while (slug.Contains("--")) slug = slug.Replace("--", "-");
        slug = slug.Trim('-');
        if (slug.Length > maxLength) slug = slug[..maxLength].TrimEnd('-');
        return slug.Length == 0 ? fallback : slug;
    }

    private async Task<ManagedWorktree> CreateWorktreeCoreAsync(string directory, Func<string, Task<string>> branchName,
        string folderName, CancellationToken ct)
    {
        var repository = await DetectRepositoryAsync(directory, ct)
            ?? throw new GitCommandException("A pasta não pertence a um repositório Git com commit inicial.");
        return await SerializedAsync(repository.RootPath, async () =>
        {
            if (repository.Branch == "(detached)")
                throw new InvalidOperationException("Faça checkout de uma branch antes de criar worktrees.");

            // Acquire fresh HEAD inside the lock. Never check out or stash the user's worktree.
            var branch = (await RequiredAsync(repository.RootPath, ct, "branch", "--show-current")).Trim();
            var baseCommit = await RequiredAsync(repository.RootPath, ct, "rev-parse", "HEAD");
            if (branch.Length == 0) throw new InvalidOperationException("HEAD destacado.");

            var name = await branchName(repository.RootPath);
            var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(repository.RootPath)))[..16];
            var path = Path.Combine(worktreeStorageRoot, digest, folderName);
            if (Path.GetFullPath(path).StartsWith(repository.RootPath + Path.DirectorySeparatorChar,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidOperationException("O caminho gerenciado precisa ficar fora do projeto Git.");

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            (await GitProcess.RunAsync(repository.RootPath, ct, "worktree", "add", "-b", name, path, baseCommit))
                .ThrowIfFailed("worktree add");
            return new ManagedWorktree(repository.RootPath, path, name, baseCommit, branch);
        }, ct);
    }

    public async Task<IReadOnlyList<GitFileChange>> GetChangedFilesAsync(ManagedWorktree worktree,
        CancellationToken ct = default)
    {
        ValidateWorktree(worktree);
        // List untracked files individually instead of collapsing new folders.
        var result = await GitProcess.RunAsync(worktree.Directory, ct, "status", "--porcelain=v1", "-z", "--untracked-files=all");
        result.ThrowIfFailed("status");
        return ParseStatus(result.Output);
    }

    public async Task<GitComparison> CompareWithBaseAsync(ManagedWorktree worktree, CancellationToken ct = default)
    {
        ValidateWorktree(worktree);
        var patch = await GitProcess.RunAsync(worktree.Directory, ct, "diff", "--binary", worktree.BaseCommit, "--");
        patch.ThrowIfFailed("diff base");
        var names = await GitProcess.RunAsync(worktree.Directory, ct, "diff", "--name-status", worktree.BaseCommit, "--");
        names.ThrowIfFailed("diff name-status");
        var committedAndTracked = names.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd('\r').Split('\t'))
            .Where(parts => parts.Length >= 2)
            .Select(parts => new GitFileChange(parts[^1], parts[0])).ToList();
        // Git diff omits untracked files, which are exposed separately in Changes.
        var status = await GetChangedFilesAsync(worktree, ct);
        foreach (var change in status.Where(c => c.Status == "??" &&
            !committedAndTracked.Any(tracked => tracked.Path == c.Path)))
            committedAndTracked.Add(change);
        return new GitComparison(patch.Output, committedAndTracked);
    }

    public async Task<GitMergeResult> MergeAsync(ManagedWorktree worktree, CancellationToken ct = default)
    {
        ValidateWorktree(worktree);
        return await SerializedAsync(worktree.RepositoryRoot, async () =>
        {
            var branch = await RequiredAsync(worktree.RepositoryRoot, ct, "branch", "--show-current");
            if (branch != worktree.TargetBranch)
                throw new InvalidOperationException("Branch principal mudou; merge interrompido para proteger o usuário.");

            // Tracked edits block the merge outright. Untracked files are left to Git, which refuses to
            // overwrite them before touching anything (handled below as a non-conflict failure).
            var status = await RequiredAsync(worktree.RepositoryRoot, ct, "status", "--porcelain=v1", "--untracked-files=no");
            if (!string.IsNullOrWhiteSpace(status))
                throw new InvalidOperationException("Worktree principal possui alterações locais; merge interrompido sem modificar arquivos.");

            var actualBranch = await RequiredAsync(worktree.Directory, ct, "branch", "--show-current");
            if (actualBranch != worktree.Branch)
                throw new InvalidOperationException("A branch da worktree mudou.");

            // Commit *only* agent worktree changes before merge; never stash/reset user state.
            (await GitProcess.RunAsync(worktree.Directory, ct, "add", "-A")).ThrowIfFailed("add agent changes");
            var staged = await GitProcess.RunAsync(worktree.Directory, ct, "diff", "--cached", "--quiet");
            if (staged.ExitCode == 1)
                (await GitProcess.RunAsync(worktree.Directory, ct, "commit", "-m", $"taverna: changes from {worktree.Branch}"))
                    .ThrowIfFailed("commit agent changes");
            else if (staged.ExitCode != 0) staged.ThrowIfFailed("diff staged");

            var merge = await GitProcess.RunAsync(worktree.RepositoryRoot, ct,
                "merge", "--no-ff", "--no-edit", worktree.Branch);
            if (merge.ExitCode == 0) return new GitMergeResult(true, false, []);

            // Git can refuse before starting (e.g. an untracked file would be overwritten): nothing to abort.
            var inProgress = await GitProcess.RunAsync(worktree.RepositoryRoot, CancellationToken.None,
                "rev-parse", "-q", "--verify", "MERGE_HEAD");
            if (inProgress.ExitCode != 0)
                return new GitMergeResult(false, false, [], $"Git recusou o merge: {(merge.Error + " " + merge.Output).Trim()}");

            var conflicts = await GitProcess.RunAsync(worktree.RepositoryRoot, CancellationToken.None,
                "diff", "--name-only", "--diff-filter=U");
            var paths = conflicts.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.TrimEnd('\r')).ToArray();
            // A failed merge must be aborted even if the failure wasn't a content conflict.
            var abort = await GitProcess.RunAsync(worktree.RepositoryRoot, CancellationToken.None, "merge", "--abort");
            if (abort.ExitCode != 0)
                throw new GitCommandException($"Merge falhou e git merge --abort também falhou: {abort.Error}");
            if (paths.Length > 0) return new GitMergeResult(false, true, paths);
            throw new GitCommandException($"Git merge falhou: {merge.Error} {merge.Output}");
        }, ct);
    }

    public async Task RemoveWorktreeAsync(ManagedWorktree worktree, CancellationToken ct = default)
    {
        ValidateWorktree(worktree);
        await SerializedAsync(worktree.RepositoryRoot, async () =>
        {
            // git worktree remove without --force rejects uncommitted changes.
            (await GitProcess.RunAsync(worktree.RepositoryRoot, ct, "worktree", "remove", worktree.Directory))
                .ThrowIfFailed("worktree remove");
            // -d protects commits that have not been merged into the current branch.
            (await GitProcess.RunAsync(worktree.RepositoryRoot, ct, "branch", "-d", worktree.Branch))
                .ThrowIfFailed("branch delete (branch preservada para recuperação se ainda não foi incorporada)");
        }, ct);
    }

    /// <summary>
    /// True when discarding would lose work: uncommitted edits in the worktree, or commits on its
    /// branch that the repository's current HEAD does not contain.
    /// </summary>
    public async Task<bool> HasUnmergedWorkAsync(ManagedWorktree worktree, CancellationToken ct = default)
    {
        if (Directory.Exists(worktree.Directory) && (await GetChangedFilesAsync(worktree, ct)).Count > 0) return true;
        var branchExists = await GitProcess.RunAsync(worktree.RepositoryRoot, ct, "rev-parse", "--verify", "--quiet", $"refs/heads/{worktree.Branch}");
        if (branchExists.ExitCode != 0) return false;
        var ahead = await RequiredAsync(worktree.RepositoryRoot, ct, "rev-list", "--count", $"HEAD..refs/heads/{worktree.Branch}");
        return int.TryParse(ahead, out var count) && count > 0;
    }

    /// <summary>
    /// Removes the worktree and its branch after the user explicitly confirmed losing unmerged work.
    /// Never call this without that confirmation; <see cref="RemoveWorktreeAsync"/> is the safe default.
    /// </summary>
    public async Task DiscardWorktreeAsync(ManagedWorktree worktree, CancellationToken ct = default)
    {
        if (!Directory.Exists(worktree.RepositoryRoot)) throw new DirectoryNotFoundException(worktree.RepositoryRoot);
        await SerializedAsync(worktree.RepositoryRoot, async () =>
        {
            if (Directory.Exists(worktree.Directory))
                (await GitProcess.RunAsync(worktree.RepositoryRoot, ct, "worktree", "remove", "--force", worktree.Directory))
                    .ThrowIfFailed("worktree remove");
            else
                // The folder vanished outside Taverna; drop Git's stale registration.
                (await GitProcess.RunAsync(worktree.RepositoryRoot, ct, "worktree", "prune")).ThrowIfFailed("worktree prune");

            var branchExists = await GitProcess.RunAsync(worktree.RepositoryRoot, ct, "rev-parse", "--verify", "--quiet", $"refs/heads/{worktree.Branch}");
            if (branchExists.ExitCode == 0)
                (await GitProcess.RunAsync(worktree.RepositoryRoot, ct, "branch", "-D", worktree.Branch)).ThrowIfFailed("branch delete");
        }, ct);
    }

    private static IReadOnlyList<GitFileChange> ParseStatus(string output)
    {
        var entries = output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        var changes = new List<GitFileChange>();
        for (var index = 0; index < entries.Length; index++)
        {
            var entry = entries[index];
            if (entry.Length < 4) continue;
            var code = entry[..2];
            changes.Add(new GitFileChange(entry[3..], code));
            if (code.Contains('R') || code.Contains('C')) index++; // Old path is the next NUL field.
        }
        return changes;
    }

    private static void ValidateWorktree(ManagedWorktree worktree)
    {
        if (!Directory.Exists(worktree.Directory)) throw new DirectoryNotFoundException(worktree.Directory);
        if (!Directory.Exists(worktree.RepositoryRoot)) throw new DirectoryNotFoundException(worktree.RepositoryRoot);
    }

    private static async Task<string> RequiredAsync(string directory, CancellationToken ct, params string[] args)
    {
        var result = await GitProcess.RunAsync(directory, ct, args);
        result.ThrowIfFailed(string.Join(' ', args));
        return result.Output.Trim();
    }

    private static async Task<T> SerializedAsync<T>(string root, Func<Task<T>> action, CancellationToken ct)
    {
        var key = Path.GetFullPath(root);
        var gate = RepositoryLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try { return await action(); }
        finally { gate.Release(); }
    }

    private static Task SerializedAsync(string root, Func<Task> action, CancellationToken ct) =>
        SerializedAsync(root, async () => { await action(); return true; }, ct);
}
