using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;

namespace erp_inteligente;

/// <summary>What <see cref="AppController"/> needs from the desktop lifetime: the main window and quitting.</summary>
internal interface IWindowHost
{
    Window? MainWindow { get; set; }

    void Shutdown();
}

/// <summary>Adapter over Avalonia's classic desktop lifetime.</summary>
internal sealed class DesktopWindowHost(IClassicDesktopStyleApplicationLifetime desktop) : IWindowHost
{
    public Window? MainWindow
    {
        get => desktop.MainWindow;
        set => desktop.MainWindow = value;
    }

    public void Shutdown() => desktop.Shutdown();
}
