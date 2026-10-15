using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace Taverna.App.Views;

public partial class ChatWorkspaceView : UserControl
{
    private Control? anchor;
    private TopLevel? topLevel;
    private bool showingTasks;

    public ChatWorkspaceView()
    {
        InitializeComponent();
        ChatView.PermissionsRequested += button => Toggle(button, tasks: false);
        LayoutUpdated += (_, _) => PlacePopover();
        DataContextChanged += (_, _) => ClosePopover(false);
        AttachedToVisualTree += (_, _) =>
        {
            topLevel = TopLevel.GetTopLevel(this);
            topLevel?.AddHandler(PointerPressedEvent, OnOutsidePointer, RoutingStrategies.Tunnel, handledEventsToo: true);
            topLevel?.AddHandler(KeyDownEvent, OnEscape, RoutingStrategies.Tunnel);
        };
        DetachedFromVisualTree += (_, _) =>
        {
            topLevel?.RemoveHandler(PointerPressedEvent, OnOutsidePointer);
            topLevel?.RemoveHandler(KeyDownEvent, OnEscape);
            topLevel = null;
            ClosePopover(false);
        };
    }

    public void FocusDraft() => ChatView.FocusDraft();

    private void OnTasksClick(object? sender, RoutedEventArgs e) => Toggle(TasksButton, tasks: true);
    private void OnPermissionClick(object? sender, RoutedEventArgs e) => ClosePopover(true);

    private void Toggle(Control button, bool tasks)
    {
        if (Overlay.IsVisible && anchor == button) { ClosePopover(true); return; }
        anchor = button;
        showingTasks = tasks;
        TasksContent.IsVisible = tasks;
        PermissionsContent.IsVisible = !tasks;
        Overlay.IsVisible = true;
        TasksButton.Classes.Set("active", tasks);
        PlacePopover();
    }

    private void ClosePopover(bool restoreFocus)
    {
        if (!Overlay.IsVisible) return;
        Overlay.IsVisible = false;
        TasksButton.Classes.Set("active", false);
        if (restoreFocus) anchor?.Focus();
        anchor = null;
    }

    private void OnOutsidePointer(object? sender, PointerPressedEventArgs e)
    {
        if (!Overlay.IsVisible || e.Source is not Visual source) return;
        if (Inside(Popover, source) || (anchor is not null && Inside(anchor, source))) return;
        // A click that dismisses an open dropdown inside the popover closes only that dropdown.
        if (Popover.GetVisualDescendants().OfType<Popup>().Any(p => p.IsOpen)) return;
        // Do not consume the click: the chat/editor still receives it.
        ClosePopover(false);
    }

    /// <summary>
    /// Dropdowns (ComboBox, menus) render in a popup layer outside the popover's visual tree, so the walk
    /// also follows logical and templated parents back to the control that owns the popup.
    /// </summary>
    private static bool Inside(Visual parent, Visual child)
    {
        for (StyledElement? current = child; current is not null;
             current = current.Parent ?? current.TemplatedParent as StyledElement ?? (current as Visual)?.GetVisualParent())
            if (current == parent) return true;
        return false;
    }

    private void OnEscape(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || !Overlay.IsVisible) return;
        ClosePopover(true);
        e.Handled = true;
    }

    private void PlacePopover()
    {
        if (!Overlay.IsVisible || anchor is null) return;
        if (!IsEffectivelyVisible) { ClosePopover(false); return; }
        var point = anchor.TranslatePoint(default, PanelBounds);
        if (point is null) return;
        const double gap = 4;
        var width = Math.Max(0, PanelBounds.Bounds.Width - 2 * gap);
        var height = Math.Max(0, PanelBounds.Bounds.Height - 2 * gap);
        Popover.Width = Math.Min(showingTasks ? 520 : 330, width);
        // Prefer below Tasks and above permissions, always clamped to the visible chat bounds.
        var available = showingTasks ? height - point.Value.Y - anchor.Bounds.Height : point.Value.Y - gap;
        Popover.MaxHeight = Math.Min(height, Math.Max(0, available));
        var x = Math.Clamp(point.Value.X + anchor.Bounds.Width - Popover.Width, gap, Math.Max(gap, width + gap - Popover.Width));
        var y = showingTasks ? point.Value.Y + anchor.Bounds.Height + gap : point.Value.Y - Popover.Bounds.Height - gap;
        Canvas.SetLeft(Popover, x);
        Canvas.SetTop(Popover, Math.Clamp(y, gap, Math.Max(gap, height + gap - Popover.Bounds.Height)));
    }
}
