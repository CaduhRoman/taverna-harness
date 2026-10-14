using Avalonia.Controls;
using Avalonia.Input;
using Taverna.App.ViewModels;

namespace Taverna.App.Views;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel viewModel;

    public MainWindow()
    {
        InitializeComponent();
        viewModel = new MainWindowViewModel(PickFolderAsync);
        DataContext = viewModel;
        KeyDown += OnKeyDown;
    }

    private async Task<string?> PickFolderAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions
        {
            Title = "Open project folder",
            AllowMultiple = false
        });
        var folder = folders.FirstOrDefault();
        return folder is not null && folder.Path.IsFile ? folder.Path.LocalPath : null;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
        if (e.Key == Key.S && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            viewModel.SaveActiveFileCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.KeyModifiers.HasFlag(KeyModifiers.Shift) && e.Key.ToString() == "OemQuotes")
        {
            viewModel.OpenExternalTerminal();
            e.Handled = true;
        }
    }
}
