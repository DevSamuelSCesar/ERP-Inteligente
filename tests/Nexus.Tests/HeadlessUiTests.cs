using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using erp_inteligente;
using erp_inteligente.ViewModels;
using erp_inteligente.Views;

namespace Nexus.Tests;

/// <summary>Loads the real XAML on the headless platform: styles, resources, bindings and fonts.</summary>
public class HeadlessUiTests
{
    [AvaloniaFact]
    public async Task Every_menu_page_resolves_to_its_view()
    {
        var window = new MainWindow();
        var shell = App.CreateDemoShell(new FakeDialogService(), TimeSpan.Zero);
        window.DataContext = shell;
        window.Show();

        foreach (var item in shell.MenuItems.Concat(shell.FooterItems))
        {
            await shell.NavigateCommand.ExecuteAsync(item);
            Dispatcher.UIThread.RunJobs();

            var expected = item.Page.GetType().Name.Replace("ViewModel", "View");
            Assert.Contains(window.GetVisualDescendants(), v => v.GetType().Name == expected);
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text?.StartsWith("Not Found") == true);
        }
        window.Close();
    }

    [AvaloniaFact]
    public async Task Nexus_request_renders_plan_board_and_summary()
    {
        var window = new MainWindow();
        var shell = App.CreateDemoShell(new FakeDialogService(), TimeSpan.Zero);
        window.DataContext = shell;
        window.Show();
        var nexus = Assert.IsType<NexusViewModel>(shell.CurrentPage);

        nexus.Draft = "Guarde as 40 caixas do ABC-123 que estão na doca";
        await nexus.SendCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Contains(window.GetVisualDescendants(), v => v is PlanBoardView);
        Assert.Contains(window.GetVisualDescendants(), v => v is SummaryView);
        window.Close();
    }

    /// <summary>
    /// The Send button has no fill while the request is empty and turns brand red when there is text;
    /// Fluent's light overlay on the content presenter must never show.
    /// </summary>
    [AvaloniaFact]
    public void Send_button_is_neutral_when_empty_and_red_with_text()
    {
        var window = new MainWindow();
        var shell = App.CreateDemoShell(new FakeDialogService(), TimeSpan.Zero);
        window.DataContext = shell;
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var nexus = Assert.IsType<NexusViewModel>(shell.CurrentPage);
        var send = window.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("primary"));
        var presenter = send.GetVisualDescendants().OfType<ContentPresenter>().First(p => p.Name == "PART_ContentPresenter");

        Assert.False(send.IsEffectivelyEnabled);
        Assert.Equal(Colors.Transparent, Assert.IsAssignableFrom<ISolidColorBrush>(send.Background).Color);
        Assert.Equal(Resource("TextDisabledColor"), Assert.IsAssignableFrom<ISolidColorBrush>(send.Foreground).Color);
        Assert.True(presenter.Background is null or ISolidColorBrush { Color.A: 0 }, "No overlay on the content presenter");

        nexus.Draft = "saldo do ABC-123";
        Dispatcher.UIThread.RunJobs();

        Assert.True(send.IsEffectivelyEnabled);
        Assert.Equal(Resource("BrandPrimaryColor"), Assert.IsAssignableFrom<ISolidColorBrush>(send.Background).Color);
        Assert.Equal(Resource("OnBrandColor"), Assert.IsAssignableFrom<ISolidColorBrush>(presenter.Foreground).Color);
        window.Close();

        static Color Resource(string key) =>
            Assert.IsType<Color>(Avalonia.Application.Current!.FindResource(Avalonia.Styling.ThemeVariant.Dark, key));
    }

    /// <summary>Each font resource used by the styles resolves to the intended Barlow file, not a fallback.</summary>
    [AvaloniaTheory]
    [InlineData("TextFont", 400, "Barlow")]
    [InlineData("TextFont", 500, "Barlow Medium")]
    [InlineData("TextFont", 600, "Barlow SemiBold")]
    [InlineData("TitleFont", 600, "Barlow Condensed SemiBold")]
    [InlineData("TabularFont", 400, "Barlow Semi Condensed")]
    [InlineData("TabularStrongFont", 600, "Barlow Semi Condensed SemiBold")]
    public void Font_resources_resolve_to_embedded_barlow(string resourceKey, int weight, string expectedFamily)
    {
        var family = Assert.IsType<FontFamily>(Avalonia.Application.Current!.FindResource(resourceKey));
        var typeface = new Typeface(family, FontStyle.Normal, (FontWeight)weight);

        Assert.True(FontManager.Current.TryGetGlyphTypeface(typeface, out var glyphs));
        Assert.Equal(expectedFamily, glyphs.FamilyName);
    }

    [AvaloniaFact]
    public void Default_font_is_barlow() =>
        Assert.Equal("Barlow", FontManager.Current.DefaultFontFamily.Name);
}
