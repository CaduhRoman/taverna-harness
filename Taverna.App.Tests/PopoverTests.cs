using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Taverna.App.ViewModels;
using Taverna.App.Views;
using Taverna.Core.Agents;
using Taverna.Core.Git;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(Taverna.App.Tests.TestAppBuilder))]

namespace Taverna.App.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

public sealed class PopoverTests
{
    [AvaloniaFact]
    public async Task TaskModelPickerUsesSharedCatalogAndModesRespectModelEfforts()
    {
        var runners = Enum.GetValues<AgentKind>().ToDictionary(k => k, k => (IAgentRunner)new CatalogRunner(k));
        await using var vm = new TasksViewModel(new GitService(), runners, _ => { },
            (_, _, _) => Task.FromResult(false), _ => { }, _ => Task.FromResult(true));
        Assert.Equal(vm.NewAgent!.ModelChoices.Select(m => (m.Value, m.Label)), vm.ModelChoices.Select(m => (m.Value, m.Label)));
        Assert.Equal("Model A", vm.SelectedModelChoice!.Label);
        Assert.Equal(["Default", "low", "high"], vm.EffortChoices.Select(c => c.Value));
        vm.SelectEffortChoiceCommand.Execute(vm.EffortChoices.Single(c => c.Value == "high"));
        Assert.Equal("high", vm.NewEffort);
        Assert.True(vm.EffortChoices.Single(c => c.Value == "high").IsSelected);
        Assert.Equal("model-a", vm.NewModel);
        vm.SelectedModelChoice = vm.ModelChoices.Single(m => m.Value == "model-b");
        Assert.Equal(AgentOptionViewModel.DefaultChoice, vm.NewEffort);
        Assert.False(vm.NewAgentSupportsEffort);
        vm.NewAgent = vm.AgentOptions.Single(a => a.Kind == AgentKind.Codex);
        Assert.Equal("model-a", vm.SelectedModelChoice!.Value);
        vm.SelectEffortChoiceCommand.Execute(vm.EffortChoices.Single(c => c.Value == "low"));
        Assert.Equal("low", vm.NewEffort);
        vm.SelectEffortChoiceCommand.Execute(vm.EffortChoices[0]);
        Assert.Null(AgentOptionViewModel.Normalize(vm.NewEffort));
    }

    private sealed class CatalogRunner(AgentKind kind) : IAgentRunner
    {
        public AgentKind Kind => kind;
        public string Name => kind.ToString();
        public AgentCapabilities Capabilities => AgentCapabilities.From(
            [new AgentModel("model-a", "Model A", ["low", "high"]), new AgentModel("model-b", "Model B", [])], [], false);
        public bool IsAvailable() => true;
        public async IAsyncEnumerable<AgentEvent> RunAsync(AgentRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        { await Task.CompletedTask; yield return new AgentEvent(AgentEventKind.Exit, ExitCode: 0); }
    }

    [AvaloniaFact]
    public async Task TasksOverlayKeepsChatAndViewModelAndClosesOnEscapeAndOutsideClick()
    {
        var (window, view, vm) = Create();
        try
        {
            var chat = view.FindControl<ChatPanelView>("ChatView")!;
            var draft = chat.FindControl<TextBox>("DraftBox")!;
            var tasks = view.FindControl<TasksPanelView>("TasksContent")!;
            var originalBounds = chat.Bounds;
            Click(window, view.FindControl<Button>("TasksButton")!);
            Assert.True(view.FindControl<Canvas>("Overlay")!.IsVisible);
            Assert.True(chat.IsEffectivelyVisible);
            Assert.Same(vm.Tasks, tasks.DataContext);
            Assert.Equal(originalBounds, chat.Bounds);
            window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            Flush(window);
            Assert.False(view.FindControl<Canvas>("Overlay")!.IsVisible);

            Click(window, view.FindControl<Button>("TasksButton")!);
            Click(window, draft);
            Assert.False(view.FindControl<Canvas>("Overlay")!.IsVisible);
            window.KeyTextInput("chat remains usable");
            Assert.Equal("chat remains usable", draft.Text);
            Assert.Same(vm.Tasks, tasks.DataContext);
            Assert.Equal(originalBounds, chat.Bounds);
        }
        finally { window.Close(); await vm.Tasks.DisposeAsync(); await vm.Chat.DisposeAsync(); }
    }

