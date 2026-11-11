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
/// Auditoria (feature 9: audit panel), for <c>audit.read</c>: the last 500 events with filters by category and by problem
/// (denied or failed), the totals, and the state of the hash chain, which can be checked again at any time.
/// </summary>
public sealed partial class AuditViewModel(IQueryService queries, IOperationsService? operations = null) : QueryPageViewModel<AuditRow>
{
    public const string AllCategories = "Todas";

    public override string Title => "Auditoria";
    public override string FilterPlaceholder => "Filtrar por usuário, ação ou detalhe";

    public IReadOnlyList<string> Categories { get; } = [AllCategories, "Planos", "IA", "Segurança", "Conhecimento", "Sistema"];

    [ObservableProperty]
    public partial string SelectedCategory { get; set; } = AllCategories;

    partial void OnSelectedCategoryChanged(string value) => Refilter();

    /// <summary>Only denied and failed events.</summary>
    [ObservableProperty]
    public partial bool OnlyProblems { get; set; }

    partial void OnOnlyProblemsChanged(bool value) => Refilter();

    public bool CanVerify => operations is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasChainStatus))]
    public partial string ChainStatus { get; private set; } = "";

    [ObservableProperty] public partial bool ChainIsValid { get; private set; }
    [ObservableProperty] public partial bool ChainIsBroken { get; private set; }

    public bool HasChainStatus => ChainStatus.Length > 0;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(VerifyCommand))]
    public partial bool IsVerifying { get; private set; }

    public override async Task OnActivatedAsync()
    {
        await base.OnActivatedAsync();
        if (CanVerify && !HasChainStatus)
            await VerifyAsync();
    }

    protected override Task<IReadOnlyList<AuditRow>> LoadAsync() => queries.GetAuditAsync();

    protected override IEnumerable<string> SearchableText(AuditRow row) => [row.User, row.Action, row.Detail];

    protected override bool Include(AuditRow row) =>
        (SelectedCategory == AllCategories || row.Category == SelectedCategory) && (!OnlyProblems || row.IsDenied || row.IsFailed);

    protected override IEnumerable<SummaryTotal> BuildTotals(IReadOnlyList<AuditRow> rows)
    {
        yield return new("Eventos", rows.Count.ToString());
        yield return new("Planos executados", rows.Count(r => r.Action == "Plano executado").ToString());
        yield return new("Eventos negados", rows.Count(r => r.IsDenied).ToString());
        yield return new("Falhas", rows.Count(r => r.IsFailed).ToString());
        yield return new("Instruções suspeitas", rows.Count(r => r.Action == "Instrução suspeita").ToString());
    }

    private bool CanRunVerify() => CanVerify && !IsVerifying;

    [RelayCommand(CanExecute = nameof(CanRunVerify))]
    private async Task VerifyAsync()
    {
        IsVerifying = true;
        try
        {
            var result = await operations!.VerifyAuditAsync();
            ChainIsValid = result.IsValid;
            ChainIsBroken = !result.IsValid;
            ChainStatus = result.IsValid
                ? $"Cadeia de auditoria íntegra: {result.RowsChecked:N0} registros verificados agora."
                : $"Cadeia de auditoria quebrada na linha {result.FirstBrokenId}: um registro foi alterado ou removido. Avise o administrador.";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Trace.TraceError("Audit verification failed: {0}", ex.GetType().Name);
            ChainIsValid = false;
            ChainIsBroken = true;
            ChainStatus = "Não foi possível verificar a cadeia de auditoria.";
        }
        finally
        {
            IsVerifying = false;
        }
    }
}
