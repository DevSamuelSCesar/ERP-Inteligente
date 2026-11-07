using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using erp_inteligente.Models;
using erp_inteligente.Services;

namespace erp_inteligente.ViewModels;

public sealed record PlanRow(Plan Plan, string Number, string CreatedText, string Requester, string Skill, string Risk, string Status);

/// <summary>Plan history and pending approvals; the selected plan opens in the same board used by the Nexus.</summary>
public sealed partial class PlansViewModel : PageViewModel
{
    private readonly UserSession _user;
    private readonly IPlanService _plans;
    private readonly IDialogService _dialogs;

    public PlansViewModel(UserSession user, IPlanService plans, IDialogService dialogs)
    {
        _user = user;
        _plans = plans;
        _dialogs = dialogs;
        ShowPendingOnly = user.Has(Permissions.PlanApprove);
        _plans.PlanChanged += (_, _) => Reload();
    }

    public override string Title => "Planos";

    public ObservableCollection<PlanRow> Rows { get; } = [];
    public ObservableCollection<SummaryTotal> Totals { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowAll))]
    public partial bool ShowPendingOnly { get; set; }

    /// <summary>Second tab; mirrors <see cref="ShowPendingOnly"/> for the radio-button tabs.</summary>
    public bool ShowAll
    {
        get => !ShowPendingOnly;
        set => ShowPendingOnly = !value;
    }

    [ObservableProperty]
    public partial PlanRow? SelectedRow { get; set; }

    [ObservableProperty]
    public partial PlanBoardViewModel? SelectedBoard { get; private set; }

    partial void OnShowPendingOnlyChanged(bool value) => Reload();

    partial void OnSelectedRowChanged(PlanRow? value)
    {
        SelectedBoard?.Dispose();
        SelectedBoard = value is null ? null : new PlanBoardViewModel(value.Plan, _user, _plans, _dialogs);
    }

    public override Task OnActivatedAsync()
    {
        Reload();
        return Task.CompletedTask;
    }

    private void Reload()
    {
        var selectedId = SelectedRow?.Plan.Id;
        IEnumerable<Plan> plans = _plans.Plans.OrderByDescending(p => p.CreatedAt);
        if (ShowPendingOnly)
            plans = plans.Where(p => p.Status == PlanStatus.PendingApproval);

        Rows.Clear();
        foreach (var plan in plans)
            Rows.Add(ToRow(plan));

        Totals.Clear();
        Totals.Add(new("Pendentes", _plans.Plans.Count(p => p.Status == PlanStatus.PendingApproval).ToString()));
        Totals.Add(new("Executados", _plans.Plans.Count(p => p.Status == PlanStatus.Executed).ToString()));
        Totals.Add(new("Rejeitados", _plans.Plans.Count(p => p.Status == PlanStatus.Rejected).ToString()));
        Totals.Add(new("Falhas", _plans.Plans.Count(p => p.Status == PlanStatus.Failed).ToString()));

        SelectedRow = Rows.FirstOrDefault(r => r.Plan.Id == selectedId)
            ?? Rows.FirstOrDefault(r => ApprovalPolicy.CanDecide(r.Plan, _user))
            ?? Rows.FirstOrDefault();
    }

    private static PlanRow ToRow(Plan plan) => new(
        plan,
        plan.Id.ToString(),
        plan.CreatedAt.ToLocalTime().ToString("dd/MM/yyyy HH:mm"),
        plan.RequestedByName,
        plan.Skill,
        plan.Risk == PlanRisk.Critical ? "Crítico" : "Reversível",
        plan.Status switch
        {
            PlanStatus.Validating => "Validando",
            PlanStatus.PendingApproval => "Aguardando aprovação",
            PlanStatus.Rejected => "Rejeitado",
            PlanStatus.Executed => "Executado",
            PlanStatus.Failed => "Falhou",
            _ => "",
        });
}
