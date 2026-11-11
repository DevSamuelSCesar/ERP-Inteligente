using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using erp_inteligente.Models;
using erp_inteligente.Services;

namespace erp_inteligente.ViewModels;

/// <summary>
/// Asks the shell of the same session (<see cref="Plans"/> is that session's service) to open a plan in the Planos screen
/// (references/UI.md, section 6: the summary links to the plan).
/// </summary>
public sealed record OpenPlanMessage(int PlanId, IPlanService Plans);

/// <summary>A plan step formatted for the board's tabular lines.</summary>
public sealed record PlanStepLine(string Operation, string Quantity, string Sku, string From, string To, string Note);

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

    /// <param name="linkToPlans">False inside the Planos screen itself, where the link would lead back to the same board.</param>
    public PlanBoardViewModel(Plan plan, UserSession user, IPlanService plans, IDialogService dialogs, bool linkToPlans = true)
    {
        ShowPlanLink = linkToPlans
            && (user.Has(Permissions.PlanSubmit) || user.Has(Permissions.PlanApprove) || user.Has(Permissions.AuditRead));
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

    /// <summary>The "Ver em Planos" link; shown where the board is not already in the Planos screen.</summary>
    public bool ShowPlanLink { get; }

    [RelayCommand]
    private void OpenInPlans()
    {
        if (ShowPlanLink)
            WeakReferenceMessenger.Default.Send(new OpenPlanMessage(_plan.Id, _plans));
    }

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
        CanDecide = _plans.IsEngineAvailable && ApprovalPolicy.CanDecide(_plan, _user);
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
            PlanStatus.PendingApproval when !_plans.IsEngineAvailable =>
                $"Pedido por {_plan.RequestedByName}. Aprovar e rejeitar ficam disponíveis com o motor de planos.",
            PlanStatus.PendingApproval => "Aguardando supervisor.",
            PlanStatus.Executed when _plan.Risk == PlanRisk.Critical => $"Aprovado por {_plan.DecidedByName} e executado em {at}",
            PlanStatus.Executed => $"Executado automaticamente em {at}",
            PlanStatus.Rejected => $"Rejeitado por {_plan.DecidedByName} em {at}: {_plan.RejectionReason}",
            PlanStatus.Failed => $"Falhou: {_plan.FailureMessage} Nada foi gravado.",
            _ => "",
        };
    }

    internal static PlanStepLine ToLine(PlanStep step) => step.Operation switch
    {
        PlanOperation.ReceiveGoods => new("Recebimento", $"{step.Quantity} {step.Unit}", step.Sku, "", step.To ?? "", Lot(step.Note)),
        PlanOperation.MoveStock => new("Movimentação", $"{step.Quantity} {step.Unit}", step.Sku, step.From ?? "", step.To ?? "", Lot(step.Note)),
        PlanOperation.AdjustStock => new("Ajuste", $"{step.Quantity:+0;-0;0} {step.Unit}", step.Sku, step.From ?? "", "", step.Note ?? ""),
        PlanOperation.CreateProduct => new("Cadastro de produto", "", step.Sku, "", "", $"{step.Note} ({step.Unit})"),
        PlanOperation.CreateLocation => new("Cadastro de endereço", $"cap. {step.Quantity}", "", $"zona {step.From}", step.To ?? "", ""),
        PlanOperation.CreateZone => new("Cadastro de zona", "", "", $"zona {step.From}", "", step.Note ?? ""),
        PlanOperation.UpdateZone => new("Alteração de zona", "", "", $"zona {step.From}", "", step.Note ?? ""),
        PlanOperation.CreateLocationRange => new("Cadastro de endereços", $"cap. {step.Quantity} cada", "", $"zona {step.From}", step.To ?? "", ""),
        PlanOperation.UpdateLocation => new("Alteração de endereço", "", "", step.From ?? "", "", step.Note ?? ""),
        PlanOperation.UpdateProduct => new("Alteração de produto", "", step.Sku, "", "", step.Note ?? ""),
        PlanOperation.CreateSupplier => new("Cadastro de fornecedor", "", "", "", "", Partner(step)),
        PlanOperation.CreateCustomer => new("Cadastro de cliente", "", "", "", "", Partner(step)),
        PlanOperation.CreateInboundOrder => new("Pedido de entrada", Lines(step.Quantity), "", step.From ?? "", step.To ?? "", step.Note ?? ""),
        PlanOperation.CancelInboundOrder => new("Cancelamento de pedido de entrada", "", "", step.From ?? "", "", step.Note ?? ""),
        PlanOperation.CreateOutboundOrder => new("Pedido de saída", Lines(step.Quantity), "", step.From ?? "", step.To ?? "", step.Note ?? ""),
        PlanOperation.ReserveStock => new("Reserva de estoque", "", "", $"pedido {step.From}", "", step.Note ?? ""),
        PlanOperation.ReleaseReservation => new("Liberação de reserva", "", "", $"pedido {step.From}", "", step.Note ?? ""),
        PlanOperation.PickItems => new("Separação", "", "", $"pedido {step.From}", step.To ?? "", step.Note ?? ""),
        PlanOperation.ShipOrder => new("Expedição", "", "", $"pedido {step.From}", "", ""),
        PlanOperation.CancelOutboundOrder => new("Cancelamento de pedido de saída", "", "", $"pedido {step.From}", "", step.Note ?? ""),
        _ => new(step.Operation.ToString(), $"{step.Quantity} {step.Unit}", step.Sku, step.From ?? "", step.To ?? "", step.Note ?? ""),
    };

    private static string Lines(int count) => count == 1 ? "1 linha" : $"{count} linhas";

    private static string Partner(PlanStep step) =>
        string.IsNullOrEmpty(step.To) ? step.Note ?? "" : $"{step.Note} ({step.To})";

    private static string Lot(string? lot) => string.IsNullOrEmpty(lot) ? "" : $"lote {lot}";
}
