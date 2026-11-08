using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using erp_inteligente.Core.Skills;
using erp_inteligente.Services;

namespace erp_inteligente.ViewModels;

/// <summary>
/// A skill drafted by the Nexus and not yet saved (references/UI.md, section 5.3): the whole text is shown, and only
/// the requester's Confirmar stores it, with that person's rights (<c>skill.manage</c>) and origin <c>ai</c>.
/// </summary>
public sealed partial class SkillProposalViewModel : ViewModelBase
{
    private readonly NexusSkillProposal _proposal;
    private readonly ISkillService _skills;

    public SkillProposalViewModel(NexusSkillProposal proposal, ISkillService skills, IEnumerable<string>? productNames = null)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(skills);
        _proposal = proposal;
        _skills = skills;
        var doc = proposal.Document;
        Title = proposal.CurrentVersion is { } current
            ? $"Nova versão de {doc.Name} (substitui a v{current})"
            : $"Skill proposta · {doc.Name}";
        Reason = string.IsNullOrWhiteSpace(proposal.Reason) ? null : new TextBodyViewModel(proposal.Reason, productNames: productNames);
        Operations = SkillOperationLine.Of(doc.Operations);
        Refresh();
    }

    /// <summary>Raised once when the proposal stops waiting for the requester (confirmed, discarded or failed).</summary>
    public event EventHandler? Decided;

    public string Title { get; }
    public TextBodyViewModel? Reason { get; }
    public string Description => _proposal.Document.Description;
    public IReadOnlyList<SkillOperationLine> Operations { get; }
    public bool HasOperations => Operations.Count > 0;
    public string Body => _proposal.Document.Body;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand), nameof(DiscardCommand))]
    [NotifyPropertyChangedFor(nameof(IsPending), nameof(IsSubmitting), nameof(IsDone), nameof(IsFailed))]
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
            var saved = await _skills.SaveAsync(_proposal.Document, SkillOrigin.Ai, _proposal.CurrentVersion);
            State = DraftState.Confirmed;
            StatusText = saved.Status == SkillStatus.Archived
                ? $"Confirmado: skill {saved.Name} v{saved.Version} salva, mas está arquivada. Reative-a em Skills para o Nexus usá-la."
                : $"Confirmado: skill {saved.Name} v{saved.Version} salva. Ela já vale para os próximos pedidos.";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Trace.TraceError("Skill proposal save failed: {0}", ex.GetType().Name);
            State = DraftState.Failed;
            StatusText = SkillsViewModel.MessageFor(ex) + " Nada foi gravado.";
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

    private void Refresh() => StatusText = State switch
    {
        DraftState.Pending => "Nada foi gravado. Confirmar a skill?",
        DraftState.Submitting => "Salvando a skill",
        DraftState.Discarded => "Descartada. Nada foi gravado.",
        _ => StatusText,
    };
}
