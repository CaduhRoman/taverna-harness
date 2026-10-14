using Avalonia.Controls;
using Avalonia.Interactivity;
using Taverna.App.ViewModels;

namespace Taverna.App.Views;

public partial class UnsavedChangesDialog : Window
{
    public UnsavedChangesDialog() => InitializeComponent();

    public UnsavedChangesDialog(string fileName) : this() =>
        Heading.Text = $"Save changes to {fileName}?";

    private void OnSave(object? sender, RoutedEventArgs e) => Close(UnsavedChoice.Save);
    private void OnDiscard(object? sender, RoutedEventArgs e) => Close(UnsavedChoice.Discard);
    private void OnCancel(object? sender, RoutedEventArgs e) => Close(UnsavedChoice.Cancel);
}
