using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using erp_inteligente.Models;
using erp_inteligente.Services;

namespace erp_inteligente.ViewModels;

/// <summary>The Nexus chat: transcript, status line and request field (references/UI.md, section 4).</summary>
public sealed partial class NexusViewModel(UserSession user, INexusService nexus, IPlanService plans, IDialogService dialogs, TimeProvider time)
    : PageViewModel
{
    public const string NexusAuthor = "Nexus";

    public override string Title => "Nexus";

    public ObservableCollection<TranscriptEntryViewModel> Entries { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    public partial string Draft { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    [NotifyPropertyChangedFor(nameof(IsInputEnabled))]
    public partial bool IsBusy { get; private set; }

    [ObservableProperty]
    public partial string StatusText { get; private set; } = "";

    public bool IsInputEnabled => !IsBusy;

    private bool CanSend() => !IsBusy && !string.IsNullOrWhiteSpace(Draft);

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        var request = Draft.Trim();
        Draft = "";
        Entries.Add(new TranscriptEntryViewModel(time.GetUtcNow(), user.DisplayName, new TextBodyViewModel(request)));

        IsBusy = true;
        StatusText = "Interpretando o pedido";
        try
        {
            var reply = await nexus.SendAsync(request, user, new InlineProgress(s => StatusText = s));
            Entries.Add(new TranscriptEntryViewModel(time.GetUtcNow(), NexusAuthor, new TextBodyViewModel(reply.Text, reply.IsError)));

            if (reply.Draft is { } draft)
            {
                StatusText = "Validando e simulando o plano";
                var plan = await plans.SubmitAsync(draft);
                Entries.Add(new TranscriptEntryViewModel(time.GetUtcNow(), NexusAuthor, new PlanBoardViewModel(plan, user, plans, dialogs)));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Trace.TraceError("Nexus request failed: {0}", ex.GetType().Name);
            Entries.Add(new TranscriptEntryViewModel(time.GetUtcNow(), NexusAuthor,
                new TextBodyViewModel("Falha ao processar o pedido. Nada foi gravado. Tente de novo.", isError: true)));
        }
        finally
        {
            IsBusy = false;
            StatusText = "";
        }
    }

    /// <summary>Reports synchronously, so no status line arrives after the request has finished.</summary>
    private sealed class InlineProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }
}
