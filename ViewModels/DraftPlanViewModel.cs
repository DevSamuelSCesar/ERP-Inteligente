using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using erp_inteligente.Models;
using erp_inteligente.Services;

namespace erp_inteligente.ViewModels;

/// <summary>
/// A plan proposed by the Planner and not yet written (references/UI.md, section 5.1): the steps and the simulated
/// effect, with Confirmar / Descartar for the requester. Confirming submits the draft to the engine; the stored plan
/// then appears as a regular plan board through <see cref="Confirmed"/>.
/// </summary>
public sealed partial class DraftPlanViewModel : ViewModelBase
{
    private readonly PlanDraft _draft;
    private readonly IPlanService _plans;

    /// <param name="proposal">Title, rationale and assumptions given by the Planner; null for a bare draft.</param>
    /// <param name="index">0-based position among the alternatives of the same answer.</param>
    /// <param name="count">How many alternatives the answer has.</param>
    public DraftPlanViewModel(PlanDraft draft, PlanPreview preview, IPlanService plans,
        NexusProposal? proposal = null, int index = 0, int count = 1, IEnumerable<string>? productNames = null)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(preview);
        if (!preview.IsValid)
            throw new ArgumentException("Only a valid dry run can be proposed.", nameof(preview));

        _draft = draft;
        _plans = plans;
        var label = count > 1 ? $"Opção {index + 1} de {count}" : "Proposta do Nexus";
        Title = !string.IsNullOrWhiteSpace(proposal?.Title) ? $"{label} · {proposal.Title}"
            : string.IsNullOrEmpty(draft.Skill) ? label : $"{label} · {draft.Skill}";
        Rationale = string.IsNullOrWhiteSpace(proposal?.Rationale) ? null : new TextBodyViewModel(proposal.Rationale, productNames: productNames);
        Assumptions = (proposal?.Assumptions ?? [])
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Take(MaxAssumptions)
            .Select(a => new TextBodyViewModel(a, productNames: productNames))
            .ToList();
        IsCritical = preview.Risk == PlanRisk.Critical;
        Steps = preview.Steps.Select(PlanBoardViewModel.ToLine).ToList();
        Effect = preview.Effect is { } effect ? new SummaryViewModel(effect) : null;
        Refresh();
    }

    /// <summary>Raised once, on the UI thread, with the plan the engine stored after the confirmation.</summary>
    public event EventHandler<Plan>? Confirmed;

    /// <summary>Raised once when the proposal stops waiting for the requester (confirmed, discarded or failed).</summary>
    public event EventHandler? Decided;

    public const int MaxAssumptions = 8;

    public string Title { get; }

    /// <summary>Why the Planner chose this path, highlighted like any answer.</summary>
    public TextBodyViewModel? Rationale { get; }

    /// <summary>Values the Planner assumed because the request did not give them.</summary>
    public IReadOnlyList<TextBodyViewModel> Assumptions { get; }
    public bool HasAssumptions => Assumptions.Count > 0;
    public bool IsCritical { get; }
    public string RiskText => IsCritical ? "Crítico" : "Reversível";
    public IReadOnlyList<PlanStepLine> Steps { get; }
    public SummaryViewModel? Effect { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand), nameof(DiscardCommand))]
    [NotifyPropertyChangedFor(nameof(IsPending))]
    public partial DraftState State { get; private set; } = DraftState.Pending;

    [ObservableProperty] public partial string StatusText { get; private set; } = "";

    public bool IsPending => State == DraftState.Pending;
    public bool IsSubmitting => State == DraftState.Submitting;
    public bool IsDone => State is DraftState.Confirmed or DraftState.Discarded;
    public bool IsFailed => State == DraftState.Failed;

    private bool CanDecide() => State == DraftState.Pending;

    [RelayCommand(CanExecute = nameof(CanDecide))]
    private async Task ConfirmAsync()
    {
        State = DraftState.Submitting;
        Refresh();
        try
        {
            var plan = await _plans.SubmitAsync(_draft);
            State = DraftState.Confirmed;
            Refresh(plan.Id);
            Confirmed?.Invoke(this, plan);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Trace.TraceError("Draft submission failed: {0}", ex.GetType().Name);
            State = DraftState.Failed;
            StatusText = ex switch
            {
                ArgumentException => $"Falhou: {ex.Message} Nada foi gravado.",
                Core.Policy.PolicyDeniedException or UnauthorizedAccessException => "Seu papel não permite enviar planos. Nada foi gravado.",
                _ => "Falha ao enviar o plano. Nada foi gravado.",
            };
            OnPropertyChanged(nameof(IsFailed));
        }
        Decided?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand(CanExecute = nameof(CanDecide))]
    private void Discard()
    {
        State = DraftState.Discarded;
        Refresh();
        Decided?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Another alternative of the same answer was confirmed: this one ends without being written.</summary>
    public void Supersede()
    {
        if (State != DraftState.Pending) return;
        State = DraftState.Discarded;
        Refresh();
        StatusText = "Outra opção foi escolhida. Nada foi gravado desta.";
    }

    private void Refresh(int? planId = null)
    {
        StatusText = State switch
        {
            DraftState.Pending when IsCritical => "Nada foi gravado. Ao confirmar, o plano vai para a aprovação de um supervisor.",
            DraftState.Pending => "Nada foi gravado. Confirmar?",
            DraftState.Submitting => "Validando e executando o plano",
            DraftState.Confirmed => $"Confirmado: plano {planId}",
            DraftState.Discarded => "Descartado. Nada foi gravado.",
            _ => StatusText,
        };
        OnPropertyChanged(nameof(IsSubmitting));
        OnPropertyChanged(nameof(IsDone));
    }
}

public enum DraftState
{
    Pending,
    Submitting,
    Confirmed,
    Discarded,
    Failed,
}
