using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Taverna.Core.Chat;

namespace Taverna.App.ViewModels;

public partial class ChatMessageViewModel : ObservableObject
{
    public string Id { get; }
    public ChatAuthor Author { get; }
    public bool IsUser => Author == ChatAuthor.User;
    public string AuthorName => Author switch
    {
        ChatAuthor.User => "You",
        ChatAuthor.ExternalApi => "API",
        _ => Author.ToString()
    };
    public string TimeText { get; }
    public string? RefText { get; }
    public string? ModelText { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasContent))]
    private string content;

    public bool HasContent => !string.IsNullOrEmpty(Content);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStreaming), nameof(StateText), nameof(HasStateText))]
    private ChatMessageState state;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText), nameof(HasStateText))]
    private string? error;

    public bool IsStreaming => State == ChatMessageState.Streaming;
    public string? StateText => State switch
    {
        ChatMessageState.Failed => $"Failed: {Error}",
        ChatMessageState.Cancelled => "Stopped",
        _ => null
    };
    public bool HasStateText => StateText is not null;

    public ChatMessageViewModel(ChatMessage message, string projectRoot)
    {
        Id = message.Id;
        Author = message.Author;
        content = message.Content;
        state = message.State;
        error = message.Error;
        TimeText = message.Timestamp.ToLocalTime().ToString("HH:mm");
        RefText = message.Branch is null ? null : $"{message.Branch}@{message.Commit}";
        ModelText = (message.Model, message.Effort) switch
        {
            (null, null) => null,
            (var m, null) => m,
            (null, var e) => e,
            var (m, e) => $"{m} · {e}"
        };
    }

    public string Header => string.Join("  ·  ", new[] { AuthorName, TimeText, RefText, ModelText }.Where(s => !string.IsNullOrEmpty(s)));

    public void Apply(ChatMessage message)
    {
        Content = message.Content;
        Error = message.Error;
        State = message.State;
    }
}

/// <summary>A selectable value in the agent picker (model or effort).</summary>
public partial class ChoiceViewModel(string value) : ObservableObject
{
    public string Value { get; } = value;
    [ObservableProperty] private bool isSelected;
}

/// <summary>An agent row in the picker and the availability strip.</summary>
public partial class ChatAgentViewModel(AgentOptionViewModel option) : ObservableObject
{
    public AgentOptionViewModel Option { get; } = option;
    public string Name => Option.Name;
    public bool IsAvailable => Option.IsAvailable;
    public string AvailabilityText => Option.AvailabilityText;
    [ObservableProperty] private bool isSelected;

    public void Refresh()
    {
        Option.RefreshAvailability();
        OnPropertyChanged(nameof(IsAvailable));
        OnPropertyChanged(nameof(AvailabilityText));
    }
}
