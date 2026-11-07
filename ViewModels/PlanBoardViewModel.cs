using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using erp_inteligente.Models;
using erp_inteligente.Services;

namespace erp_inteligente.ViewModels;

/// <summary>A plan step formatted for the board's tabular lines.</summary>
public sealed record PlanStepLine(string Quantity, string Sku, string From, string To, string Note);

/// <summary>
/// The plan board (references/UI.md, section 5): business-language steps, risk, and the accept/reject prompt,
/// which is enabled only for an approver who is not the requester.
/// </summary>
public sealed partial class PlanBoardViewModel : ViewModelBase, IDisposable
{
    private readonly Plan _plan;
    private readonly UserSession _user;
    private readonly IPlanService _plans;
    private readonly IDialogService _dialogs;

    public PlanBoardViewModel(Plan plan, UserSession user, IPlanService plans, IDialogService dialogs)
    {
        _plan = plan;
        _user = user;
        _plans = plans;
        _dialogs = dialogs;

        Steps = plan.Steps.Select(ToLine).ToList();
        _plans.PlanChanged += OnPlanChanged;
        Refresh();
    }

    public int PlanId => _plan.Id;
    public string Title => $"Plano {_plan.Id} · {_plan.Skill}";
    public bool IsCritical => _plan.Risk == PlanRisk.Critical;
    public string RiskText => IsCritical ? "Crítico" : "Reversível";
    public IReadOnlyList<PlanStepLine> Steps { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AcceptCommand), nameof(RejectCommand))]
    public partial bool CanDecide { get; private set; }

    [ObservableProperty] public partial bool IsInProgress { get; private set; }
    [ObservableProperty] public partial bool IsAwaitingApprover { get; private set; }
    [ObservableProperty] public partial bool IsExecuted { get; private set; }
    [ObservableProperty] public partial bool IsRejected { get; private set; }
    [ObservableProperty] public partial bool IsFailed { get; private set; }
    [ObservableProperty] public partial string StatusText { get; private set; } = "";
    [ObservableProperty] public partial SummaryViewModel? Summary { get; private set; }

    [RelayCommand(CanExecute = nameof(CanDecide))]
    private async Task AcceptAsync()
    {
        try
        {
            await _plans.ApproveAsync(_plan, _user);
        }
        catch (UnauthorizedAccessException)
        {
            Refresh();
        }
    }

    [RelayCommand(CanExecute = nameof(CanDecide))]
    private async Task RejectAsync()
    {
        var reason = await _dialogs.AskRejectionReasonAsync();
        if (string.IsNullOrWhiteSpace(reason))
            return;

        try
        {
            await _plans.RejectAsync(_plan, _user, reason);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or ArgumentException)
        {
            Refresh();
        }
    }

    public void Dispose() => _plans.PlanChanged -= OnPlanChanged;

    private void OnPlanChanged(object? sender, Plan plan)
    {
        if (ReferenceEquals(plan, _plan))
            Refresh();
    }

    private void Refresh()
    {
        var status = _plan.Status;
        CanDecide = ApprovalPolicy.CanDecide(_plan, _user);
        IsInProgress = status == PlanStatus.Validating;
        IsAwaitingApprover = status == PlanStatus.PendingApproval && !CanDecide;
        IsExecuted = status == PlanStatus.Executed;
        IsRejected = status == PlanStatus.Rejected;
        IsFailed = status == PlanStatus.Failed;
        Summary = _plan.Summary is { } summary ? new SummaryViewModel(summary) : null;

        var at = _plan.DecidedAt?.ToLocalTime().ToString("dd/MM HH:mm") ?? "";
        StatusText = status switch
        {
            PlanStatus.Validating => "Validando plano",
            PlanStatus.PendingApproval when CanDecide => $"Pedido por {_plan.RequestedByName}. Aceitar?",
            PlanStatus.PendingApproval when _plan.RequestedBy == _user.Username => "Aguardando supervisor. Quem pediu não pode aprovar.",
            PlanStatus.PendingApproval => "Aguardando supervisor.",
            PlanStatus.Executed when _plan.Risk == PlanRisk.Critical => $"Aprovado por {_plan.DecidedByName} e executado em {at}",
            PlanStatus.Executed => $"Executado automaticamente em {at}",
            PlanStatus.Rejected => $"Rejeitado por {_plan.DecidedByName} em {at}: {_plan.RejectionReason}",
            PlanStatus.Failed => $"Falhou: {_plan.FailureMessage} Nada foi gravado.",
            _ => "",
        };
    }

    private static PlanStepLine ToLine(PlanStep step) => step.Operation switch
    {
        PlanOperation.AdjustStock => new PlanStepLine($"{step.Quantity:+0;-0;0} {step.Unit}", step.Sku, step.From ?? "", "", step.Note ?? ""),
        _ => new PlanStepLine($"{step.Quantity} {step.Unit}", step.Sku, step.From ?? "", step.To ?? "", step.Note ?? ""),
    };
}
