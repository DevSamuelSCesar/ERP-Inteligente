using System;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using erp_inteligente.Services.Demo;
using erp_inteligente.ViewModels;
using erp_inteligente.Views;

namespace erp_inteligente;

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
            var window = new MainWindow();
            window.DataContext = CreateDemoShell(new DialogService(window));
            desktop.MainWindow = window;
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Composition root of the UI-only build: in-memory fictitious data and a keyword-based Nexus
    /// replace the SQLite layer and the LLM Planner until those features exist (ROADMAP.md).
    /// </summary>
    internal static ShellViewModel CreateDemoShell(Services.IDialogService dialogs, TimeSpan? nexusStepDelay = null)
    {
        var time = TimeProvider.System;
        var data = new DemoData(time);
        return new ShellViewModel(
            new DemoSessionService(DemoSessionService.Supervisor),
            new DemoQueryService(data),
            new DemoPlanService(data),
            new DemoNexusService(data, nexusStepDelay ?? TimeSpan.FromMilliseconds(350)),
            dialogs,
            time);
    }
}
