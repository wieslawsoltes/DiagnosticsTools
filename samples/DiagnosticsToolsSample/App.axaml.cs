using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace DiagnosticsToolsSample;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow();
        }

        base.OnFrameworkInitializationCompleted();

#if DEBUG
        // Attach DevTools to open with F12 key and wire Roslyn workspace for persistence tests.
        var workspace = DiagnosticsWorkspaceProvider.TryCreateWorkspace();
        var options = new Avalonia.Diagnostics.DevToolsOptions
        {
            RoslynWorkspace = workspace
        };

        this.AttachDevTools(options);
#endif
    }
}
