using System;
using System.Diagnostics;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using erp_inteligente.Core.Security;
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
            // The controller decides when to quit, since windows are swapped between login and shell.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try
            {
                var host = AppHost.Open(ResolveDatabasePath(desktop.Args), SecretStores.ForCurrentPlatform());
                desktop.Exit += (_, _) => host.Dispose();
                new AppController(new DesktopWindowHost(desktop), host).Start();
            }
            catch (Exception ex)
            {
                Trace.TraceError("Startup failed: {0}", ex.GetType().Name);
                var window = new FatalErrorWindow(StartupError.Describe(ex));
                window.Closed += (_, _) => desktop.Shutdown();
                desktop.MainWindow = window;
                window.Show();
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// <c>--db &lt;file&gt;</c> selects the database; otherwise the per-user data folder is used
    /// (e.g. ~/.local/share/Nexus/nexus.db on Linux, %LOCALAPPDATA%\Nexus\nexus.db on Windows).
    /// </summary>
    internal static string ResolveDatabasePath(string[]? args)
    {
        if (args is not null)
        {
            var index = Array.IndexOf(args, "--db");
            if (index >= 0 && index + 1 < args.Length && args[index + 1].Length > 0)
                return Path.GetFullPath(args[index + 1]);
        }
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nexus", "nexus.db");
    }
}
