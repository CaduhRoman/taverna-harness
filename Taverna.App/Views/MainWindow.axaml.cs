using System.ComponentModel;
using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Taverna.App.Services;
using Taverna.App.ViewModels;

namespace Taverna.App.Views;

public partial class MainWindow : Window
{
    private const double MinExplorerWidth = 180, MaxExplorerWidth = 640;
    private const double MinChatWidth = 260, MaxChatWidth = 720;
    private const double MinPaneWidth = 220;
    private const double CaptionButtonsWidth = 144;

    private readonly MainWindowViewModel viewModel;
    private readonly LayoutSettings layout = LayoutSettings.Load();
    private bool closeConfirmed;

    public MainWindow() : this(null) { }

    public MainWindow(string? initialFolder)
    {
        InitializeComponent();
        viewModel = new MainWindowViewModel(PickFolderAsync, ConfirmUnsavedAsync)
        {
            IsExplorerVisible = layout.ExplorerVisible,
            IsChatVisible = layout.ChatVisible
        };
        DataContext = viewModel;

        Width = layout.WindowWidth;
        Height = layout.WindowHeight;
        if (layout.Maximized) WindowState = WindowState.Maximized;
        if (OperatingSystem.IsWindows())
        {
            // The top bar doubles as the title bar; empty areas of it drag the window.
            ExtendClientAreaToDecorationsHint = true;
            ExtendClientAreaChromeHints = Avalonia.Platform.ExtendClientAreaChromeHints.PreferSystemChrome;
            ExtendClientAreaTitleBarHeightHint = this.TryFindResource("Size.TopBar", out var height) && height is double h ? h : 40;
            TopBar.Margin = new Avalonia.Thickness(14, 0, CaptionButtonsWidth, 0);
        }
        Opened += (_, _) => FitToScreen();
        Workbench.ColumnDefinitions[2].MinWidth = MinPaneWidth;
        ApplySidePanels();
        ApplySplit();

        viewModel.PropertyChanged += OnViewModelChanged;
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        FileTree.KeyDown += OnTreeKeyDown;
        NewEntryBox.AddHandler(KeyDownEvent, OnNewEntryKeyDown, RoutingStrategies.Tunnel);
        // Clicking elsewhere in the window abandons the new entry; switching to another app does not.
        // Deactivating the window clears focus entirely, so only a focus move inside it counts.
        NewEntryBox.LostFocus += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            var focused = FocusManager?.GetFocusedElement();
            if (focused is not null && focused != NewEntryBox && viewModel.IsCreatingEntry)
                viewModel.CancelNewEntryCommand.Execute(null);
        });
        Closing += OnClosing;
        // A folder passed on the command line wins; otherwise reopen the last session's folder.
        var startupFolder = initialFolder ?? layout.LastProjectDirectory;
        if (startupFolder is not null && Directory.Exists(startupFolder))
            Opened += async (_, _) => await viewModel.OpenProjectAsync(Path.GetFullPath(startupFolder));
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

    private async Task<UnsavedChoice> ConfirmUnsavedAsync(DocumentViewModel document) =>
        await new UnsavedChangesDialog(document.Name).ShowDialog<UnsavedChoice?>(this) ?? UnsavedChoice.Cancel;

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainWindowViewModel.IsExplorerVisible):
            case nameof(MainWindowViewModel.IsChatVisible):
                ApplySidePanels();
                break;
            case nameof(MainWindowViewModel.ProjectDirectory) when viewModel.ProjectDirectory is not null:
                // Persist right away so the folder survives even if the app does not exit cleanly.
                layout.LastProjectDirectory = viewModel.ProjectDirectory;
                layout.Save();
                break;
            case nameof(MainWindowViewModel.IsSplit):
                ApplySplit();
                break;
            case nameof(MainWindowViewModel.ActivePane):
                var view = viewModel.ActivePane == viewModel.SecondaryPane ? SecondaryPaneView : PrimaryPaneView;
                if (!view.IsKeyboardFocusWithin) Dispatcher.UIThread.Post(view.FocusEditor, DispatcherPriority.Loaded);
                break;
            case nameof(MainWindowViewModel.IsCreatingEntry) when viewModel.IsCreatingEntry:
                Dispatcher.UIThread.Post(() => NewEntryBox.Focus(), DispatcherPriority.Loaded);
                break;
        }
    }

    // ---------- Layout ----------

    private void ApplySidePanels()
    {
        CaptureLayout();
        SetSidePanel(Workbench.ColumnDefinitions[0], ExplorerPanel, ExplorerSplitter, viewModel.IsExplorerVisible,
            layout.ExplorerWidth, MinExplorerWidth, MaxExplorerWidth);
        SetSidePanel(Workbench.ColumnDefinitions[4], ChatPanel, ChatSplitter, viewModel.IsChatVisible,
            layout.ChatWidth, MinChatWidth, MaxChatWidth);
    }

    private static void SetSidePanel(ColumnDefinition column, Control panel, Control splitter, bool visible,
        double width, double min, double max)
    {
        panel.IsVisible = splitter.IsVisible = visible;
        column.MinWidth = visible ? min : 0;
        column.MaxWidth = visible ? max : double.PositiveInfinity;
        column.Width = new GridLength(visible ? width : 0);
    }

    private void ApplySplit()
    {
        CaptureLayout();
        var columns = EditorGrid.ColumnDefinitions;
        var split = viewModel.IsSplit;
        EditorSplitter.IsVisible = SecondaryPaneView.IsVisible = split;
        columns[0].Width = new GridLength(split ? layout.SplitRatio : 1, GridUnitType.Star);
        columns[2].Width = split ? new GridLength(1 - layout.SplitRatio, GridUnitType.Star) : new GridLength(0);
        columns[0].MinWidth = columns[2].MinWidth = split ? MinPaneWidth : 0;
    }

    /// <summary>Copies the user's current panel sizes into the layout before anything is hidden or saved.</summary>
    private void CaptureLayout()
    {
        if (ExplorerPanel.IsVisible && Workbench.ColumnDefinitions[0].ActualWidth > 0)
            layout.ExplorerWidth = Workbench.ColumnDefinitions[0].ActualWidth;
        if (ChatPanel.IsVisible && Workbench.ColumnDefinitions[4].ActualWidth > 0)
            layout.ChatWidth = Workbench.ColumnDefinitions[4].ActualWidth;
        if (SecondaryPaneView.IsVisible)
        {
            var left = EditorGrid.ColumnDefinitions[0].ActualWidth;
            var right = EditorGrid.ColumnDefinitions[2].ActualWidth;
            if (left + right > 0) layout.SplitRatio = left / (left + right);
        }
    }

    /// <summary>Keeps a remembered size from overflowing a smaller monitor.</summary>
    private void FitToScreen()
    {
        if (WindowState != WindowState.Normal || Screens.ScreenFromWindow(this) is not { } screen) return;
        var area = screen.WorkingArea;
        var maxWidth = area.Width / screen.Scaling;
        var maxHeight = area.Height / screen.Scaling;
        if (Width <= maxWidth && Height <= maxHeight) return;
        Width = Math.Min(Width, maxWidth);
        Height = Math.Min(Height, maxHeight);
        Position = new Avalonia.PixelPoint(
            area.X + (int)((area.Width - Width * screen.Scaling) / 2),
            area.Y + (int)((area.Height - Height * screen.Scaling) / 2));
    }

    private void SaveLayout()
    {
        CaptureLayout();
        layout.Maximized = WindowState == WindowState.Maximized;
        if (WindowState == WindowState.Normal)
        {
            layout.WindowWidth = Width;
            layout.WindowHeight = Height;
        }
        layout.ExplorerVisible = viewModel.IsExplorerVisible;
        layout.ChatVisible = viewModel.IsChatVisible;
        layout.Save();
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (closeConfirmed || !viewModel.HasUnsavedChanges)
        {
            SaveLayout();
            return;
        }
        e.Cancel = true;
        if (!await viewModel.ConfirmExitAsync()) return;
        closeConfirmed = true;
        Dispatcher.UIThread.Post(Close);
    }

    // ---------- Input ----------

    private void OnTreeRowTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as Control)?.DataContext is FileNodeViewModel node) viewModel.ActivateNodeCommand.Execute(node);
    }

    private void OnTreeKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || viewModel.SelectedNode is null) return;
        viewModel.ActivateNodeCommand.Execute(viewModel.SelectedNode);
        e.Handled = true;
    }

    private void OnNewEntryKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) viewModel.CommitNewEntryCommand.Execute(null);
        else if (e.Key == Key.Escape) viewModel.CancelNewEntryCommand.Execute(null);
        else return;
        e.Handled = true;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        var alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
        ICommand? command = (e.Key, shift, alt) switch
        {
            (Key.S, false, false) => viewModel.SaveActiveFileCommand,
            (Key.S, true, false) => viewModel.SaveAllCommand,
            (Key.O, false, false) => viewModel.OpenFolderCommand,
            (Key.N, false, false) => viewModel.BeginNewFileCommand,
            (Key.W or Key.F4, false, false) => viewModel.CloseActiveDocumentCommand,
            (Key.Tab or Key.PageDown, false, false) => viewModel.NextTabCommand,
            (Key.Tab, true, false) or (Key.PageUp, false, false) => viewModel.PreviousTabCommand,
            (Key.B, false, false) => viewModel.ToggleExplorerCommand,
            (Key.B, false, true) => viewModel.ToggleChatCommand,
            (Key.OemPipe or Key.OemBackslash, false, false) => viewModel.SplitEditorCommand,
            _ => null
        };
        if (command is not null)
        {
            command.Execute(null);
            e.Handled = true;
        }
        else if (shift && e.Key == Key.OemQuotes)
        {
            viewModel.OpenExternalTerminal();
            e.Handled = true;
        }
    }
}
