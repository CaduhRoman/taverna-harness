using Avalonia.Controls;

namespace Taverna.App.Views;

public partial class TasksPanelView : UserControl
{
    public TasksPanelView() => InitializeComponent();

    /// <summary>Keeps a streaming log pinned to its newest line.</summary>
    private void OnLogChanged(object? sender, TextChangedEventArgs e)
    {
        if (sender is TextBox { Text: { } text } box) box.CaretIndex = text.Length;
    }
}
