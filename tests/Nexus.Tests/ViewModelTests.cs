using erp_inteligente.Core.Audit;
using erp_inteligente.Models;
using Nexus.Tests.Fakes;
using erp_inteligente.ViewModels;

namespace Nexus.Tests;

public class ViewModelTests
{
    private static ShellViewModel Shell(DemoWorld w, UserSession user, FakeDialogService? dialogs = null) =>
        new(new DemoSessionService(user), w.Queries, w.Plans, w.Nexus, dialogs ?? new FakeDialogService(), TimeProvider.System,
            new AuditVerification(true, 0, null, null), "banco de teste");

    private static NexusViewModel Nexus(DemoWorld w, UserSession user) =>
        new(user, w.Nexus, w.Plans, new FakeDialogService(), TimeProvider.System);

    [Fact]
    public void Menu_hides_items_without_permission()
    {
        var shell = Shell(new DemoWorld(), DemoSessionService.Operator);

        var labels = shell.MenuItems.Select(i => i.Label).ToList();
        Assert.Equal(["Nexus", "Novo plano", "Estoque", "Endereços", "Pedidos", "Planos", "Receitas"], labels);
    }

    [Fact]
    public void Supervisor_sees_audit_and_pending_counter()
    {
        var shell = Shell(new DemoWorld(), DemoSessionService.Supervisor);

        Assert.Contains(shell.MenuItems, i => i.Label == "Auditoria");
        Assert.Equal(1, shell.MenuItems.Single(i => i.Label == "Planos").Badge);
    }

    [Fact]
    public void Operator_never_sees_pending_counter()
    {
        var shell = Shell(new DemoWorld(), DemoSessionService.Operator);

        Assert.False(shell.MenuItems.Single(i => i.Label == "Planos").HasBadge);
    }

    [Fact]
    public async Task Navigation_keeps_the_nexus_page_and_marks_one_active_item()
    {
        var shell = Shell(new DemoWorld(), DemoSessionService.Supervisor);
        var nexus = shell.CurrentPage;
        Assert.IsType<NexusViewModel>(nexus);

        await shell.NavigateCommand.ExecuteAsync(shell.MenuItems.Single(i => i.Label == "Estoque"));
        Assert.IsType<StockViewModel>(shell.CurrentPage);
        await shell.NavigateCommand.ExecuteAsync(shell.MenuItems[0]);

        Assert.Same(nexus, shell.CurrentPage);
        Assert.Single(shell.MenuItems.Concat(shell.FooterItems), i => i.IsActive);
    }

    [Fact]
    public async Task Sending_a_putaway_request_shows_executed_plan_with_summary()
    {
        var w = new DemoWorld();
        var vm = Nexus(w, DemoSessionService.Supervisor);
        vm.Draft = "Guarde as 40 caixas do ABC-123 que estão na doca";

        await vm.SendCommand.ExecuteAsync(null);

        Assert.Equal("", vm.Draft);
        Assert.False(vm.IsBusy);
        var board = Assert.IsType<PlanBoardViewModel>(vm.Entries.Last().Body);
        Assert.True(board.IsExecuted);
        Assert.Equal(2, board.Summary!.Flows.Count);
        Assert.Contains(board.Summary.Totals, t => t.Value == "40 cx");
    }

    [Fact]
    public async Task Own_critical_plan_waits_for_another_supervisor()
    {
        var w = new DemoWorld();
        var vm = Nexus(w, DemoSessionService.Supervisor);
        vm.Draft = "ajuste -1 cx do ALI-300 em C-01-01 por avaria";

        await vm.SendCommand.ExecuteAsync(null);

        var board = Assert.IsType<PlanBoardViewModel>(vm.Entries.Last().Body);
        Assert.False(board.CanDecide);
        Assert.True(board.IsAwaitingApprover);
        Assert.False(board.AcceptCommand.CanExecute(null));
        Assert.Contains("Quem pediu não pode aprovar", board.StatusText);
    }

    [Fact]
    public void Empty_request_cannot_be_sent()
    {
        var vm = Nexus(new DemoWorld(), DemoSessionService.Supervisor);
        vm.Draft = "   ";

        Assert.False(vm.SendCommand.CanExecute(null));
    }

    [Fact]
    public async Task Supervisor_approves_pending_plan_from_plans_screen()
    {
        var w = new DemoWorld();
        var shell = Shell(w, DemoSessionService.Supervisor);
        var plans = new PlansViewModel(DemoSessionService.Supervisor, w.Plans, new FakeDialogService());
        await plans.OnActivatedAsync();

        var board = plans.SelectedBoard!;
        Assert.Equal(2, board.PlanId);
        Assert.True(board.CanDecide);

        await board.AcceptCommand.ExecuteAsync(null);

        Assert.Equal(PlanStatus.Executed, w.Plans.Plans.Single(p => p.Id == 2).Status);
        Assert.Equal(0, shell.MenuItems.Single(i => i.Label == "Planos").Badge);
        Assert.Empty(plans.Rows);
    }

    [Fact]
    public async Task Rejecting_asks_for_a_reason_and_cancel_keeps_plan_pending()
    {
        var w = new DemoWorld();
        var cancel = new FakeDialogService(reason: null);
        var board = new PlanBoardViewModel(w.Plans.Plans.Single(p => p.Id == 2), DemoSessionService.Supervisor, w.Plans, cancel);

        await board.RejectCommand.ExecuteAsync(null);

        Assert.Equal(1, cancel.Calls);
        Assert.True(board.CanDecide);

        var confirm = new FakeDialogService("Contagem errada");
        var board2 = new PlanBoardViewModel(w.Plans.Plans.Single(p => p.Id == 2), DemoSessionService.Supervisor, w.Plans, confirm);
        await board2.RejectCommand.ExecuteAsync(null);

        Assert.True(board2.IsRejected);
        Assert.True(board.IsRejected);
        Assert.Contains("Contagem errada", board2.StatusText);
    }

    [Fact]
    public async Task Stock_filter_updates_rows_and_totals()
    {
        var vm = new StockViewModel(new DemoWorld().Queries, TimeProvider.System);
        await vm.OnActivatedAsync();
        var allRows = vm.Rows.Count;

        vm.FilterText = "abc-123";

        Assert.True(vm.Rows.Count < allRows);
        Assert.All(vm.Rows, r => Assert.Equal("ABC-123", r.Sku));
        Assert.Equal("80", vm.Totals.Single(t => t.Label == "Saldo total").Value);
    }

    [Fact]
    public void Summary_bars_scale_to_the_largest_value()
    {
        var summary = new SummaryViewModel(new ExecutionSummary([], [], [new BalanceChange("ALI-300", "C-01-01", 36, 18, "cx")]));

        var bar = Assert.Single(summary.Balances);
        Assert.Equal(SummaryViewModel.MaxBarWidth, bar.BeforeWidth);
        Assert.Equal(SummaryViewModel.MaxBarWidth / 2, bar.AfterWidth);
    }

    [Fact]
    public void Reject_dialog_requires_text_within_limit()
    {
        var vm = new RejectReasonDialogViewModel();
        Assert.False(vm.CanConfirm);

        vm.Reason = "Endereço errado";
        Assert.True(vm.CanConfirm);

        vm.Reason = new string('x', vm.MaxLength + 1);
        Assert.False(vm.CanConfirm);
    }
}
