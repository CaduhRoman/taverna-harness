using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
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

    /// <summary>Enter sends, Shift+Enter inserts a new line, Ctrl+V also accepts images.</summary>
    private void OnDraftKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.V && e.KeyModifiers == KeyModifiers.Control)
        {
            e.Handled = true;
            _ = PasteAsync();
            return;
        }
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

    // Raw image formats as published by common sources: "PNG" on Windows (browsers, Snipping Tool), MIME types on Linux.
    private static readonly (string Format, string Extension)[] ImageFormats =
        [("PNG", ".png"), ("image/png", ".png"), ("image/jpeg", ".jpg")];

    private async Task PasteAsync()
    {
        try
        {
            if (Chat is { } chat && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard && await ReadImageAsync(clipboard) is { } image)
            {
                await chat.PasteImageAsync(image.Data, image.Extension);
                return;
            }
        }
        catch (Exception) { /* Unreadable clipboard data: fall back to a plain text paste. */ }
        DraftBox.Paste();
    }

    private static async Task<(byte[] Data, string Extension)?> ReadImageAsync(Avalonia.Input.Platform.IClipboard clipboard)
    {
        foreach (var (format, extension) in ImageFormats)
        {
            var bytes = await clipboard.TryGetValueAsync(DataFormat.CreateBytesPlatformFormat(format));
            if (bytes is { Length: > 0 }) return (bytes, extension);
        }
        // An image file copied in the file manager.
        if (await clipboard.TryGetFilesAsync() is { } files)
            foreach (var file in files)
                if (file.TryGetLocalPath() is { } path && Taverna.Core.Chat.AttachmentStore.ImageExtensions.Contains(Path.GetExtension(path)))
                    return (await File.ReadAllBytesAsync(path), Path.GetExtension(path));
        // Print Screen and many Windows apps publish only a device-independent bitmap.
        if (OperatingSystem.IsWindows() && Services.WindowsClipboardImage.TryReadPng() is { } png) return (png, ".png");
        return null;
    }
}
