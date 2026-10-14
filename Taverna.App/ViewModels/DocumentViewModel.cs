using CommunityToolkit.Mvvm.ComponentModel;

namespace Taverna.App.ViewModels;

public partial class DocumentViewModel : ObservableObject
{
    public string Path { get; }
    public string Name => System.IO.Path.GetFileName(Path);
    public string TabTitle => IsModified ? $"●  {Name}" : Name;
    private string savedText;

    [ObservableProperty] private string text;
    [ObservableProperty] private bool isModified;

    public DocumentViewModel(string path, string text)
    {
        Path = path;
        this.text = text;
        savedText = text;
    }

    partial void OnTextChanged(string value) => IsModified = value != savedText;
    partial void OnIsModifiedChanged(bool value) => OnPropertyChanged(nameof(TabTitle));

    public void MarkSaved()
    {
        savedText = Text;
        IsModified = false;
    }
}
