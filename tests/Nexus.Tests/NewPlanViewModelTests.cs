using erp_inteligente.Models;
using erp_inteligente.ViewModels;

namespace Nexus.Tests;

/// <summary>"Novo plano" over the real engine (TestApp): form, preview, submit and permissions.</summary>
public class NewPlanViewModelTests
{
    private static async Task<NewPlanViewModel> PageFor(TestApp app, string username)
    {
        var session = await app.LoginAsync(username);
        return new NewPlanViewModel(session,
            new erp_inteligente.Services.Sqlite.EnginePlanService(app.Host.Engine, app.Host.ApplicationDb, app.Host.Session),
            new FakeDialogService(),
            new erp_inteligente.Services.Sqlite.SqliteQueryService(app.Host.ApplicationDb, app.Host.Session));
    }

    private static void Add(NewPlanViewModel page, string op, params (string Key, string Value)[] values)
    {
        page.SelectedOperation = page.Operations.Single(o => o.Op == op);
        foreach (var (key, value) in values)
            page.Fields.Single(f => f.Key == key).Value = value;
        page.AddStepCommand.Execute(null);
    }

    [Fact]
    public async Task Operator_only_sees_the_operations_of_the_role()
    {
        using var app = new TestApp();
        var page = await PageFor(app, "op.joao");

        var ops = page.Operations.Select(o => o.Op).ToList();
        Assert.Contains("receive_goods", ops);
        Assert.Contains("move_stock", ops);
        Assert.DoesNotContain("adjust_stock", ops);
        Assert.DoesNotContain("create_location", ops);
        Assert.DoesNotContain("create_zone", ops);
        Assert.DoesNotContain("create_product", ops);
    }

    [Fact]
    public void Every_catalog_operation_has_a_generated_form()
    {
        Assert.Equal(erp_inteligente.Core.Engine.OperationCatalog.V1.Select(o => o.Name), NewPlanViewModel.AllOperations.Select(o => o.Op));
        Assert.All(NewPlanViewModel.AllOperations, o => Assert.NotEmpty(o.Fields));
        var range = NewPlanViewModel.AllOperations.Single(o => o.Op == "create_location_range");
        Assert.Equal(["zone", "aisle", "level_from", "level_to", "capacity"], range.Fields.Select(f => f.Key));
        Assert.Equal(PlanLookup.Zone, range.Fields[0].Lookup);
    }

    [Fact]
    public async Task Pick_lists_come_from_the_database()
    {
        using var app = new TestApp();
        var page = await PageFor(app, "sup.maria");
        await page.OnActivatedAsync();

        page.SelectedOperation = page.Operations.Single(o => o.Op == "move_stock");

        var sku = page.Fields.Single(f => f.Key == "sku");
        Assert.True(sku.HasSuggestions);
        Assert.Contains("ABC-123", sku.Suggestions);
        Assert.Contains("DOCA-01", page.Fields.Single(f => f.Key == "from").Suggestions);
    }

    [Fact]
    public async Task Choosing_a_zone_fills_the_next_free_code_and_the_usual_capacity()
    {
        using var app = new TestApp();
        var page = await PageFor(app, "sup.maria");
        await page.OnActivatedAsync();
        page.SelectedOperation = page.Operations.Single(o => o.Op == "create_location");

        page.Fields.Single(f => f.Key == "zone").Value = "a";

        var code = page.Fields.Single(f => f.Key == "code").Value;
        Assert.Matches("^A-[0-9]{2}-[0-9]{2}$", code);
        Assert.NotEqual("", page.Fields.Single(f => f.Key == "capacity").Value);
        page.AddStepCommand.Execute(null);
        await page.PreviewCommand.ExecuteAsync(null);
        Assert.True(page.IsPreviewValid);
    }

