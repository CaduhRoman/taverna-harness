using System.Diagnostics;
using Taverna.Core.Git;
using Xunit;

namespace Taverna.Core.Tests;

public sealed class GitServiceTests
{
    [Fact]
    public async Task DetectRepository_ReturnsBranchAndCommit()
    {
        using var repo = await TemporaryRepository.CreateAsync();
        var info = await repo.Service.DetectRepositoryAsync(repo.Root);
        Assert.NotNull(info);
        Assert.Equal("main", info.Branch);
        Assert.InRange(info.ShortCommit.Length, 4, 40);
    }

    [Fact]
    public async Task CreateWorktree_IsIsolatedOutsideRepository()
    {
        using var repo = await TemporaryRepository.CreateAsync();
        var worktree = await repo.Service.CreateWorktreeAsync(repo.Root, "claude");
        Assert.True(Directory.Exists(worktree.Directory));
        Assert.False(worktree.Directory.StartsWith(repo.Root + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase));
        Assert.Equal("first\n", await File.ReadAllTextAsync(Path.Combine(worktree.Directory, "shared.txt")));
        var branch = await TemporaryRepository.GitAsync(worktree.Directory, "branch", "--show-current");
        Assert.Equal(worktree.Branch, branch.Trim());
    }

    [Fact]
    public async Task RemoveWorktree_RemovesPathAndBranch()
    {
        using var repo = await TemporaryRepository.CreateAsync();
        var worktree = await repo.Service.CreateWorktreeAsync(repo.Root, "codex");
        await repo.Service.RemoveWorktreeAsync(worktree);
        Assert.False(Directory.Exists(worktree.Directory));
        var branches = await TemporaryRepository.GitAsync(repo.Root, "branch", "--list", worktree.Branch);
        Assert.True(string.IsNullOrWhiteSpace(branches));
    }

    [Fact]
    public async Task ChangedFiles_IncludesUnstagedAndUntracked()
    {
        using var repo = await TemporaryRepository.CreateAsync();
        var worktree = await repo.Service.CreateWorktreeAsync(repo.Root, "claude");
        await File.WriteAllTextAsync(Path.Combine(worktree.Directory, "shared.txt"), "modified\n");
        await File.WriteAllTextAsync(Path.Combine(worktree.Directory, "new.txt"), "new\n");
        var changed = await repo.Service.GetChangedFilesAsync(worktree);
        Assert.Contains(changed, x => x.Path == "shared.txt" && x.Status.Contains('M'));
        Assert.Contains(changed, x => x.Path == "new.txt" && x.Status == "??");
        var comparison = await repo.Service.CompareWithBaseAsync(worktree);
        Assert.Contains("modified", comparison.Patch);
        Assert.Contains(comparison.Changes, x => x.Path == "new.txt");
    }

    [Fact]
    public async Task MergeClean_AutoCommitsAgentChanges()
    {
        using var repo = await TemporaryRepository.CreateAsync();
        var worktree = await repo.Service.CreateWorktreeAsync(repo.Root, "codex");
        await File.WriteAllTextAsync(Path.Combine(worktree.Directory, "agent.txt"), "from agent\n");
        var merged = await repo.Service.MergeAsync(worktree);
        Assert.True(merged.Merged);
        Assert.False(merged.HadConflicts);
        Assert.Equal("from agent\n", await File.ReadAllTextAsync(Path.Combine(repo.Root, "agent.txt")));
        Assert.True(string.IsNullOrWhiteSpace(await TemporaryRepository.GitAsync(repo.Root, "status", "--porcelain")));
        await repo.Service.RemoveWorktreeAsync(worktree);
    }

