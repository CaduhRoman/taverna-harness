using System.ComponentModel;
using AvaloniaEdit.Document;
using CommunityToolkit.Mvvm.ComponentModel;
using Taverna.Core.Projects;

namespace Taverna.App.ViewModels;

/// <summary>
/// One open file. The TextDocument is shared by every editor pane showing the file,
/// so split views stay in sync and keep a single undo history.
/// </summary>
public partial class DocumentViewModel : ObservableObject
{
    public string Path { get; }
    public string Name => System.IO.Path.GetFileName(Path);
    public string IconKey { get; }
    public EditorLanguage Language { get; }
    public TextDocument Document { get; }
    public string Text => Document.Text;
    public string CaretLabel => $"Ln {CaretLine}, Col {CaretColumn}";

    [ObservableProperty] private bool isModified;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CaretLabel))]
    private int caretLine = 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CaretLabel))]
    private int caretColumn = 1;

    public DocumentViewModel(string path, string text)
    {
        Path = path;
        IconKey = FileKinds.Classify(Name, isDirectory: false).ToString();
        Language = EditorLanguages.For(path);
        Document = new TextDocument(text) { FileName = path };
        // Undoing back to the saved state clears the modified marker.
        Document.UndoStack.PropertyChanged += UndoStackChanged;
    }

    private void UndoStackChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(UndoStack.IsOriginalFile)) IsModified = !Document.UndoStack.IsOriginalFile;
    }

    public void MarkSaved()
    {
        Document.UndoStack.MarkAsOriginalFile();
        IsModified = false;
    }
}