    [Fact]
    public async Task Range_form_proposes_a_free_aisle_and_previews_valid()
    {
        using var app = new TestApp();
        var page = await PageFor(app, "sup.maria");
        await page.OnActivatedAsync();
        page.SelectedOperation = page.Operations.Single(o => o.Op == "create_location_range");

        page.Fields.Single(f => f.Key == "zone").Value = "B";
        page.AddStepCommand.Execute(null);
        await page.PreviewCommand.ExecuteAsync(null);

        Assert.True(page.IsPreviewValid, string.Join(" ", page.Issues.Select(i => i.Message)) + page.FormError);
    }

    [Fact]
    public async Task Missing_required_field_keeps_the_step_out_with_a_message()
    {
        using var app = new TestApp();
        var page = await PageFor(app, "sup.maria");

        Add(page, "move_stock", ("sku", "ABC-123"), ("qty", "10"), ("from", "DOCA-01"));

        Assert.Empty(page.Steps);
        Assert.Equal("Preencha destino.", page.FormError);
    }

    [Fact]
    public async Task Non_numeric_quantity_is_refused_in_the_form()
    {
        using var app = new TestApp();
        var page = await PageFor(app, "sup.maria");

        Add(page, "move_stock", ("sku", "ABC-123"), ("qty", "dez"), ("from", "DOCA-01"), ("to", "A-02-01"));

        Assert.Empty(page.Steps);
        Assert.Equal("Quantidade: informe um número.", page.FormError);
    }

    [Fact]
    public async Task Invalid_plan_shows_issues_and_cannot_be_sent()
    {
        using var app = new TestApp();
        var page = await PageFor(app, "sup.maria");
        Add(page, "move_stock", ("sku", "ABC-123"), ("qty", "41"), ("from", "DOCA-01"), ("to", "A-02-01"));

        await page.PreviewCommand.ExecuteAsync(null);

        var issue = Assert.Single(page.Issues);
        Assert.Equal(1, issue.StepNumber);
        Assert.False(page.IsPreviewValid);
        Assert.False(page.SubmitCommand.CanExecute(null));
    }

    [Fact]
    public async Task Changing_steps_after_preview_requires_a_new_preview()
    {
        using var app = new TestApp();
        var page = await PageFor(app, "sup.maria");
        Add(page, "move_stock", ("sku", "ABC-123"), ("qty", "10"), ("from", "DOCA-01"), ("to", "A-02-01"));
        await page.PreviewCommand.ExecuteAsync(null);
        Assert.True(page.SubmitCommand.CanExecute(null));

        page.RemoveStepCommand.Execute(page.Steps[0]);

        Assert.False(page.IsPreviewValid);
        Assert.False(page.SubmitCommand.CanExecute(null));
        Assert.Null(page.PreviewEffect);
    }

    [Fact]
    public async Task Critical_adjustment_previews_as_critical_and_waits_for_approval()
    {
        using var app = new TestApp();
        var page = await PageFor(app, "sup.maria");
        Add(page, "adjust_stock", ("sku", "ALI-300"), ("qty", "-1"), ("location", "C-01-01"), ("lot", "L300-2611A"), ("reason", "Avaria na embalagem"));

        await page.PreviewCommand.ExecuteAsync(null);
        Assert.True(page.PreviewIsCritical);

        await page.SubmitCommand.ExecuteAsync(null);
        var board = Assert.IsType<PlanBoardViewModel>(page.Result);
        Assert.True(board.IsAwaitingApprover); // the requester cannot approve her own plan
        Assert.False(board.CanDecide);
    }

    [Fact]
    public async Task Create_location_then_move_into_it_in_one_plan()
    {
        using var app = new TestApp();
        var page = await PageFor(app, "sup.maria");
        Add(page, "create_location", ("code", "a-09-01"), ("zone", "a"), ("capacity", "60"));
        Add(page, "move_stock", ("sku", "ABC-123"), ("qty", "5"), ("from", "DOCA-01"), ("to", "A-09-01"));

        await page.PreviewCommand.ExecuteAsync(null);
        Assert.True(page.IsPreviewValid);
        Assert.Contains(page.PreviewEffect!.Created, c => c.Code == "A-09-01");

        await page.SubmitCommand.ExecuteAsync(null);
        Assert.True(Assert.IsType<PlanBoardViewModel>(page.Result).IsExecuted);
    }
}
