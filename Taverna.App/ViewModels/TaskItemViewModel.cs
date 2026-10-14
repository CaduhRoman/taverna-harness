using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Taverna.Core.Agents;
using Taverna.Core.Git;
using Taverna.Core.Orchestration;

namespace Taverna.App.ViewModels;

/// <summary>UI projection of one orchestrated task; refreshed from orchestrator snapshots.</summary>
public partial class TaskItemViewModel : ObservableObject
{
    public string Id { get; }
    public ObservableCollection<GitFileChange> ChangedFiles { get; } = new();

    [ObservableProperty] private AgentTask model;
    [ObservableProperty] private string log = "";
    [ObservableProperty] private bool isExpanded;
    [ObservableProperty] private bool isBusy;

    public TaskItemViewModel(AgentTask task)
    {
        Id = task.Id;
        model = task;
        SyncFiles();
    }

    public string Title => Model.Title;
    public string Prompt => Model.Prompt;
    public string AgentName => AgentNames.Display(Model.Agent);
    public string Branch => Model.Branch;
    public string BaseCommit => Model.BaseCommit.Length > 10 ? Model.BaseCommit[..10] : Model.BaseCommit;
    public string WorktreePath => Model.WorktreePath;
    public bool IsWorking => Model.IsActive;
    public bool HasConflict => Model.MergeState == TaskMergeState.Conflict;
    public IReadOnlyList<string> ConflictFiles => Model.ConflictFiles;

    /// <summary>Idle, Working, Done or Error; merge outcomes take over once a merge was attempted.</summary>
    public string StatusText => Model.MergeState switch
    {
        TaskMergeState.Merged => "Merged",
        TaskMergeState.Conflict => "Conflict",
        _ => Model.Status.ToString()
    };

    /// <summary>Status dot: pulsing while working, hollow on error or conflict, solid when done.</summary>
    public bool IsErrorState => Model.Status == AgentTaskStatus.Error || Model.MergeState == TaskMergeState.Conflict;
    public bool IsDoneState => !IsWorking && !IsErrorState;
    public bool HasDetail => !string.IsNullOrEmpty(Detail);
    public bool HasChangedFiles => ChangedFiles.Count > 0;
    public string Meta => string.IsNullOrEmpty(Branch) ? AgentName : $"{AgentName} · {Branch}";

    public string? Detail => Model.MergeState is TaskMergeState.Conflict or TaskMergeState.Failed ? Model.MergeMessage
        : Model.Status == AgentTaskStatus.Error ? Model.Reason : null;

    public string DurationText
    {
        get
        {
            var span = TimeSpan.FromMilliseconds(Model.DurationMs);
            return span.TotalHours >= 1 ? span.ToString(@"h\:mm\:ss") : span.ToString(@"m\:ss");
        }
    }

    public string TokensText => Model.Tokens is not { } t ? "—"
        : $"{Compact(t.Input)} in · {Compact(t.Output)} out" + (t.CachedInput is > 0 ? $" · {Compact(t.CachedInput.Value)} cached" : "");
    public string FilesText => ChangedFiles.Count == 1 ? "1 file" : $"{ChangedFiles.Count} files";

    public bool CanCancel => IsWorking && !IsBusy;
    public bool CanMerge => !IsWorking && !IsBusy && Model.MergeState != TaskMergeState.Merged && Directory.Exists(Model.WorktreePath);
    public bool CanDiscard => !IsWorking && !IsBusy;
    public bool CanOpen => Directory.Exists(Model.WorktreePath);

    partial void OnModelChanged(AgentTask value)
    {
        SyncFiles();
        OnPropertyChanged(string.Empty);
    }

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanCancel));
        OnPropertyChanged(nameof(CanMerge));
        OnPropertyChanged(nameof(CanDiscard));
    }

    /// <summary>Bytes of log tail shown live; the full log stays on disk.</summary>
    public const int LogTailBytes = 200_000;

    private void SyncFiles()
    {
        if (ChangedFiles.Select(f => (f.Path, f.Status)).SequenceEqual(Model.ChangedFiles.Select(f => (f.Path, f.Status)))) return;
        ChangedFiles.Clear();
        foreach (var file in Model.ChangedFiles) ChangedFiles.Add(file);
    }

    private static string Compact(long value) => value >= 1000 ? $"{value / 1000.0:0.#}k" : value.ToString();
}
