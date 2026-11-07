using Avalonia;
using Avalonia.Headless;
using Avalonia.Media;
using erp_inteligente;
using erp_inteligente.Services;
using Nexus.Tests.Fakes;

[assembly: AvaloniaTestApplication(typeof(Nexus.Tests.TestAppBuilder))]

namespace Nexus.Tests;

public static class TestAppBuilder
{
    /// <summary>Headless platform with real Skia text rendering, so embedded fonts are actually loaded.</summary>
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
        .With(new FontManagerOptions { DefaultFamilyName = Program.DefaultFontFamily });
}

/// <summary>Dialog stand-in that answers with a fixed reason (null = cancelled).</summary>
public sealed class FakeDialogService(string? reason = "Endereço errado") : IDialogService
{
    public int Calls { get; private set; }

    public Task<string?> AskRejectionReasonAsync()
    {
        Calls++;
        return Task.FromResult(reason);
    }
}

/// <summary>Fresh fictitious warehouse and services for each test.</summary>
public sealed class DemoWorld
{
    public DemoWorld()
    {
        Data = new DemoData(TimeProvider.System);
        Plans = new DemoPlanService(Data);
        Queries = new DemoQueryService(Data);
        Nexus = new DemoNexusService(Data, TimeSpan.Zero);
    }

    public DemoData Data { get; }
    public DemoPlanService Plans { get; }
    public DemoQueryService Queries { get; }
    public DemoNexusService Nexus { get; }

    public static IProgress<string> NoProgress { get; } = new Progress<string>();
}
