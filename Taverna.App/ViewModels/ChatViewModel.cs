using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Taverna.Core.Agents;
using Taverna.Core.Chat;
using Taverna.Core.Context;
using Taverna.Core.Git;

namespace Taverna.App.ViewModels;

/// <summary>The project's single chat, shared by every agent, plus the composer and agent picker.</summary>
public partial class ChatViewModel : ObservableObject, IAsyncDisposable
{
    private readonly GitService git;
    private readonly IReadOnlyDictionary<AgentKind, IAgentRunner> runners;
    private readonly Action<string> reportError;
    /// <summary>Last model/effort chosen per agent, so switching back restores it.</summary>
    private readonly Dictionary<AgentKind, (string Model, string Effort)> choices = [];
    private ChatSession? session;
    private string? projectRoot;

    public ObservableCollection<ChatMessageViewModel> Messages { get; } = new();
    public IReadOnlyList<ChatAgentViewModel> Agents { get; }
    public ObservableCollection<ChoiceViewModel> Models { get; } = new();
    public ObservableCollection<ChoiceViewModel> Efforts { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PickerLabel), nameof(SupportsEffort), nameof(Watermark), nameof(CanSend))]
    private ChatAgentViewModel selectedAgent;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PickerLabel))]
    private string selectedModel = AgentOptionViewModel.DefaultChoice;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PickerLabel))]
    private string selectedEffort = AgentOptionViewModel.DefaultChoice;

    [ObservableProperty] private string customModel = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSend))]
    private string draft = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSend), nameof(IsIdle))]
    private bool isBusy;

    [ObservableProperty] private string? activityText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSend), nameof(IsEmpty))]
    private bool hasProject;

    public bool IsIdle => !IsBusy;
    public bool IsEmpty => Messages.Count == 0;
    public bool SupportsEffort => SelectedAgent.Option.SupportsEffort;
    public string Watermark => HasProject ? $"Message {SelectedAgent.Name}…" : "Open a folder to chat";
    public bool CanSend => HasProject && !IsBusy && SelectedAgent.IsAvailable && !string.IsNullOrWhiteSpace(Draft);

    /// <summary>"Claude · opus · high", omitting defaults.</summary>
    public string PickerLabel
    {
        get
        {
            var parts = new List<string> { SelectedAgent.Name };
            if (SelectedModel != AgentOptionViewModel.DefaultChoice) parts.Add(SelectedModel);
            if (SupportsEffort && SelectedEffort != AgentOptionViewModel.DefaultChoice) parts.Add(SelectedEffort);
            return string.Join(" · ", parts);
        }
    }

    public ChatViewModel(GitService git, IReadOnlyDictionary<AgentKind, IAgentRunner> runners, Action<string> reportError)
    {
        this.git = git;
        this.runners = runners;
        this.reportError = reportError;
        Agents = new[] { AgentKind.Claude, AgentKind.Codex, AgentKind.Copilot, AgentKind.ExternalApi }
            .Select(kind => new ChatAgentViewModel(new AgentOptionViewModel(runners[kind]))).ToList();
        selectedAgent = Agents.FirstOrDefault(a => a.IsAvailable) ?? Agents[0];
        selectedAgent.IsSelected = true;
        RebuildChoices();
        Messages.CollectionChanged += (_, _) => OnPropertyChanged(nameof(IsEmpty));
    }

    public async Task AttachAsync(string? root)
    {
        if (session is not null)
        {
            Unsubscribe(session);
            await session.DisposeAsync();
            session = null;
        }
        Messages.Clear();
        IsBusy = false;
        ActivityText = null;
        projectRoot = root;
        HasProject = root is not null;
        OnPropertyChanged(nameof(Watermark));
        if (root is null) return;

        var copilot = runners[AgentKind.Copilot];
        var options = new ContextOptions { MaxCharacters = ContextLimit() };
        var next = new ChatSession(root, runners, new ContextBuilder(options, new AgentSummarizer(copilot)), HeadAsync);
        try { await next.LoadAsync(); }
        catch (Exception ex) { reportError($"Could not load chat history: {ex.Message}"); }
        session = next;
        next.MessageAdded += OnMessageAdded;
        next.MessageDelta += OnMessageDelta;
        next.MessageCompleted += OnMessageCompleted;
        next.ActivityChanged += OnActivityChanged;
        foreach (var message in next.Messages) Messages.Add(new ChatMessageViewModel(message, root));
    }

    /// <summary>Re-checks which CLIs are installed (e.g. after installing one).</summary>
    [RelayCommand]
    private void RefreshAvailability()
    {
        foreach (var agent in Agents) agent.Refresh();
        OnPropertyChanged(nameof(CanSend));
    }

    [RelayCommand]
    private void SelectAgent(ChatAgentViewModel? agent)
    {
        if (agent is null || !agent.IsAvailable || agent == SelectedAgent) return;
        choices[SelectedAgent.Option.Kind] = (SelectedModel, SelectedEffort);
        SelectedAgent.IsSelected = false;
        SelectedAgent = agent;
        agent.IsSelected = true;
        (SelectedModel, SelectedEffort) = choices.TryGetValue(agent.Option.Kind, out var saved)
            ? saved : (AgentOptionViewModel.DefaultChoice, AgentOptionViewModel.DefaultChoice);
        RebuildChoices();
    }

    [RelayCommand]
    private void SelectModel(ChoiceViewModel? choice)
    {
        if (choice is null) return;
        SelectedModel = choice.Value;
        CustomModel = "";
        MarkSelected(Models, choice.Value);
    }

    [RelayCommand]
    private void ApplyCustomModel()
    {
        var model = CustomModel.Trim();
        if (model.Length == 0) return;
        if (!AgentCapabilities.IsSafeToken(model)) { reportError($"Invalid model name: {model}"); return; }
        SelectedModel = model;
        MarkSelected(Models, model);
    }

    [RelayCommand]
    private void SelectEffort(ChoiceViewModel? choice)
    {
        if (choice is null) return;
        SelectedEffort = choice.Value;
        MarkSelected(Efforts, choice.Value);
    }

    [RelayCommand]
    private async Task SendAsync()
    {
        if (session is null || !CanSend) return;
        var request = new ChatSendRequest(Draft, SelectedAgent.Option.Kind)
        {
            Model = AgentOptionViewModel.Normalize(SelectedModel),
            Effort = SupportsEffort ? AgentOptionViewModel.Normalize(SelectedEffort) : null
        };
        Task sending;
        try { sending = session.SendAsync(request); }
        catch (Exception ex) { reportError(ex.Message); return; }

        Draft = "";
        IsBusy = true;
        try { await sending; }
        catch (Exception ex) { reportError($"Send failed: {ex.Message}"); }
        finally { IsBusy = session?.IsBusy ?? false; }
    }

    [RelayCommand]
    private void Stop() => session?.Cancel();

    public async ValueTask DisposeAsync()
    {
        if (session is not null) await session.DisposeAsync();
    }

    // ---------- Session events (background threads) ----------

    private void OnMessageAdded(ChatMessage message) => Dispatcher.UIThread.Post(() =>
    {
        if (projectRoot is not null) Messages.Add(new ChatMessageViewModel(message, projectRoot));
    });

    private void OnMessageDelta(string id, string delta) => Dispatcher.UIThread.Post(() =>
    {
        if (Find(id) is { } message) message.Content += delta;
    });

    private void OnMessageCompleted(ChatMessage message) => Dispatcher.UIThread.Post(() =>
    {
        Find(message.Id)?.Apply(message);
        if (message.State == ChatMessageState.Failed) reportError($"{AgentNames.Display(Kind(message.Author))} failed: {message.Error}");
    });

    private void OnActivityChanged(string? activity) => Dispatcher.UIThread.Post(() =>
    {
        ActivityText = activity is null ? null : activity + "…";
        IsBusy = activity is not null || (session?.IsBusy ?? false);
    });

    // ---------- Helpers ----------

    /// <summary>TAVERNA_CONTEXT_MAX_CHARS overrides the 24,000 character context budget.</summary>
    private static int ContextLimit() =>
        int.TryParse(Environment.GetEnvironmentVariable("TAVERNA_CONTEXT_MAX_CHARS"), out var value) && value >= 1_000
            ? value : new ContextOptions().MaxCharacters;

    private async Task<RepositoryHead> HeadAsync(CancellationToken ct)
    {
        if (projectRoot is null) return new RepositoryHead(null, null);
        var repo = await git.DetectRepositoryAsync(projectRoot, ct);
        return new RepositoryHead(repo?.Branch, repo?.ShortCommit);
    }

    private ChatMessageViewModel? Find(string id)
    {
        // Streaming replies are almost always the last message.
        for (var i = Messages.Count - 1; i >= 0; i--)
            if (Messages[i].Id == id) return Messages[i];
        return null;
    }

    private void RebuildChoices()
    {
        Models.Clear();
        foreach (var model in SelectedAgent.Option.Models) Models.Add(new ChoiceViewModel(model));
        if (!SelectedAgent.Option.Models.Contains(SelectedModel)) Models.Add(new ChoiceViewModel(SelectedModel));
        MarkSelected(Models, SelectedModel);
        Efforts.Clear();
        foreach (var effort in SelectedAgent.Option.Efforts) Efforts.Add(new ChoiceViewModel(effort));
        MarkSelected(Efforts, SelectedEffort);
        OnPropertyChanged(nameof(PickerLabel));
    }

    private static void MarkSelected(IEnumerable<ChoiceViewModel> items, string value)
    {
        foreach (var item in items) item.IsSelected = item.Value == value;
    }

    private static AgentKind Kind(ChatAuthor author) => author switch
    {
        ChatAuthor.Claude => AgentKind.Claude,
        ChatAuthor.Codex => AgentKind.Codex,
        ChatAuthor.Copilot => AgentKind.Copilot,
        _ => AgentKind.ExternalApi
    };

    private void Unsubscribe(ChatSession old)
    {
        old.MessageAdded -= OnMessageAdded;
        old.MessageDelta -= OnMessageDelta;
        old.MessageCompleted -= OnMessageCompleted;
        old.ActivityChanged -= OnActivityChanged;
    }
}
