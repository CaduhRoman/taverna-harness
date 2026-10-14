using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Taverna.App.ViewModels;

namespace Taverna.App.Views;

public partial class ChatPanelView : UserControl
{
    private bool followTail = true;

    public ChatPanelView()
    {
        InitializeComponent();
        DraftBox.AddHandler(KeyDownEvent, OnDraftKeyDown, RoutingStrategies.Tunnel);
        CustomModelBox.AddHandler(KeyDownEvent, OnCustomModelKeyDown, RoutingStrategies.Tunnel);
        // Stay pinned to the newest message unless the user scrolled up to read.
        Scroller.ScrollChanged += (_, e) =>
        {
            if (e.ExtentDelta.Y != 0 && followTail) Scroller.ScrollToEnd();
            else if (e.OffsetDelta.Y != 0) followTail = Scroller.Offset.Y >= Scroller.Extent.Height - Scroller.Viewport.Height - 24;
        };
    }

    private ChatViewModel? Chat => DataContext as ChatViewModel;

    /// <summary>Enter sends, Shift+Enter inserts a new line.</summary>
    private void OnDraftKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.KeyModifiers.HasFlag(KeyModifiers.Shift)) return;
        e.Handled = true;
        followTail = true;
        if (Chat?.SendCommand.CanExecute(null) == true) Chat.SendCommand.Execute(null);
    }

    private void OnCustomModelKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        Chat?.ApplyCustomModelCommand.Execute(null);
    }

    public void FocusDraft() => Dispatcher.UIThread.Post(() => DraftBox.Focus());
}
