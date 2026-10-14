using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Taverna.Core.Projects;

namespace Taverna.App.ViewModels;

public partial class FileNodeViewModel : ObservableObject
{
    public string FullPath { get; }
    public string Name { get; }
    public bool IsDirectory { get; }
    public FileKind Kind { get; }
    public FileNodeViewModel? Parent { get; }
    public string IconKey => IsDirectory && IsExpanded ? "FolderOpen" : Kind.ToString();
    public ObservableCollection<FileNodeViewModel> Children { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IconKey))]
    private bool isExpanded;

    public FileNodeViewModel(string path, bool directory, FileNodeViewModel? parent = null)
    {
        FullPath = path;
        Name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        IsDirectory = directory;
        Kind = FileKinds.Classify(Name, directory);
        Parent = parent;
    }

    public void CollapseRecursively()
    {
        IsExpanded = false;
        foreach (var node in Children)
            node.CollapseRecursively();
    }

    public void ExpandAncestors()
    {
        for (var node = Parent; node is not null; node = node.Parent) node.IsExpanded = true;
    }
}