    [AvaloniaTheory]
    [InlineData(260, 360)]
    [InlineData(340, 550)]
    [InlineData(720, 800)]
    public async Task ExpandedFormAndPermissionsStayWithinPanelWhenResized(double width, double height)
    {
        var (window, view, vm) = Create();
        try
        {
            vm.Tasks.HasProject = true;
            Click(window, view.FindControl<Button>("TasksButton")!);
            vm.Tasks.BeginCreateCommand.Execute(null);
            window.Width = width;
            window.Height = height;
            Flush(window);
            AssertInside(view);
            var scroll = view.FindControl<Border>("Popover")!.GetVisualDescendants().OfType<ScrollViewer>().First();
            Assert.True(scroll.Viewport.Height <= height);
            Assert.True(vm.Tasks.IsCreating);
            SaveFrame(window, $"tasks-{width}.png");
            // The form sizes to its content; it only scrolls when the panel is too short.
            if (height <= 400) Assert.True(scroll.Extent.Height > scroll.Viewport.Height);
            scroll.Offset = new Vector(0, scroll.Extent.Height);
            Flush(window);
            SaveFrame(window, $"tasks-prompt-{width}.png");
            window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            Flush(window);
            var chat = view.FindControl<ChatPanelView>("ChatView")!;
            Click(window, chat.FindControl<Button>("PermissionsButton")!);
            AssertInside(view);
            window.Width = 280;
            window.Height = 400;
            Flush(window);
            AssertInside(view);
            Assert.False(view.FindControl<TasksPanelView>("TasksContent")!.IsVisible);
            Assert.True(view.FindControl<ItemsControl>("PermissionsContent")!.IsVisible);
            SaveFrame(window, $"permissions-{width}.png");
            window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            Flush(window);
            Assert.False(view.FindControl<Canvas>("Overlay")!.IsVisible);
        }
        finally { window.Close(); await vm.Tasks.DisposeAsync(); await vm.Chat.DisposeAsync(); }
    }

    [AvaloniaFact]
    public async Task PermissionPreferenceSurvivesAgentSwitchAndNewViewModelWithoutChangingModelEffort()
    {
        var directory = Directory.CreateTempSubdirectory("taverna-ui-permission-").FullName;
        var runners = Enum.GetValues<AgentKind>().ToDictionary(k => k, k => (IAgentRunner)new AvailableRunner(k));
        ChatViewModel CreateChat() => new(new GitService(), runners, message => throw new Exception(message),
            () => Task.FromResult<string?>(null), new PermissionPreferences(Path.Combine(directory, "permissions.json")));
        try
        {
            await using var vm = CreateChat();
            Assert.Equal(AgentPermissionMode.Auto, vm.SelectedPermission);
            vm.SelectedModel = "opus";
            vm.SelectedEffort = "high";
            await vm.SelectPermissionCommand.ExecuteAsync(vm.Permissions.Single(p => p.Mode == AgentPermissionMode.Auto));
            vm.SelectAgentCommand.Execute(vm.Agents.Single(a => a.Option.Kind == AgentKind.ExternalApi));
            Assert.Equal(AgentPermissionMode.Plan, vm.SelectedPermission);
            Assert.False(vm.Permissions.Single(p => p.Mode == AgentPermissionMode.Auto).IsSupported);
            vm.SelectAgentCommand.Execute(vm.Agents.Single(a => a.Option.Kind == AgentKind.Claude));
            Assert.Equal(AgentPermissionMode.Auto, vm.SelectedPermission);
            Assert.Equal("opus", vm.SelectedModel);
            Assert.Equal("high", vm.SelectedEffort);
            await using var fresh = CreateChat();
            Assert.Equal(AgentPermissionMode.Auto, fresh.SelectedPermission);
            await fresh.SelectPermissionCommand.ExecuteAsync(fresh.Permissions.Single(p => p.Mode == AgentPermissionMode.Manual));
            Assert.Equal(AgentPermissionMode.Auto, fresh.SelectedPermission);
        }
        finally { Directory.Delete(directory, true); }
    }

    private static (Window, ChatWorkspaceView, MainWindowViewModel) Create()
    {
        var vm = new MainWindowViewModel(() => Task.FromResult<string?>(null), _ => Task.FromResult(UnsavedChoice.Cancel),
            (_, _, _) => Task.FromResult(false), () => Task.FromResult<string?>(null));
        vm.Chat.HasProject = true;
        var view = new ChatWorkspaceView { DataContext = vm };
        var window = new Window { Content = view, Width = 340, Height = 550 };
        window.Show();
        Flush(window);
        return (window, view, vm);
    }

