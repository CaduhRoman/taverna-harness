using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Taverna.Core.Agents;
using Taverna.Core.Git;
using Taverna.Core.Chat;
using Taverna.Core.Orchestration;

namespace Taverna.App.ViewModels;

/// <summary>The orchestrator panel: New Task form, task list and per-task actions.</summary>
public partial class TasksViewModel : ObservableObject, IAsyncDisposable
{
    private readonly GitService git;
    private readonly IReadOnlyDictionary<AgentKind, IAgentRunner> runners;
    private readonly Action<string> reportError;
    private readonly Func<string, string, string, Task<bool>> confirm;
    private readonly Action<TaskItemViewModel> openWorktree;
    /// <summary>Closes editor tabs inside a worktree before it is deleted; false when the user cancels.</summary>
    private readonly Func<string, Task<bool>> releaseWorktree;
    private TaskOrchestrator? orchestrator;
    private string? projectRoot;
    private readonly Func<Task<string?>> pickImage;
    public ObservableCollection<AttachmentViewModel> ContextAttachments { get; } = new();
    [ObservableProperty] private bool startAfterCreate = true;
    public bool CanAddContext => NewAgent?.SupportsImages == true;
    public ObservableCollection<ChoiceViewModel> ModelChoices { get; } = new();
    public ChoiceViewModel? SelectedModelChoice
    {
        get => ModelChoices.FirstOrDefault(m => m.Value == NewModel);
        set { if (value is not null) NewModel = value.Value; }
    }
    /// <summary>Effort chips for the chosen model; "Default" leaves the agent's own setting.</summary>
    public ObservableCollection<ChoiceViewModel> EffortChoices { get; } = new();
    [RelayCommand] private void SelectEffortChoice(ChoiceViewModel? choice) { if (choice is not null) NewEffort = choice.Value; }

    private void NotifyExecutionChoices()
    {
        var efforts = NewEfforts;
        if (!EffortChoices.Select(c => c.Value).SequenceEqual(efforts))
        {
            EffortChoices.Clear();
            foreach (var effort in efforts) EffortChoices.Add(new ChoiceViewModel(effort));
        }
        foreach (var choice in EffortChoices) choice.IsSelected = choice.Value == (NewEffort ?? AgentOptionViewModel.DefaultChoice);
        OnPropertyChanged(nameof(NewAgentSupportsEffort));
        OnPropertyChanged(nameof(SelectedModelChoice));
    }
    partial void OnNewModelChanged(string? value)
    {
        NewEffort = NewAgent?.ValidEffort(value, NewEffort) ?? AgentOptionViewModel.DefaultChoice;
        NotifyExecutionChoices();
    }
    partial void OnNewEffortChanged(string? value) => NotifyExecutionChoices();

    [RelayCommand]
    private async Task AddContextAsync()
    {
        if (!CanAddContext || projectRoot is null) return;
        var root = projectRoot;
        var path = await pickImage();
        if (path is null || root != projectRoot) return;
        try
        {
            var relative = await AttachmentStore.ImportAsync(root, path);
            if (root == projectRoot) ContextAttachments.Add(new AttachmentViewModel(root, relative));
        }
        catch (Exception ex) { reportError(ex.Message); }
    }
    [RelayCommand] private void RemoveContext(AttachmentViewModel? attachment)
    { if (attachment is not null) ContextAttachments.Remove(attachment); }

    public ObservableCollection<TaskItemViewModel> Tasks { get; } = new();
    public IReadOnlyList<AgentOptionViewModel> AgentOptions { get; }

