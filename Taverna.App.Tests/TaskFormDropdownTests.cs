using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Taverna.App.ViewModels;
using Taverna.App.Views;
using Xunit;

namespace Taverna.App.Tests;

/// <summary>Dropdowns render outside the popover's visual tree; using them must not light-dismiss Tasks.</summary>
public sealed class TaskFormDropdownTests
{
    [AvaloniaFact]
    public async Task ChoosingFromDropdownsKeepsTheTaskFormOpen()
    {
        var vm = new MainWindowViewModel(() => Task.FromResult<string?>(null), _ => Task.FromResult(UnsavedChoice.Cancel),
            (_, _, _) => Task.FromResult(false), () => Task.FromResult<string?>(null));
        vm.Chat.HasProject = true;
        vm.Tasks.HasProject = true;
        var view = new ChatWorkspaceView { DataContext = vm };
        var window = new Window { Content = view, Width = 520, Height = 800 };
        window.Show();
        try
        {
            Flush(window);
            Click(window, view.FindControl<Button>("TasksButton")!);
            vm.Tasks.BeginCreateCommand.Execute(null);
            Flush(window);
            var overlay = view.FindControl<Canvas>("Overlay")!;
            var agent = view.FindControl<TasksPanelView>("TasksContent")!.GetVisualDescendants().OfType<ComboBox>().First();

            Click(window, agent);
            Assert.True(agent.IsDropDownOpen);
            var popup = agent.GetVisualDescendants().OfType<Popup>().First();
            var item = ((Visual)popup.Host!).GetVisualDescendants().OfType<ComboBoxItem>().Last();
            Click(window, item);
            Assert.False(agent.IsDropDownOpen);
            Assert.True(overlay.IsVisible);
            Assert.Same(item.DataContext, vm.Tasks.NewAgent);

            // Clicking the combo again to dismiss its open list closes only the list.
            Click(window, agent);
            Click(window, agent);
            Assert.False(agent.IsDropDownOpen);
            Assert.True(overlay.IsVisible);
            Assert.True(vm.Tasks.IsCreating);
        }
        finally { window.Close(); await vm.Tasks.DisposeAsync(); await vm.Chat.DisposeAsync(); }
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
}
