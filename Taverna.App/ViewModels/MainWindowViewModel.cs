using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Taverna.Core.Git;
using Taverna.Core.Projects;

namespace Taverna.App.ViewModels;

public enum UnsavedChoice { Save, Discard, Cancel }

public partial class MainWindowViewModel : ObservableObject
{
    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private readonly Func<Task<string?>> pickFolder;
    private readonly Func<DocumentViewModel, Task<UnsavedChoice>> confirmUnsaved;
    private readonly ProjectFiles files = new();
    private readonly GitIgnoreService gitIgnore = new();
    private readonly GitService git = new();
    private readonly TerminalLauncher terminals = new();
    private readonly Dictionary<string, FileNodeViewModel> nodesByPath = new(PathComparer);
    private bool creatingDirectory;
    private string? createParent;

    public ObservableCollection<FileNodeViewModel> RootNodes { get; } = new();
    public EditorPaneViewModel PrimaryPane { get; }
    public EditorPaneViewModel SecondaryPane { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProjectTitle), nameof(HasProject), nameof(WindowTitle), nameof(ActiveFilePath))]
    private string? projectDirectory;

    [ObservableProperty] private EditorPaneViewModel activePane;
    [ObservableProperty] private bool isSplit;
    [ObservableProperty] private bool isExplorerVisible = true;
    [ObservableProperty] private bool isChatVisible = true;
    [ObservableProperty] private FileNodeViewModel? selectedNode;
    [ObservableProperty] private string? gitBranch;
    [ObservableProperty] private string status = "";

    [ObservableProperty] private bool isCreatingEntry;
    [ObservableProperty] private string newEntryName = "";
    [ObservableProperty] private string newEntryHint = "";

    public DocumentViewModel? ActiveDocument => ActivePane.ActiveDocument;
    public bool HasProject => ProjectDirectory is not null;
    public string ProjectTitle => ProjectDirectory is null
        ? "No folder open"
        : Path.GetFileName(Path.TrimEndingDirectorySeparator(ProjectDirectory));

    public string? ActiveFilePath => ActiveDocument is null ? null
        : ProjectDirectory is null ? ActiveDocument.Path
        : Path.GetRelativePath(ProjectDirectory, ActiveDocument.Path);

    public string WindowTitle => ActiveDocument is not null ? $"{ActiveDocument.Name} — {ProjectTitle} — Taverna"
        : HasProject ? $"{ProjectTitle} — Taverna" : "Taverna";

    private IEnumerable<DocumentViewModel> AllDocuments => PrimaryPane.Documents.Concat(SecondaryPane.Documents).Distinct();

    public MainWindowViewModel(Func<Task<string?>> pickFolder, Func<DocumentViewModel, Task<UnsavedChoice>> confirmUnsaved)
    {
        this.pickFolder = pickFolder;
        this.confirmUnsaved = confirmUnsaved;
        PrimaryPane = new EditorPaneViewModel(this, isPrimary: true) { IsActive = true };
        SecondaryPane = new EditorPaneViewModel(this, isPrimary: false);
        activePane = PrimaryPane;
    }

    partial void OnActivePaneChanged(EditorPaneViewModel? oldValue, EditorPaneViewModel newValue)
    {
        if (oldValue is not null) oldValue.IsActive = false;
        newValue.IsActive = true;
        NotifyActiveDocument();
    }

    internal void OnPaneDocumentChanged(EditorPaneViewModel pane)
    {
        if (pane == ActivePane) NotifyActiveDocument();
    }

    private void NotifyActiveDocument()
    {
        OnPropertyChanged(nameof(ActiveDocument));
        OnPropertyChanged(nameof(ActiveFilePath));
        OnPropertyChanged(nameof(WindowTitle));
        // Keep the explorer highlight on the file being edited.
        if (ActiveDocument is not null && nodesByPath.TryGetValue(ActiveDocument.Path, out var node)) SelectedNode = node;
    }

    // ---------- Project & explorer ----------

    [RelayCommand]
    private async Task OpenFolderAsync()
    {
        var directory = await pickFolder();
        if (directory is not null) await OpenProjectAsync(directory);
    }

    public async Task OpenProjectAsync(string directory)
    {
        if (!await ResolveUnsavedAsync(AllDocuments.ToList())) return;

        try
        {
            await gitIgnore.EnsureTavernaIgnoredAsync(directory);
            CloseAllPanes();
            IsCreatingEntry = false;
            nodesByPath.Clear();
            SelectedNode = null;
            ProjectDirectory = directory;
            await RefreshAsync();
            Status = "";
        }
        catch (Exception ex) { Status = $"Open folder failed: {ex.Message}"; }
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        var directory = ProjectDirectory;
        if (directory is null) return;
        try
        {
            var expanded = nodesByPath.Values.Where(n => n.IsExpanded).Select(n => n.FullPath).ToHashSet(PathComparer);
            var selected = SelectedNode?.FullPath;
            var entries = await Task.Run(() => files.List(directory));

            nodesByPath.Clear();
            RootNodes.Clear();
            foreach (var entry in entries) RootNodes.Add(ToNode(entry, null));
            foreach (var node in nodesByPath.Values.Where(n => expanded.Contains(n.FullPath))) node.IsExpanded = true;
            SelectedNode = selected is not null && nodesByPath.TryGetValue(selected, out var match) ? match : null;

            GitBranch = await DetectBranchAsync(directory);
        }
        catch (Exception ex) { Status = $"Refresh failed: {ex.Message}"; }
    }

    [RelayCommand]
    private void CollapseAll()
    {
        foreach (var node in RootNodes) node.CollapseRecursively();
    }

    [RelayCommand]
    private async Task ActivateNodeAsync(FileNodeViewModel? node)
    {
        if (node is null) return;
        if (node.IsDirectory) node.IsExpanded = !node.IsExpanded;
        else await OpenFileAsync(node.FullPath);
    }

    [RelayCommand] private void BeginNewFile() => BeginCreate(directory: false);
    [RelayCommand] private void BeginNewFolder() => BeginCreate(directory: true);

    private void BeginCreate(bool directory)
    {
        if (ProjectDirectory is null) return;
        createParent = SelectedNode switch
        {
            { IsDirectory: true } folder => folder.FullPath,
            { } file => Path.GetDirectoryName(file.FullPath),
            null => ProjectDirectory
        } ?? ProjectDirectory;
        creatingDirectory = directory;
        var relative = Path.GetRelativePath(ProjectDirectory, createParent);
        var location = relative == "." ? ProjectTitle : relative;
        NewEntryHint = directory ? $"New folder in {location}" : $"New file in {location}";
        NewEntryName = "";
        IsCreatingEntry = true;
    }

    [RelayCommand]
    private async Task CommitNewEntryAsync()
    {
        if (!IsCreatingEntry || ProjectDirectory is null || createParent is null) return;
        if (string.IsNullOrWhiteSpace(NewEntryName)) { CancelNewEntry(); return; }
        try
        {
            var path = creatingDirectory
                ? files.CreateDirectory(ProjectDirectory, createParent, NewEntryName)
                : files.CreateFile(ProjectDirectory, createParent, NewEntryName);
            IsCreatingEntry = false;
            if (nodesByPath.TryGetValue(createParent, out var parent)) parent.IsExpanded = true;
            await RefreshAsync();
            if (nodesByPath.TryGetValue(path, out var created))
            {
                created.ExpandAncestors();
                SelectedNode = created;
            }
            if (!creatingDirectory) await OpenFileAsync(path);
            Status = $"Created {Path.GetFileName(path)}";
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            // Keep the input open so the name can be corrected.
            Status = ex.Message;
        }
    }

    [RelayCommand]
    private void CancelNewEntry()
    {
        IsCreatingEntry = false;
        NewEntryName = "";
    }

    public void OpenExternalTerminal()
    {
        if (ProjectDirectory is null) return;
        try { terminals.Open(ProjectDirectory); }
        catch (Exception ex) { Status = $"Terminal failed: {ex.Message}"; }
    }

    // ---------- Documents ----------

    private async Task OpenFileAsync(string path)
    {
        var existing = AllDocuments.FirstOrDefault(doc => PathComparer.Equals(doc.Path, path));
        if (existing is not null) { ActivePane.Open(existing); return; }
        try
        {
            var text = await File.ReadAllTextAsync(path);
            if (text.Contains('\0')) { Status = "Binary files cannot be opened in the text editor."; return; }
            ActivePane.Open(new DocumentViewModel(path, text));
            Status = "";
        }
        catch (Exception ex) { Status = $"Open file failed: {ex.Message}"; }
    }

    [RelayCommand]
    private Task SaveActiveFileAsync() =>
        ActiveDocument is { IsModified: true } document ? SaveAsync(document) : Task.CompletedTask;

    [RelayCommand]
    private async Task SaveAllAsync()
    {
        foreach (var document in AllDocuments.Where(d => d.IsModified).ToList())
            if (!await SaveAsync(document)) return;
    }

    private async Task<bool> SaveAsync(DocumentViewModel document)
    {
        var text = document.Text;
        try
        {
            await File.WriteAllTextAsync(document.Path, text);
            // Edits typed while the write was in flight stay marked as unsaved.
            if (document.Text == text) document.MarkSaved();
            Status = $"Saved {document.Name}";
            return true;
        }
        catch (Exception ex)
        {
            Status = $"Save failed: {ex.Message}";
            return false;
        }
    }

    public async Task CloseDocumentAsync(EditorPaneViewModel pane, DocumentViewModel document)
    {
        var openElsewhere = IsSplit && Other(pane).Documents.Contains(document);
        if (!openElsewhere && !await ResolveUnsavedAsync([document])) return;
        pane.Remove(document);
        NormalizeSplit();
    }

    public async Task CloseOthersAsync(EditorPaneViewModel pane, DocumentViewModel keep)
    {
        var closing = pane.Documents.Where(d => d != keep).ToList();
        if (!await ResolveUnsavedAsync(closing.Where(d => !(IsSplit && Other(pane).Documents.Contains(d))).ToList())) return;
        foreach (var document in closing) pane.Remove(document);
        pane.ActiveDocument = keep;
    }

    public async Task CloseAllAsync(EditorPaneViewModel pane)
    {
        var closing = pane.Documents.ToList();
        if (!await ResolveUnsavedAsync(closing.Where(d => !(IsSplit && Other(pane).Documents.Contains(d))).ToList())) return;
        foreach (var document in closing) pane.Remove(document);
        NormalizeSplit();
    }

    public void MoveToOtherPane(EditorPaneViewModel pane, DocumentViewModel document)
    {
        var target = Other(pane);
        IsSplit = true;
        target.Open(document);
        pane.Remove(document);
        ActivePane = target;
        NormalizeSplit();
    }

    [RelayCommand]
    private Task CloseActiveDocumentAsync() =>
        ActiveDocument is { } document ? CloseDocumentAsync(ActivePane, document) : Task.CompletedTask;

    [RelayCommand] private void NextTab() => ActivePane.SelectRelative(1);
    [RelayCommand] private void PreviousTab() => ActivePane.SelectRelative(-1);

    [RelayCommand]
    private void SplitEditor()
    {
        var document = ActiveDocument;
        if (document is null) { Status = "Open a file to split the editor."; return; }
        var target = Other(ActivePane);
        IsSplit = true;
        target.Open(document);
        ActivePane = target;
    }

    [RelayCommand]
    private void CloseSplit()
    {
        if (!IsSplit) return;
        var preferred = ActivePane == SecondaryPane || PrimaryPane.ActiveDocument is null
            ? SecondaryPane.ActiveDocument ?? PrimaryPane.ActiveDocument
            : PrimaryPane.ActiveDocument;
        // Nothing is closed: tabs from the right split move back into the remaining pane.
        foreach (var document in SecondaryPane.Documents.ToList())
            if (!PrimaryPane.Documents.Contains(document)) PrimaryPane.Documents.Add(document);
        SecondaryPane.Clear();
        IsSplit = false;
        ActivePane = PrimaryPane;
        if (preferred is not null) PrimaryPane.ActiveDocument = preferred;
    }

    [RelayCommand] private void ToggleExplorer() => IsExplorerVisible = !IsExplorerVisible;
    [RelayCommand] private void ToggleChat() => IsChatVisible = !IsChatVisible;

    public bool HasUnsavedChanges => AllDocuments.Any(d => d.IsModified);

    public Task<bool> ConfirmExitAsync() => ResolveUnsavedAsync(AllDocuments.ToList());

    private async Task<bool> ResolveUnsavedAsync(IReadOnlyCollection<DocumentViewModel> documents)
    {
        foreach (var document in documents.Where(d => d.IsModified).Distinct().ToList())
        {
            var pane = PrimaryPane.Documents.Contains(document) ? PrimaryPane : SecondaryPane;
            pane.ActiveDocument = document;
            var choice = await confirmUnsaved(document);
            if (choice == UnsavedChoice.Cancel) return false;
            if (choice == UnsavedChoice.Save && !await SaveAsync(document)) return false;
        }
        return true;
    }

    private void NormalizeSplit()
    {
        // A split never stays open with an empty side.
        if (IsSplit && (SecondaryPane.Documents.Count == 0 || PrimaryPane.Documents.Count == 0)) CloseSplit();
    }

    private void CloseAllPanes()
    {
        PrimaryPane.Clear();
        SecondaryPane.Clear();
        IsSplit = false;
        ActivePane = PrimaryPane;
    }

    private EditorPaneViewModel Other(EditorPaneViewModel pane) => pane == PrimaryPane ? SecondaryPane : PrimaryPane;

    private async Task<string?> DetectBranchAsync(string directory)
    {
        try { return (await git.DetectRepositoryAsync(directory))?.Branch; }
        catch (Exception) { return null; } // Repositories without commits have no branch to show.
    }

    private FileNodeViewModel ToNode(FileEntry entry, FileNodeViewModel? parent)
    {
        var node = new FileNodeViewModel(entry.FullPath, entry.IsDirectory, parent);
        nodesByPath[entry.FullPath] = node;
        foreach (var child in entry.Children) node.Children.Add(ToNode(child, node));
        return node;
    }
}