    [AvaloniaFact]
    public async Task ChoosingPermissionClosesPopoverUpdatesIndicatorAndPersists()
    {
        var directory = Directory.CreateTempSubdirectory("taverna-ui-choice-").FullName;
        var (window, view, vm) = Create();
        var runners = Enum.GetValues<AgentKind>().ToDictionary(k => k, k => (IAgentRunner)new AvailableRunner(k));
        var store = new PermissionPreferences(Path.Combine(directory, "permissions.json"));
        await using var chat = new ChatViewModel(new GitService(), runners, message => throw new Exception(message),
            () => Task.FromResult<string?>(null), store) { HasProject = true };
        try
        {
            var chatView = view.FindControl<ChatPanelView>("ChatView")!;
            chatView.DataContext = chat;
            var choices = view.FindControl<ItemsControl>("PermissionsContent")!;
            choices.DataContext = chat;
            Flush(window);
            Click(window, view.FindControl<Button>("TasksButton")!);
            // Clicking the other anchor closes Tasks and opens permissions with one click.
            Click(window, chatView.FindControl<Button>("PermissionsButton")!);
            Assert.False(view.FindControl<TasksPanelView>("TasksContent")!.IsVisible);
            var auto = choices.GetVisualDescendants().OfType<Button>()
                .Single(b => b.DataContext is PermissionChoiceViewModel { Mode: AgentPermissionMode.Auto });
            Click(window, auto);
            if (chat.SelectPermissionCommand.ExecutionTask is { } pending) await pending;
            Assert.False(view.FindControl<Canvas>("Overlay")!.IsVisible);
            Assert.Equal("Auto", chat.PermissionLabel);
            Assert.Equal(AgentPermissionMode.Auto, store.Load()[AgentKind.Claude]);
            Click(window, chatView.FindControl<Button>("PermissionsButton")!);
            Assert.True(chat.Permissions.Single(p => p.Mode == AgentPermissionMode.Auto).IsSelected);
            // The menu can cover the draft; use an exposed point above it for light dismiss.
            window.MouseDown(new Point(15, 60), MouseButton.Left);
            window.MouseUp(new Point(15, 60), MouseButton.Left);
            Flush(window);
            Assert.False(view.FindControl<Canvas>("Overlay")!.IsVisible);
            Click(window, chatView.FindControl<TextBox>("DraftBox")!);
            Assert.False(view.FindControl<Canvas>("Overlay")!.IsVisible);
            window.KeyTextInput("still usable");
            Assert.Equal("still usable", chat.Draft);
        }
        finally { window.Close(); await vm.Tasks.DisposeAsync(); await vm.Chat.DisposeAsync(); Directory.Delete(directory, true); }
    }

    private static void Click(Window window, Control control)
    {
        var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Flush(window);
    }

    private static void Flush(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    private static void SaveFrame(Window window, string name)
    {
        if (Environment.GetEnvironmentVariable("TAVERNA_TEST_SCREENSHOTS") is not { Length: > 0 } output) return;
        Directory.CreateDirectory(output);
        using var frame = window.CaptureRenderedFrame();
        frame?.Save(Path.Combine(output, name));
    }

    private static void AssertInside(ChatWorkspaceView view)
    {
        var popover = view.FindControl<Border>("Popover")!;
        Assert.True(view.FindControl<Canvas>("Overlay")!.IsVisible);
        var position = popover.TranslatePoint(default, view)!.Value;
        Assert.True(popover.Bounds.Width > 0 && popover.Bounds.Height > 0);
        Assert.InRange(position.X, 0, view.Bounds.Width);
        Assert.InRange(position.Y, 0, view.Bounds.Height);
        Assert.True(position.X + popover.Bounds.Width <= view.Bounds.Width + 0.1);
        Assert.True(position.Y + popover.Bounds.Height <= view.Bounds.Height + 0.1);
    }

    private sealed class AvailableRunner(AgentKind kind) : IAgentRunner
    {
        public AgentKind Kind => kind;
        public string Name => kind.ToString();
        public AgentCapabilities Capabilities => new(["opus"], ["high"], false);
        public bool IsAvailable() => true;
        public async IAsyncEnumerable<AgentEvent> RunAsync(AgentRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        { await Task.CompletedTask; yield return new AgentEvent(AgentEventKind.Exit, ExitCode: 0); }
    }
}

