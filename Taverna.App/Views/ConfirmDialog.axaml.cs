using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Taverna.App.Views;

/// <summary>Short yes/no confirmation for destructive actions. Cancel is the default.</summary>
public partial class ConfirmDialog : Window
{
    public ConfirmDialog() => InitializeComponent();

    public ConfirmDialog(string heading, string message, string confirmLabel) : this()
    {
        Heading.Text = heading;
        Message.Text = message;
        ConfirmButton.Content = confirmLabel;
    }

    private void OnConfirm(object? sender, RoutedEventArgs e) => Close(true);
    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
