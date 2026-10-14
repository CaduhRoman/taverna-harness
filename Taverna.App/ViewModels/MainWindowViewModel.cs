using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Taverna.Core.Projects;

namespace Taverna.App.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    private readonly Func<Task<string?>> pickFolder;
    private readonly ProjectFiles files = new();
    private readonly GitIgnoreService gitIgnore = new();
    private readonly TerminalLauncher terminals = new();

    public ObservableCollection<FileNodeViewModel> RootNodes { get; } = new();
    public ObservableCollection<DocumentViewModel> OpenDocuments { get; } = new();

    [ObservableProperty] private string? projectDirectory;
    [ObservableProperty] private FileNodeViewModel? selectedNode;
    [ObservableProperty] private DocumentViewModel? activeDocument;
    [ObservableProperty] private string status = "Open a folder to get started";

    public string ProjectTitle => ProjectDirectory is null ? "NO FOLDER OPEN" : Path.GetFileName(ProjectDirectory.TrimEnd(Path.DirectorySeparatorChar));

    public MainWindowViewModel(Func<Task<string?>> pickFolder) => this.pickFolder = pickFolder;

    partial void OnProjectDirectoryChanged(string? value) => OnPropertyChanged(nameof(ProjectTitle));

    partial void OnSelectedNodeChanged(FileNodeViewModel? value)
    {
        if (value is { IsDirectory: false }) _ = OpenFileAsync(value.FullPath);
    }

    [RelayCommand]
    private async Task OpenFolderAsync()
    {
        var directory = await pickFolder();
        if (directory is null) return;

        try
        {
            await gitIgnore.EnsureTavernaIgnoredAsync(directory);
            ProjectDirectory = directory;
            OpenDocuments.Clear();
            ActiveDocument = null;
            await RefreshAsync();
            Status = directory;
        }
        catch (Exception ex) { Status = $"Open folder failed: {ex.Message}"; }
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (ProjectDirectory is null) return;
        try
        {
            var entries = await Task.Run(() => files.List(ProjectDirectory));
            RootNodes.Clear();
            foreach (var entry in entries) RootNodes.Add(ToNode(entry));
            Status = $"{RootNodes.Count} top-level entries";
        }
        catch (Exception ex) { Status = $"Refresh failed: {ex.Message}"; }
    }

    [RelayCommand]
    private void CollapseAll()
    {
        foreach (var node in RootNodes) node.CollapseRecursively();
    }

    [RelayCommand]
    private async Task SaveActiveFileAsync()
    {
        var document = ActiveDocument;
        if (document is null || !document.IsModified) return;
        try
        {
            await File.WriteAllTextAsync(document.Path, document.Text);
            document.MarkSaved();
            Status = $"Saved {document.Name}";
        }
        catch (Exception ex) { Status = $"Save failed: {ex.Message}"; }
    }

    public void OpenExternalTerminal()
    {
        if (ProjectDirectory is null) return;
        try { terminals.Open(ProjectDirectory); }
        catch (Exception ex) { Status = $"Terminal failed: {ex.Message}"; }
    }

    private async Task OpenFileAsync(string path)
    {
        var existing = OpenDocuments.FirstOrDefault(doc => string.Equals(doc.Path, path, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) { ActiveDocument = existing; return; }
        try
        {
            var text = await File.ReadAllTextAsync(path);
            if (text.Contains('\0')) { Status = "Binary files cannot be opened in the text editor."; return; }
            var document = new DocumentViewModel(path, text);
            OpenDocuments.Add(document);
            ActiveDocument = document;
            Status = path;
        }
        catch (Exception ex) { Status = $"Open file failed: {ex.Message}"; }
    }

    private static FileNodeViewModel ToNode(FileEntry entry)
    {
        var node = new FileNodeViewModel(entry.FullPath, entry.IsDirectory);
        foreach (var child in entry.Children) node.Children.Add(ToNode(child));
        return node;
    }
}