    [ObservableProperty] private bool hasProject;
    [ObservableProperty] private bool isCreating;
    [ObservableProperty] private string newTitle = "";
    [ObservableProperty] private string newPrompt = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NewEfforts))]
    private string? newModel = AgentOptionViewModel.DefaultChoice;
    [ObservableProperty] private string? newEffort = AgentOptionViewModel.DefaultChoice;
    [ObservableProperty] private bool isStarting;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NewModels), nameof(NewEfforts), nameof(NewAgentSupportsEffort), nameof(NewAgentHint))]
    private AgentOptionViewModel? newAgent;

    public IReadOnlyList<string> NewModels => NewAgent?.Models ?? [];
    public IReadOnlyList<string> NewEfforts => NewAgent?.EffortsFor(NewModel) ?? [];
    public bool NewAgentSupportsEffort => NewEfforts.Count > 1;
    public string? NewAgentHint => NewAgent is null ? null : orchestrator?.CannotStartReason(NewAgent.Kind);

    public int ActiveCount => Tasks.Count(t => t.IsWorking);
    public bool HasActiveTasks => ActiveCount > 0;
    public bool HasNoTasks => Tasks.Count == 0;
    public string HeaderBadge => ActiveCount > 0 ? ActiveCount.ToString() : "";

    public TasksViewModel(GitService git, IReadOnlyDictionary<AgentKind, IAgentRunner> runners, Action<string> reportError,
        Func<string, string, string, Task<bool>> confirm, Action<TaskItemViewModel> openWorktree,
        Func<string, Task<bool>> releaseWorktree, Func<Task<string?>>? pickImage = null)
    {
        this.releaseWorktree = releaseWorktree;
        this.pickImage = pickImage ?? (() => Task.FromResult<string?>(null));
        this.git = git;
        this.runners = runners;
        this.reportError = reportError;
        this.confirm = confirm;
        this.openWorktree = openWorktree;
        AgentOptions = AgentRunnerCatalog.TaskAgents.Select(kind => new AgentOptionViewModel(runners[kind])).ToList();
        newAgent = AgentOptions.FirstOrDefault(a => a.IsAvailable) ?? AgentOptions[0];
        foreach (var model in newAgent.ModelChoices) ModelChoices.Add(model);
        newModel = newAgent.Runner.Capabilities.Models.FirstOrDefault() ?? AgentOptionViewModel.DefaultChoice;
        NotifyExecutionChoices();
        logTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(300), DispatcherPriority.Background, (_, _) => RefreshDirtyLogs());
        logTimer.Start();
    }

    private readonly HashSet<string> dirtyLogs = [];
    private readonly DispatcherTimer logTimer;

    partial void OnNewAgentChanged(AgentOptionViewModel? value)
    {
        ModelChoices.Clear();
        if (value is not null) foreach (var model in value.ModelChoices) ModelChoices.Add(model);
        NewModel = value?.Runner.Capabilities.Models.FirstOrDefault() ?? AgentOptionViewModel.DefaultChoice;
        NewEffort = AgentOptionViewModel.DefaultChoice;
        OnPropertyChanged(nameof(ModelChoices));
        OnPropertyChanged(nameof(CanAddContext));
        NotifyExecutionChoices();
    }

    /// <summary>Switches to another project's tasks. Running tasks of the previous project are stopped.</summary>
    public async Task AttachAsync(string? projectRoot)
    {
        this.projectRoot = projectRoot;
        ContextAttachments.Clear();
        NewTitle = "";
        NewPrompt = "";
        if (orchestrator is not null)
        {
            orchestrator.TaskChanged -= OnTaskChanged;
            orchestrator.TaskLog -= OnTaskLog;
            orchestrator.TaskRemoved -= OnTaskRemoved;
            await orchestrator.DisposeAsync();
            orchestrator = null;
        }
        Tasks.Clear();
        IsCreating = false;
        HasProject = projectRoot is not null;
        NotifyCounts();
        if (projectRoot is null) return;

        var next = new TaskOrchestrator(projectRoot, git, runners);
        try { await next.LoadAsync(); }
        catch (Exception ex) { reportError($"Could not load tasks: {ex.Message}"); }
        orchestrator = next;
        next.TaskChanged += OnTaskChanged;
        next.TaskLog += OnTaskLog;
        next.TaskRemoved += OnTaskRemoved;
        // Newest first.
        foreach (var task in next.Tasks.OrderByDescending(t => t.CreatedAt)) Tasks.Add(new TaskItemViewModel(task));
        NotifyCounts();
    }

    [RelayCommand]
    private void BeginCreate()
    {
        if (!HasProject) { reportError("Open a folder first."); return; }
        foreach (var option in AgentOptions) option.RefreshAvailability();
        if (NewAgent is null || !NewAgent.IsAvailable) NewAgent = AgentOptions.FirstOrDefault(a => a.IsAvailable) ?? NewAgent;
        OnPropertyChanged(nameof(NewAgentHint));
        IsCreating = true;
    }

    [RelayCommand]
    private void CancelCreate() => IsCreating = false;

    [RelayCommand]
    private async Task CreateAsync()
    {
        if (orchestrator is null || NewAgent is null) return;
        if (string.IsNullOrWhiteSpace(NewTitle) || string.IsNullOrWhiteSpace(NewPrompt))
        {
            reportError("Title and prompt are required.");
            return;
        }
        if (StartAfterCreate && orchestrator.CannotStartReason(NewAgent.Kind) is { } reason)
        {
            reportError(reason);
            return;
        }

        IsStarting = true;
        try
        {
            var request = new TaskRequest(NewTitle, NewAgent.Kind, NewPrompt)
            {
                Model = AgentOptionViewModel.Normalize(NewModel),
                Effort = AgentOptionViewModel.Normalize(NewAgent.ValidEffort(NewModel, NewEffort)),
                Attachments = ContextAttachments.Select(a => a.RelativePath).ToList()
            };
            var task = StartAfterCreate ? await orchestrator.StartAsync(request) : await orchestrator.EnqueueAsync(request);
            var item = Upsert(task);
            item.IsExpanded = true;
            LoadLog(item);
            NewTitle = "";
            NewPrompt = "";
            ContextAttachments.Clear();
            IsCreating = false;
        }
        catch (Exception ex) { reportError(ex.Message); }
        finally { IsStarting = false; }
    }

    [RelayCommand]
    private async Task StartQueuedAsync(TaskItemViewModel? item)
    {
        if (item is null || orchestrator is null) return;
        item.IsBusy = true;
        try { Upsert(await orchestrator.StartQueuedAsync(item.Id)); }
        catch (Exception ex) { reportError(ex.Message); }
        finally { item.IsBusy = false; }
    }

    [RelayCommand]
    private void CancelTask(TaskItemViewModel? item)
    {
        if (item is not null) orchestrator?.Cancel(item.Id);
    }

    [RelayCommand]
    private async Task MergeAsync(TaskItemViewModel? item)
    {
        if (item is null || orchestrator is null) return;
        item.IsBusy = true;
        try
        {
            var result = await orchestrator.MergeAsync(item.Id);
            if (result.HadConflicts) reportError($"Conflict in {result.ConflictPaths.Count} file(s); merge aborted, worktree kept.");
            else if (!result.Merged) reportError(result.Message ?? "Merge failed.");
        }
        catch (Exception ex) { reportError($"Merge failed: {ex.Message}"); }
        finally { item.IsBusy = false; }
    }

    [RelayCommand]
    private async Task DiscardAsync(TaskItemViewModel? item)
    {
        if (item is null || orchestrator is null) return;
        item.IsBusy = true;
        try
        {
            var needsConfirmation = await orchestrator.DiscardNeedsConfirmationAsync(item.Id);
            if (needsConfirmation && !await confirm("Discard task?",
                    $"{item.Branch} has changes that are not in the project. They will be deleted.", "Discard"))
                return;
            if (!string.IsNullOrEmpty(item.WorktreePath) && !await releaseWorktree(item.WorktreePath)) return;
            await orchestrator.DiscardAsync(item.Id, confirmed: needsConfirmation);
        }
        catch (Exception ex) { reportError($"Discard failed: {ex.Message}"); }
        finally { item.IsBusy = false; }
    }

    [RelayCommand]
    private void Open(TaskItemViewModel? item)
    {
        if (item is { CanOpen: true }) openWorktree(item);
        else if (item is not null) reportError("The worktree folder no longer exists.");
    }

    [RelayCommand]
    private void ToggleExpand(TaskItemViewModel? item)
    {
        if (item is null) return;
        item.IsExpanded = !item.IsExpanded;
        if (item.IsExpanded) LoadLog(item);
    }

    public async ValueTask DisposeAsync()
    {
        logTimer.Stop();
        if (orchestrator is not null) await orchestrator.DisposeAsync();
    }

    // ---------- Orchestrator events (background threads) ----------

    private void OnTaskChanged(AgentTask task) => Dispatcher.UIThread.Post(() =>
    {
        Upsert(task);
        NotifyCounts();
    });

    private void OnTaskLog(string id, string text)
    {
        lock (dirtyLogs) dirtyLogs.Add(id);
    }

    /// <summary>Re-reads the log tail of expanded tasks that received output; the file is the single source.</summary>
    private void RefreshDirtyLogs()
    {
        string[] ids;
        lock (dirtyLogs)
        {
            ids = [.. dirtyLogs];
            dirtyLogs.Clear();
        }
        foreach (var id in ids)
            if (Tasks.FirstOrDefault(t => t.Id == id) is { IsExpanded: true } item) LoadLog(item);
    }

    private void LoadLog(TaskItemViewModel item)
    {
        if (orchestrator is null) return;
        try { item.Log = orchestrator.ReadLog(item.Id, TaskItemViewModel.LogTailBytes); }
        catch (KeyNotFoundException) { }
    }

    private void OnTaskRemoved(string id) => Dispatcher.UIThread.Post(() =>
    {
        if (Tasks.FirstOrDefault(t => t.Id == id) is { } item) Tasks.Remove(item);
        NotifyCounts();
    });

    private TaskItemViewModel Upsert(AgentTask task)
    {
        if (Tasks.FirstOrDefault(t => t.Id == task.Id) is { } existing)
        {
            existing.Model = task;
            return existing;
        }
        var item = new TaskItemViewModel(task);
        Tasks.Insert(0, item);
        return item;
    }

    private void NotifyCounts()
    {
        OnPropertyChanged(nameof(ActiveCount));
        OnPropertyChanged(nameof(HasActiveTasks));
        OnPropertyChanged(nameof(HasNoTasks));
        OnPropertyChanged(nameof(HeaderBadge));
        OnPropertyChanged(nameof(NewAgentHint));
    }
}
