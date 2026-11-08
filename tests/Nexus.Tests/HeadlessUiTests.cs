using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using erp_inteligente;
using erp_inteligente.Core.Llm;
using erp_inteligente.ViewModels;
using erp_inteligente.Views;

using Nexus.Tests.Fakes;

namespace Nexus.Tests;

/// <summary>Loads the real XAML on the headless platform: styles, resources, bindings and fonts.</summary>
public class HeadlessUiTests
{
    [AvaloniaFact]
    public async Task Every_menu_page_resolves_to_its_view()
    {
        using var app = new TestApp();
        var shell = await app.ShellForAsync("sup.maria");
        var window = new MainWindow { DataContext = shell };
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
    public async Task Nexus_answers_stock_question_through_the_planner_tools()
    {
        var llm = new ScriptedLlmProvider()
            .Call("get_stock", "{\"sku\":\"ABC-123\"}")
            .Then(r => new LlmResponse(
                r.Messages[^1].Content!.Contains("DOCA-01") ? "ABC-123: 40 cx em DOCA-01." : "sem dados",
                [], null, LlmFinishReason.Stop, new LlmUsage(10, 5)));
        using var app = new TestApp(llm: llm);
        var shell = await app.ShellForAsync("op.joao");
        var window = new MainWindow { DataContext = shell };
        window.Show();
        var nexus = Assert.IsType<NexusViewModel>(shell.CurrentPage);

        nexus.Draft = "qual o saldo do ABC-123?";
        await nexus.SendCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Contains(window.GetVisualDescendants().OfType<SelectableTextBlock>(), t => t.Text == "ABC-123: 40 cx em DOCA-01.");
        Assert.DoesNotContain(window.GetVisualDescendants(), v => v is PlanBoardView or DraftPlanView);
        window.Close();
    }

    /// <summary>The Planner proposes a move; nothing is written until op.joao confirms, then the plan runs as planned by the AI.</summary>
    [AvaloniaFact]
    public async Task Nexus_proposal_is_written_only_after_confirmation()
    {
        const string plan =
            "{\"plan\":{\"dsl_version\":\"1.0\",\"intent\":\"putaway\",\"skill\":\"armazenagem\",\"steps\":[" +
            "{\"op\":\"move_stock\",\"args\":{\"sku\":\"ABC-123\",\"qty\":40,\"from\":\"DOCA-01\",\"to\":\"A-02-01\"}}]}}";
        var llm = new ScriptedLlmProvider().Call("submit_plan", plan);
        using var app = new TestApp(llm: llm);
        var shell = await app.ShellForAsync("op.joao");
        var window = new MainWindow { DataContext = shell };
        window.Show();
        var nexus = Assert.IsType<NexusViewModel>(shell.CurrentPage);
        long Plans() => Scalar(app, "SELECT COUNT(*) FROM plans");
        var before = Plans();
        long AiExecuted() => Scalar(app, "SELECT COUNT(*) FROM plans WHERE planned_by = 'ai' AND status = 'executed'");
        var aiBefore = AiExecuted();

        nexus.Draft = "guarde as 40 caixas do ABC-123 da doca em A-02-01";
        await nexus.SendCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        var card = Assert.Single(window.GetVisualDescendants().OfType<DraftPlanView>());
        var proposal = Assert.IsType<DraftPlanViewModel>(card.DataContext);
        Assert.Equal(before, Plans());
        Assert.False(nexus.IsInputEnabled);
        Assert.Contains(card.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Efeito simulado");

        await proposal.ConfirmCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        var board = Assert.IsType<PlanBoardViewModel>(Assert.Single(window.GetVisualDescendants().OfType<PlanBoardView>()).DataContext);
        Assert.True(board.IsExecuted);
        Assert.Equal(before + 1, Plans());
        Assert.Equal(aiBefore + 1, AiExecuted());
        Assert.True(nexus.IsInputEnabled);
        window.Close();
    }

    private static long Scalar(TestApp app, string sql)
    {
        using var connection = app.Host.Database.OpenApplication();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)command.ExecuteScalar()!;
    }

    /// <summary>
    /// Seed plan 2 (an adjustment requested on behalf of op.joao) can be decided by sup.maria, but the
    /// requester's rights decide what runs: op.joao has no stock.adjust, so approving ends in a failed plan.
    /// </summary>
    [AvaloniaFact]
    public async Task Approving_seed_plan_runs_with_the_requester_rights()
    {
        using var app = new TestApp();
        var shell = await app.ShellForAsync("sup.maria");
        var window = new MainWindow { DataContext = shell };
        window.Show();

        await shell.NavigateCommand.ExecuteAsync(shell.MenuItems.Single(i => i.Label == "Planos"));
        Dispatcher.UIThread.RunJobs();

        var board = Assert.Single(window.GetVisualDescendants().OfType<PlanBoardView>());
        var viewModel = Assert.IsType<PlanBoardViewModel>(board.DataContext);
        Assert.Equal(2, viewModel.PlanId);
        Assert.True(viewModel.CanDecide);

        await viewModel.AcceptCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.True(viewModel.IsFailed);
        Assert.Contains("não permite", viewModel.StatusText);
        Assert.Equal(0, shell.MenuItems.Single(i => i.Label == "Planos").Badge);
        window.Close();
    }

    [AvaloniaFact]
    public async Task New_plan_screen_previews_and_submits_a_move()
    {
        using var app = new TestApp();
        var shell = await app.ShellForAsync("sup.maria");
        var window = new MainWindow { DataContext = shell };
        window.Show();
        await shell.NavigateCommand.ExecuteAsync(shell.MenuItems.Single(i => i.Label == "Novo plano"));
        var page = Assert.IsType<NewPlanViewModel>(shell.CurrentPage);

        page.SelectedOperation = page.Operations.Single(o => o.Op == "move_stock");
        Fill(page, ("sku", "abc-123"), ("qty", "40"), ("from", "DOCA-01"), ("to", "A-02-01"));
        page.AddStepCommand.Execute(null);
        Assert.False(page.SubmitCommand.CanExecute(null)); // only a previewed plan can be sent

        await page.PreviewCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        Assert.True(page.IsPreviewValid);
        Assert.Contains(window.GetVisualDescendants(), v => v is SummaryView);

        await page.SubmitCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        var result = Assert.IsType<PlanBoardViewModel>(page.Result);
        Assert.True(result.IsExecuted);
        Assert.Empty(page.Steps);
        Assert.Contains(window.GetVisualDescendants(), v => v is PlanBoardView);
        window.Close();
    }

    private static void Fill(NewPlanViewModel page, params (string Key, string Value)[] values)
    {
        foreach (var (key, value) in values)
            page.Fields.Single(f => f.Key == key).Value = value;
    }

    /// <summary>
    /// The Send button has no fill while the request is empty and turns brand red when there is text;
    /// Fluent's light overlay on the content presenter must never show.
    /// </summary>
    [AvaloniaFact]
    public async Task Send_button_is_neutral_when_empty_and_red_with_text()
    {
        using var app = new TestApp();
        var shell = await app.ShellForAsync("sup.maria");
        var window = new MainWindow { DataContext = shell };
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
