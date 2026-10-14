using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Taverna.App.ViewModels;

public partial class FileNodeViewModel : ObservableObject
{
    public string FullPath { get; }
    public string Name { get; }
    public bool IsDirectory { get; }
    public string Glyph => IsDirectory ? "▸" : "·";
    public ObservableCollection<FileNodeViewModel> Children { get; } = new();

    [ObservableProperty] private bool isExpanded;

    public FileNodeViewModel(string path, bool directory)
    {
        FullPath = path;
        Name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        IsDirectory = directory;
    }

    public void CollapseRecursively()
    {
        IsExpanded = false;
        foreach (var node in Children)
            node.CollapseRecursively();
    }
}
