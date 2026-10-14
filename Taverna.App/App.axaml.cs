using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Taverna.App.Views;

namespace Taverna.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // `Taverna.App <folder>` opens that folder directly.
            var folder = desktop.Args?.FirstOrDefault(Directory.Exists);
            desktop.MainWindow = new MainWindow(folder);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
