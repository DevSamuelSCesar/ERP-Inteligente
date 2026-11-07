using Avalonia;
using Avalonia.Media;
using System;

namespace erp_inteligente;

sealed class Program
{
    /// <summary>Embedded Barlow family (SIL OFL); used everywhere, including popups.</summary>
    internal const string DefaultFontFamily = "avares://erp_inteligente/Assets/Fonts#Barlow";

    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .With(new FontManagerOptions { DefaultFamilyName = DefaultFontFamily })
            .LogToTrace();
}
