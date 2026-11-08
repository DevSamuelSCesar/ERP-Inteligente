using System;
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
public sealed partial class NexusViewModel(UserSession user, INexusService nexus, IPlanService plans, IDialogService dialogs, TimeProvider time)
    : PageViewModel
{
    public const string NexusAuthor = "Nexus";

    private CancellationTokenSource? _running;

    public override string Title => "Nexus";

    public ObservableCollection<TranscriptEntryViewModel> Entries { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    public partial string Draft { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand), nameof(CancelCommand))]
    [NotifyPropertyChangedFor(nameof(IsInputEnabled))]
    public partial bool IsBusy { get; private set; }

    /// <summary>The proposal of the Planner still waiting for the requester, if any.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    [NotifyPropertyChangedFor(nameof(IsInputEnabled))]
    public partial DraftPlanViewModel? PendingProposal { get; private set; }

    [ObservableProperty]
    public partial string StatusText { get; private set; } = "";

    public bool IsInputEnabled => !IsBusy && PendingProposal is null;

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
            if (!string.IsNullOrWhiteSpace(reply.Text))
                Say(new TextBodyViewModel(reply.Text, reply.IsError, reply.IsEscalated));

            if (reply.Draft is { } draft)
            {
                // The draft is shown with its dry run; nothing is written before the requester confirms.
                StatusText = "Simulando o plano";
                var preview = reply.Preview ?? await plans.PreviewAsync(draft);
                if (preview.IsValid)
                    Propose(new DraftPlanViewModel(draft, preview, plans));
                else
                    Say(new TextBodyViewModel(
                        "O plano proposto não passou na validação: " + string.Join(" ", preview.Issues.Select(i => i.Message)) +
                        " Nada foi gravado.", isError: true));
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

    private bool CanCancel() => IsBusy;

    /// <summary>Stops the running request (and its call to the provider); nothing has been written at that point.</summary>
    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => _running?.Cancel();

    private void Propose(DraftPlanViewModel proposal)
    {
        proposal.Confirmed += (_, plan) =>
            Say(new PlanBoardViewModel(plan, user, plans, dialogs));
        proposal.Decided += (_, _) =>
        {
            if (ReferenceEquals(PendingProposal, proposal))
                PendingProposal = null;
        };
        PendingProposal = proposal;
        Say(proposal);
    }

    private void Say(ViewModelBase body) => Entries.Add(new TranscriptEntryViewModel(time.GetUtcNow(), NexusAuthor, body));

    /// <summary>Reports synchronously, so no status line arrives after the request has finished.</summary>
    private sealed class InlineProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }
}
