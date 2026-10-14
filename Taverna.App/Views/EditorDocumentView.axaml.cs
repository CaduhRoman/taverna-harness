using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.TextMate;
using TextMateSharp.Grammars;
using Taverna.App.ViewModels;

namespace Taverna.App.Views;

/// <summary>
/// Hosts one AvaloniaEdit editor per pane. The view is recycled across tabs, so it swaps the
/// shared TextDocument and restores each file's caret and scroll position.
/// </summary>
public partial class EditorDocumentView : UserControl
{
    private static readonly RegistryOptions Grammars = new(ThemeName.DarkPlus);

    private sealed record ViewState(int CaretOffset, double VerticalOffset, double HorizontalOffset);

    private readonly ConditionalWeakTable<DocumentViewModel, ViewState> states = new();
    private readonly TextDocument emptyDocument = new();
    private DocumentViewModel? document;
    private TextMate.Installation? syntax;

    public EditorDocumentView()
    {
        InitializeComponent();
        ConfigureEditor();
        CodeEditor.TextArea.Caret.PositionChanged += (_, _) => ReportCaret();
        DataContextChanged += (_, _) => LoadDocument();
        AttachedToVisualTree += (_, _) => EnsureSyntax();
        DetachedFromVisualTree += (_, _) =>
        {
            syntax?.Dispose();
            syntax = null;
        };
    }

    public void FocusEditor() => CodeEditor.TextArea.Focus();

    private void ConfigureEditor()
    {
        CodeEditor.Options.HighlightCurrentLine = true;
        CodeEditor.Options.AllowScrollBelowDocument = true;

        var area = CodeEditor.TextArea;
        area.SelectionBrush = Brush("Brush.Editor.Selection");
        area.SelectionBorder = null;
        area.SelectionForeground = null;
        area.SelectionCornerRadius = 2;
        area.Caret.CaretBrush = Brush("Brush.Editor.Caret");
        area.TextView.CurrentLineBackground = Brush("Brush.Editor.CurrentLine");
        // AvaloniaEdit ignores a null border and falls back to its green default.
        area.TextView.CurrentLineBorder = new Pen(Brushes.Transparent, 0);

        foreach (var margin in area.LeftMargins)
        {
            if (margin is LineNumberMargin numbers) numbers.Margin = new Thickness(12, 0, 16, 0);
            else if (margin is Line separator) separator.IsVisible = false;
        }
    }

    private void LoadDocument()
    {
        SaveState();
        document = DataContext as DocumentViewModel;
        CodeEditor.Document = document?.Document ?? emptyDocument;
        CodeEditor.IsReadOnly = document is null;
        // A fresh installation per document: reusing one across fast tab switches can leave
        // the background tokenizer of the previous document attached and drop all coloring.
        syntax?.Dispose();
        syntax = null;
        EnsureSyntax();
        RestoreState();
        ReportCaret();
    }

    private void EnsureSyntax()
    {
        if (syntax is not null || document is null) return;
        syntax = CodeEditor.InstallTextMate(Grammars);
        if (Grammars.GetScopeByLanguageId(document.Language.TextMateId) is { } scope) syntax.SetGrammar(scope);
    }

    private void SaveState()
    {
        if (document is null) return;
        states.AddOrUpdate(document, new ViewState(CodeEditor.CaretOffset, CodeEditor.VerticalOffset, CodeEditor.HorizontalOffset));
    }

    private void RestoreState()
    {
        var target = document;
        if (target is null || !states.TryGetValue(target, out var state))
        {
            CodeEditor.CaretOffset = 0;
            CodeEditor.ScrollToVerticalOffset(0);
            CodeEditor.ScrollToHorizontalOffset(0);
            return;
        }
        CodeEditor.CaretOffset = Math.Min(state.CaretOffset, target.Document.TextLength);
        // Scrolling needs the new document laid out first.
        Dispatcher.UIThread.Post(() =>
        {
            if (document != target) return;
            CodeEditor.ScrollToVerticalOffset(state.VerticalOffset);
            CodeEditor.ScrollToHorizontalOffset(state.HorizontalOffset);
        }, DispatcherPriority.Loaded);
    }

    private void ReportCaret()
    {
        if (document is null) return;
        var caret = CodeEditor.TextArea.Caret;
        document.CaretLine = caret.Line;
        document.CaretColumn = caret.Column;
    }

    private static IBrush? Brush(string key)
    {
        var app = Application.Current;
        return app is not null && app.TryGetResource(key, app.ActualThemeVariant, out var value) ? value as IBrush : null;
    }
}
