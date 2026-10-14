using Avalonia.Controls;
using AvaloniaEdit.TextMate;
using TextMateSharp.Grammars;
using Taverna.App.ViewModels;

namespace Taverna.App.Views;

public partial class EditorDocumentView : UserControl
{
    private DocumentViewModel? document;
    private TextMate.Installation? syntax;
    private bool loading;

    public EditorDocumentView()
    {
        InitializeComponent();
        CodeEditor.TextChanged += (_, _) =>
        {
            if (!loading && document is not null) document.Text = CodeEditor.Text;
        };
        DataContextChanged += (_, _) => LoadDocument();
        DetachedFromVisualTree += (_, _) => syntax?.Dispose();
    }

    private void LoadDocument()
    {
        if (document is not null) document.PropertyChanged -= DocumentChanged;
        document = DataContext as DocumentViewModel;
        loading = true;
        CodeEditor.Text = document?.Text ?? string.Empty;
        loading = false;

        syntax?.Dispose();
        syntax = null;
        if (document is null) return;

        document.PropertyChanged += DocumentChanged;
        var grammars = new RegistryOptions(ThemeName.DarkPlus);
        syntax = CodeEditor.InstallTextMate(grammars);
        var language = Path.GetExtension(document.Path).ToLowerInvariant() switch
        {
            ".cs" => "csharp", ".py" => "python", ".js" or ".jsx" => "javascript",
            ".ts" or ".tsx" => "typescript", ".json" => "json", ".html" => "html",
            ".css" => "css", ".axaml" or ".xml" => "xml", ".md" => "markdown",
            ".sh" => "shellscript", ".yml" or ".yaml" => "yaml", _ => "plaintext"
        };
        syntax.SetGrammar(grammars.GetScopeByLanguageId(language));
    }

    private void DocumentChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(DocumentViewModel.Text) || document is null || CodeEditor.Text == document.Text) return;
        loading = true;
        CodeEditor.Text = document.Text;
        loading = false;
    }
}