    [Fact]
    public async Task MergeConflict_AbortsAndRestoresMain()
    {
        using var repo = await TemporaryRepository.CreateAsync();
        var worktree = await repo.Service.CreateWorktreeAsync(repo.Root, "claude");
        await File.WriteAllTextAsync(Path.Combine(worktree.Directory, "shared.txt"), "agent edit\n");
        await File.WriteAllTextAsync(Path.Combine(repo.Root, "shared.txt"), "user committed edit\n");
        await TemporaryRepository.GitAsync(repo.Root, "add", "shared.txt");
        await TemporaryRepository.GitAsync(repo.Root, "commit", "-m", "user change");

        var result = await repo.Service.MergeAsync(worktree);
        Assert.False(result.Merged);
        Assert.True(result.HadConflicts);
        Assert.Contains("shared.txt", result.ConflictPaths);
        Assert.Equal("user committed edit\n", await File.ReadAllTextAsync(Path.Combine(repo.Root, "shared.txt")));
        Assert.False(File.Exists(Path.Combine(repo.Root, ".git", "MERGE_HEAD")));
        Assert.True(string.IsNullOrWhiteSpace(await TemporaryRepository.GitAsync(repo.Root, "status", "--porcelain")));
    }

    [Fact]
    public async Task DirtyMain_BlocksMergeAndPreservesUnsavedFile()
    {
        using var repo = await TemporaryRepository.CreateAsync();
        var worktree = await repo.Service.CreateWorktreeAsync(repo.Root, "claude");
        await File.WriteAllTextAsync(Path.Combine(worktree.Directory, "agent.txt"), "agent\n");
        await File.WriteAllTextAsync(Path.Combine(repo.Root, "shared.txt"), "unsaved work\n");
        var before = await TemporaryRepository.GitAsync(repo.Root, "rev-parse", "HEAD");

        await Assert.ThrowsAsync<InvalidOperationException>(() => repo.Service.MergeAsync(worktree));
        Assert.Equal("unsaved work\n", await File.ReadAllTextAsync(Path.Combine(repo.Root, "shared.txt")));
        Assert.Equal(before, await TemporaryRepository.GitAsync(repo.Root, "rev-parse", "HEAD"));
    }

    [Fact]
    public async Task UncommittedAgentFiles_PreventRemoval()
    {
        using var repo = await TemporaryRepository.CreateAsync();
        var worktree = await repo.Service.CreateWorktreeAsync(repo.Root, "claude");
        await File.WriteAllTextAsync(Path.Combine(worktree.Directory, "shared.txt"), "dirty\n");
        await Assert.ThrowsAsync<GitCommandException>(() => repo.Service.RemoveWorktreeAsync(worktree));
        Assert.True(Directory.Exists(worktree.Directory));
        Assert.Equal("dirty\n", await File.ReadAllTextAsync(Path.Combine(worktree.Directory, "shared.txt")));
    }

    private sealed class TemporaryRepository : IDisposable
    {
        public string Root { get; }
        public GitService Service { get; }
        private readonly string parent;

        private TemporaryRepository()
        {
            parent = Path.Combine(Path.GetTempPath(), "taverna-git-tests", Guid.NewGuid().ToString("N"));
            Root = Path.Combine(parent, "repository");
            Directory.CreateDirectory(Root);
            Service = new GitService(Path.Combine(parent, "worktrees"));
        }

        public static async Task<TemporaryRepository> CreateAsync()
        {
            var repo = new TemporaryRepository();
            try
            {
                await GitAsync(repo.Root, "init", "-b", "main");
                await GitAsync(repo.Root, "config", "user.name", "Taverna Test");
                await GitAsync(repo.Root, "config", "user.email", "test@taverna.local");
                await File.WriteAllTextAsync(Path.Combine(repo.Root, "shared.txt"), "first\n");
                await GitAsync(repo.Root, "add", ".");
                await GitAsync(repo.Root, "commit", "-m", "initial");
                return repo;
            }
            catch { repo.Dispose(); throw; }
        }

        public static async Task<string> GitAsync(string directory, params string[] args)
        {
            var info = new ProcessStartInfo("git")
            {
                WorkingDirectory = directory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var argument in args) info.ArgumentList.Add(argument);
            using var process = Process.Start(info) ?? throw new InvalidOperationException("Git indisponível.");
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var result = await output;
            if (process.ExitCode != 0) throw new Exception($"git {string.Join(' ', args)}: {await error}");
            return result.Trim();
        }

        public void Dispose()
        {
            try { Directory.Delete(parent, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
