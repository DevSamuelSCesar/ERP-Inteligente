using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using erp_inteligente.Models;
using erp_inteligente.Services;

namespace erp_inteligente.ViewModels;

/// <summary>
/// The Nexus chat: transcript, status line and request field (references/UI.md, section 4). A plan proposed by the
/// Planner waits for the requester's Confirmar / Descartar; the request field stays disabled meanwhile.
/// </summary>
/// <param name="productNames">Catalog names highlighted in the answers; read again after every request.</param>
/// <param name="skills">Saves a skill drafted by the Nexus when the requester confirms it; null hides such drafts.</param>
public sealed partial class NexusViewModel(UserSession user, INexusService nexus, IPlanService plans, IDialogService dialogs, TimeProvider time,
    Func<Task<IReadOnlyList<string>>>? productNames = null, ISkillService? skills = null)
    : PageViewModel
{
    public const string NexusAuthor = "Nexus";

    private CancellationTokenSource? _running;
    private IReadOnlyList<string> _productNames = [];

    public override string Title => "Nexus";

    public ObservableCollection<TranscriptEntryViewModel> Entries { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    public partial string Draft { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand), nameof(CancelCommand))]
    [NotifyPropertyChangedFor(nameof(IsInputEnabled))]
    public partial bool IsBusy { get; private set; }

    /// <summary>
    /// A proposal of the Planner still waiting for the requester, if any (the first of the pending alternatives).
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    [NotifyPropertyChangedFor(nameof(IsInputEnabled))]
    public partial DraftPlanViewModel? PendingProposal { get; private set; }

    /// <summary>A skill drafted by the Planner still waiting for the requester, if any.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    [NotifyPropertyChangedFor(nameof(IsInputEnabled))]
    public partial SkillProposalViewModel? PendingSkill { get; private set; }

    private readonly List<DraftPlanViewModel> _pending = [];
    private readonly List<OptionsViewModel> _optionRows = [];

    [ObservableProperty]
    public partial string StatusText { get; private set; } = "";

    public bool IsInputEnabled => !IsBusy && PendingProposal is null && PendingSkill is null;

    private bool CanSend() => IsInputEnabled && !string.IsNullOrWhiteSpace(Draft);

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        var request = Draft.Trim();
        Draft = "";
        Entries.Add(new TranscriptEntryViewModel(time.GetUtcNow(), user.DisplayName, new TextBodyViewModel(request)));

        using var running = new CancellationTokenSource();
        _running = running;
        IsBusy = true;
        StatusText = "Interpretando o pedido";
        try
        {
            var reply = await nexus.SendAsync(request, user, new InlineProgress(s => StatusText = s), running.Token);
            await RefreshProductNamesAsync();
            if (!string.IsNullOrWhiteSpace(reply.Text))
                Say(new TextBodyViewModel(reply.Text, reply.IsError, reply.IsEscalated, _productNames));

            var proposals = reply.Proposals is { Count: > 0 } list
                ? list.Take(MaxAlternatives).ToList()
                : reply.Draft is { } draft ? [new NexusProposal("", "", [], draft, reply.Preview)] : [];
            if (proposals.Count > 0)
                await ProposeAsync(proposals);
            if (reply.Skill is { } skill && skills is not null)
                ProposeSkill(skill);

            if (reply.Options is { Count: > 0 } options)
            {
                var row = new OptionsViewModel(options, SendOptionAsync, () => IsInputEnabled);
                if (row.Options.Count > 0)
                {
                    _optionRows.Add(row);
                    Say(row);
                }
            }
        }
        catch (OperationCanceledException) when (running.IsCancellationRequested)
        {
            Say(new TextBodyViewModel("Pedido cancelado. Nada foi gravado."));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Trace.TraceError("Nexus request failed: {0}", ex.GetType().Name);
            Say(new TextBodyViewModel("Falha ao processar o pedido. Nada foi gravado. Tente de novo.", isError: true));
        }
        finally
        {
            _running = null;
            IsBusy = false;
            StatusText = "";
        }
    }

    /// <summary>Highlighting is a convenience: a failed read keeps the previous names.</summary>
    private async Task RefreshProductNamesAsync()
    {
        if (productNames is null) return;
        try
        {
            _productNames = await productNames();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Trace.TraceError("Product names unavailable: {0}", ex.GetType().Name);
        }
    }

    private bool CanCancel() => IsBusy;

    /// <summary>Stops the running request (and its call to the provider); nothing has been written at that point.</summary>
    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => _running?.Cancel();

    public const int MaxAlternatives = 3;

    /// <summary>
    /// Shows each alternative with its dry run; nothing is written before the requester confirms one. Confirming one
    /// supersedes the others, discarding all of them frees the request field.
    /// </summary>
    private async Task ProposeAsync(IReadOnlyList<NexusProposal> proposals)
    {
        StatusText = "Simulando o plano";
        var cards = new List<DraftPlanViewModel>();
        var invalid = new List<string>();
        for (var i = 0; i < proposals.Count; i++)
        {
            var proposal = proposals[i];
            var preview = proposal.Preview ?? await plans.PreviewAsync(proposal.Draft);
            if (preview.IsValid)
                cards.Add(new DraftPlanViewModel(proposal.Draft, preview, plans, proposal, cards.Count, proposals.Count, _productNames));
            else
                invalid.AddRange(preview.Issues.Select(issue => issue.Message));
        }

        if (cards.Count == 0)
        {
            Say(new TextBodyViewModel(
                "O plano proposto não passou na validação: " + string.Join(" ", invalid) + " Nada foi gravado.", isError: true));
            return;
        }

        foreach (var card in cards)
        {
            card.Confirmed += (_, plan) =>
            {
                foreach (var other in cards.Where(c => !ReferenceEquals(c, card)))
                    other.Supersede();
                Say(new PlanBoardViewModel(plan, user, plans, dialogs));
            };
            card.Decided += (_, _) =>
            {
                _pending.Remove(card);
                if (cards.All(c => c.State != DraftState.Pending))
                    _pending.RemoveAll(cards.Contains);
                PendingProposal = _pending.FirstOrDefault();
                RefreshOptions();
            };
            _pending.Add(card);
            Say(card);
        }
        PendingProposal = _pending.FirstOrDefault();
    }

    /// <summary>Shows a drafted skill; like a plan, nothing is saved before the requester confirms it.</summary>
    private void ProposeSkill(NexusSkillProposal proposal)
    {
        var card = new SkillProposalViewModel(proposal, skills!, _productNames);
        card.Decided += (_, _) =>
        {
            if (ReferenceEquals(PendingSkill, card))
                PendingSkill = null;
            RefreshOptions();
        };
        PendingSkill = card;
        Say(card);
    }

    /// <summary>An option button behaves like typing its request and pressing Enter.</summary>
    private async Task SendOptionAsync(string request)
    {
        if (!IsInputEnabled) return;
        Draft = request;
        if (SendCommand.CanExecute(null))
            await SendCommand.ExecuteAsync(null);
    }

    private void RefreshOptions()
    {
        foreach (var row in _optionRows)
            row.Refresh();
    }

    partial void OnIsBusyChanged(bool value) => RefreshOptions();

    private void Say(ViewModelBase body) => Entries.Add(new TranscriptEntryViewModel(time.GetUtcNow(), NexusAuthor, body));

    /// <summary>Reports synchronously, so no status line arrives after the request has finished.</summary>
    private sealed class InlineProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }
}
