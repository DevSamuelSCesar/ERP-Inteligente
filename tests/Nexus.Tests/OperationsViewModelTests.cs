using erp_inteligente.Core.Llm;
using erp_inteligente.Core.Operations;
using erp_inteligente.Models;
using erp_inteligente.ViewModels;

namespace Nexus.Tests;

/// <summary>Feature 9 screens over the real AppHost: audit panel, AI costs, backups in Configurações, memory maintenance.</summary>
public sealed class OperationsViewModelTests
{
    private static async Task<T> PageAsync<T>(TestApp app, string username, string iconKey) where T : PageViewModel
    {
        var shell = await app.ShellForAsync(username);
        var item = shell.MenuItems.Concat(shell.FooterItems).Single(i => i.IconKey == iconKey);
        await shell.NavigateCommand.ExecuteAsync(item);
        return Assert.IsType<T>(shell.CurrentPage);
    }

    [Fact]
    public async Task Audit_panel_verifies_the_chain_and_filters_by_category_and_problems()
    {
        using var app = new TestApp();
        await app.LoginAsync("op.joao"); // a login row (Segurança)
        var page = await PageAsync<AuditViewModel>(app, "sup.maria", "Audit");

        Assert.True(page.ChainIsValid);
        Assert.Contains("íntegra", page.ChainStatus);
        Assert.Contains(page.Rows, r => r.Category == "Segurança");

        page.SelectedCategory = "Planos";
        Assert.All(page.Rows, r => Assert.Equal("Planos", r.Category));
        page.SelectedCategory = AuditViewModel.AllCategories;
        page.OnlyProblems = true;
        Assert.All(page.Rows, r => Assert.True(r.IsDenied || r.IsFailed));
        Assert.Contains(page.Totals, t => t.Label == "Instruções suspeitas");
    }

    [Fact]
    public async Task Cost_panel_shows_requests_recipe_share_and_costs()
    {
        using var app = new TestApp();
        var joao = await app.LoginAsync("op.joao");
        app.Host.UsageLog.Record(joao, new LlmUsageEntry(LlmUseRole.Planner, new LlmChoice(LlmProviderKind.Claude, LlmSettings.ClaudeStrongModel), 3, 12_000, 2_000, "proposals"));
        app.Host.UsageLog.Record(joao, new LlmUsageEntry(LlmUseRole.Matcher, new LlmChoice(LlmProviderKind.Claude, LlmSettings.ClaudeFastModel), 1, 1_500, 150, "recipe"));

        var page = await PageAsync<CostsViewModel>(app, "sup.maria", "Costs");
        for (var i = 0; i < 50 && page.IsEmpty; i++) await Task.Delay(10, TestContext.Current.CancellationToken);

        Assert.Equal("2", page.Totals.Single(t => t.Label == "Pedidos").Value);
        Assert.Equal("50%", page.Totals.Single(t => t.Label == "Resolvidos por receita").Value.Replace(" ", "").Replace(" ", ""));
        Assert.Contains(page.Totals, t => t.Label.StartsWith("Custo estimado (US$)"));
        Assert.Equal(2, page.Lines.Count);
        Assert.Equal("Planejamento", page.Lines[0].Role);
    }

    [Fact]
    public async Task Operators_see_neither_the_audit_nor_the_costs()
    {
        using var app = new TestApp();
        var shell = await app.ShellForAsync("op.joao");
        Assert.DoesNotContain(shell.MenuItems, i => i.IconKey is "Audit" or "Costs");
    }

    [Fact]
    public async Task Administrator_makes_and_restores_a_backup_from_the_settings()
    {
        using var app = new TestApp();
        var page = await PageAsync<SettingsViewModel>(app, "adm.carlos", "Settings");
        Assert.True(page.HasBackups);
        Assert.True(page.HasNoBackups);

        await page.CreateBackupCommand.ExecuteAsync(null);
        Assert.False(page.BackupFeedbackIsError, page.BackupFeedback);
        var row = Assert.Single(page.Backups);

        page.SelectedBackup = row;
        page.RequestRestoreCommand.Execute(null);
        Assert.True(page.IsConfirmingRestore);
        Assert.Contains("Tudo o que foi feito depois dele some", page.RestoreQuestion);
        page.CancelRestoreCommand.Execute(null);
        Assert.False(page.IsConfirmingRestore);

        page.SelectedBackup = page.Backups[0];
        page.RequestRestoreCommand.Execute(null);
        await page.ConfirmRestoreCommand.ExecuteAsync(null);
        Assert.False(page.BackupFeedbackIsError, page.BackupFeedback);
        Assert.Equal(2, page.Backups.Count); // the restored one and the safety copy
    }

    [Fact]
    public async Task Supervisors_do_not_see_the_backups()
    {
        using var app = new TestApp();
        var page = await PageAsync<SettingsViewModel>(app, "sup.maria", "Settings");
        Assert.False(page.HasBackups);
    }

    [Fact]
    public async Task Supervisor_runs_the_memory_maintenance_from_the_screen()
    {
        using var app = new TestApp();
        var page = await PageAsync<MemoriesViewModel>(app, "sup.maria", "Memory");
        await page.MaintainCommand.ExecuteAsync(null);
        Assert.True(page.FeedbackIsSuccess);
        Assert.StartsWith("Manutenção feita", page.Feedback);

        var joaoPage = await PageAsync<MemoriesViewModel>(app, "op.joao", "Memory");
        Assert.False(joaoPage.MaintainCommand.CanExecute(null));
    }
}
