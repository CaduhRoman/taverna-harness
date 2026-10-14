using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Taverna.App.ViewModels;

namespace Taverna.App.Views;

public partial class EditorPaneView : UserControl
{
    private const double DragThreshold = 6;
    private DocumentViewModel? dragged;
    private Point dragOrigin;
    private bool dragging;
    private ScrollViewer? tabScroller;

    private EditorPaneViewModel? Pane => DataContext as EditorPaneViewModel;

    public EditorPaneView()
    {
        InitializeComponent();
        // Any interaction inside a split makes it the target for files opened from the explorer.
        AddHandler(PointerPressedEvent, (_, _) => Pane?.ActivateCommand.Execute(null), RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(GotFocusEvent, (_, _) => Pane?.ActivateCommand.Execute(null));

        TabStrip.AddHandler(PointerPressedEvent, OnTabPointerPressed, RoutingStrategies.Tunnel);
        TabStrip.AddHandler(PointerMovedEvent, OnTabPointerMoved, RoutingStrategies.Tunnel);
        TabStrip.AddHandler(PointerReleasedEvent, OnTabPointerReleased, RoutingStrategies.Tunnel);
        TabStrip.AddHandler(PointerWheelChangedEvent, OnTabWheel, RoutingStrategies.Tunnel);
    }

    public void FocusEditor() => Editor.FocusEditor();

    private void OnTabPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (TabFrom(e.Source) is not { } document || IsInsideButton(e.Source)) return;
        var point = e.GetCurrentPoint(TabStrip);
        if (point.Properties.IsMiddleButtonPressed)
        {
            Pane?.CloseCommand.Execute(document);
            e.Handled = true;
        }
        else if (point.Properties.IsLeftButtonPressed)
        {
            dragged = document;
            dragOrigin = point.Position;
            dragging = false;
        }
    }

    private void OnTabPointerMoved(object? sender, PointerEventArgs e)
    {
        if (dragged is null || Pane is not { } pane) return;
        var point = e.GetCurrentPoint(TabStrip);
        if (!point.Properties.IsLeftButtonPressed) { dragged = null; return; }
        if (!dragging)
        {
            if (Math.Abs(point.Position.X - dragOrigin.X) < DragThreshold) return;
            dragging = true;
            // Moving the item recreates its container, so the strip itself must own the pointer.
            e.Pointer.Capture(TabStrip);
        }
        pane.Move(dragged, TargetIndex(point.Position.X));
    }

    private void OnTabPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (dragged is null || e.InitialPressMouseButton != MouseButton.Left) return;
        if (dragging) e.Pointer.Capture(null);
        dragged = null;
        dragging = false;
        // Selecting or dropping a tab hands the keyboard straight back to the code.
        Dispatcher.UIThread.Post(FocusEditor);
    }

    private void OnTabWheel(object? sender, PointerWheelEventArgs e)
    {
        tabScroller ??= TabStrip.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        if (tabScroller is null) return;
        var delta = e.Delta.Y != 0 ? e.Delta.Y : e.Delta.X;
        tabScroller.Offset = tabScroller.Offset.WithX(Math.Max(0, tabScroller.Offset.X - delta * 48));
        e.Handled = true;
    }

    /// <summary>Index the dragged tab should take: one past every other tab whose midpoint lies left of the pointer.</summary>
    private int TargetIndex(double x)
    {
        var index = 0;
        for (var i = 0; i < TabStrip.ItemCount; i++)
        {
            if (TabStrip.ContainerFromIndex(i) is not { } container || container.DataContext == dragged) continue;
            if (container.TranslatePoint(new Point(container.Bounds.Width / 2, 0), TabStrip) is { } middle && middle.X < x)
                index++;
        }
        return index;
    }

    private void OnTabCloseClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is DocumentViewModel document) Pane?.CloseCommand.Execute(document);
        e.Handled = true;
    }

    private void OnTabMenuClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { DataContext: DocumentViewModel document } item || Pane is not { } pane) return;
        var workspace = pane.Workspace;
        switch (item.Tag as string)
        {
            case "close": pane.CloseCommand.Execute(document); break;
            case "others": _ = workspace.CloseOthersAsync(pane, document); break;
            case "all": _ = workspace.CloseAllAsync(pane); break;
            case "split":
                workspace.ActivePane = pane;
                pane.ActiveDocument = document;
                workspace.SplitEditorCommand.Execute(null);
                break;
            case "move": workspace.MoveToOtherPane(pane, document); break;
        }
    }

    private static DocumentViewModel? TabFrom(object? source) => (source as StyledElement)?.DataContext as DocumentViewModel;

    private bool IsInsideButton(object? source)
    {
        for (var visual = source as Visual; visual is not null && visual != TabStrip; visual = visual.GetVisualParent())
            if (visual is Button) return true;
        return false;
    }
}
