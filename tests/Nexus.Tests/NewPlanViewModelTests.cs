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
            new FakeDialogService());
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

        Assert.Equal(["receive_goods", "move_stock"], page.Operations.Select(o => o.Op));
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
