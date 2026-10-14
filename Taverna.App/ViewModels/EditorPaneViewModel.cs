using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Taverna.App.ViewModels;

/// <summary>One editor split: an ordered set of tabs with its own active document.</summary>
public partial class EditorPaneViewModel : ObservableObject
{
    public MainWindowViewModel Workspace { get; }
    public bool IsPrimary { get; }
    public ObservableCollection<DocumentViewModel> Documents { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSplit))]
    private DocumentViewModel? activeDocument;

    [ObservableProperty] private bool isActive;

    public bool CanSplit => IsPrimary && ActiveDocument is not null;

    public EditorPaneViewModel(MainWindowViewModel workspace, bool isPrimary)
    {
        Workspace = workspace;
        IsPrimary = isPrimary;
    }

    partial void OnActiveDocumentChanged(DocumentViewModel? value) => Workspace.OnPaneDocumentChanged(this);

    public void Open(DocumentViewModel document)
    {
        if (!Documents.Contains(document))
        {
            // New tabs open next to the current one, like Fleet.
            var index = ActiveDocument is null ? Documents.Count : Documents.IndexOf(ActiveDocument) + 1;
            Documents.Insert(index, document);
        }
        ActiveDocument = document;
    }

    public void Remove(DocumentViewModel document)
    {
        var index = Documents.IndexOf(document);
        if (index < 0) return;
        var wasActive = ActiveDocument == document;
        Documents.RemoveAt(index);
        if (wasActive || ActiveDocument is null)
            ActiveDocument = Documents.Count == 0 ? null : Documents[Math.Min(index, Documents.Count - 1)];
    }

    public void Move(DocumentViewModel document, int newIndex)
    {
        var oldIndex = Documents.IndexOf(document);
        newIndex = Math.Clamp(newIndex, 0, Documents.Count - 1);
        if (oldIndex < 0 || oldIndex == newIndex) return;
        var active = ActiveDocument;
        Documents.Move(oldIndex, newIndex);
        // The tab strip may drop its selection while the item moves.
        if (ActiveDocument != active) ActiveDocument = active;
    }

    public void Clear()
    {
        Documents.Clear();
        ActiveDocument = null;
    }

    public void SelectRelative(int offset)
    {
        if (Documents.Count < 2 || ActiveDocument is null) return;
        var index = (Documents.IndexOf(ActiveDocument) + offset + Documents.Count) % Documents.Count;
        ActiveDocument = Documents[index];
    }

    [RelayCommand]
    private void Activate() => Workspace.ActivePane = this;

    [RelayCommand]
    private Task CloseAsync(DocumentViewModel? document) =>
        document is null ? Task.CompletedTask : Workspace.CloseDocumentAsync(this, document);

    [RelayCommand]
    private void Split()
    {
        Workspace.ActivePane = this;
        Workspace.SplitEditorCommand.Execute(null);
    }

    [RelayCommand]
    private void CloseSplit() => Workspace.CloseSplitCommand.Execute(null);
}
