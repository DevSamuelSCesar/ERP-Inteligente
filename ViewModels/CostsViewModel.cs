using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using erp_inteligente.Core.Llm;
using erp_inteligente.Core.Operations;
using erp_inteligente.Models;
using erp_inteligente.Services;

namespace erp_inteligente.ViewModels;

/// <summary>A period of the cost panel.</summary>
public sealed record PeriodOption(int Days, string Label)
{
    public override string ToString() => Label;
}

/// <summary>One line of the cost table, already formatted.</summary>
public sealed record UsageLineRow(string Role, string Provider, string Model, long Requests, long Calls, string Tokens, string Cost);

/// <summary>
/// Custos de IA (feature 9: token cost panel), for <c>audit.read</c>: requests, the share solved by recipes, average tokens
/// per request (the metrics of references/IA.md, section 5) and tokens and estimated cost per role, provider and model.
/// Costs use the prices of references/IA.md; a model without a price shows its tokens only.
/// </summary>
public sealed partial class CostsViewModel(IOperationsService operations) : PageViewModel
{
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");

    public override string Title => "Custos de IA";

    public IReadOnlyList<PeriodOption> Periods { get; } = [new(7, "Últimos 7 dias"), new(30, "Últimos 30 dias"), new(90, "Últimos 90 dias")];

    [ObservableProperty]
    public partial PeriodOption? SelectedPeriod { get; set; }

    partial void OnSelectedPeriodChanged(PeriodOption? value)
    {
        if (value is not null) _ = LoadAsync();
    }

    public ObservableCollection<SummaryTotal> Totals { get; } = [];
    public ObservableCollection<UsageLineRow> Lines { get; } = [];
    public bool IsEmpty => Lines.Count == 0;

    [ObservableProperty] public partial string Note { get; private set; } = "";
    [ObservableProperty] public partial string Error { get; private set; } = "";
    public bool HasError => Error.Length > 0;

    public override Task OnActivatedAsync()
    {
        if (SelectedPeriod is null)
        {
            SelectedPeriod = Periods[1]; // loads
            return Task.CompletedTask;
        }
        return LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            var report = await operations.GetUsageAsync(SelectedPeriod?.Days ?? 30);
            Error = "";
            Apply(report);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Trace.TraceError("Usage report failed: {0}", ex.GetType().Name);
            Error = "Não foi possível carregar o consumo de IA.";
        }
        OnPropertyChanged(nameof(HasError));
    }

    internal void Apply(UsageReport report)
    {
        Totals.Clear();
        Totals.Add(new("Pedidos", report.Requests.ToString("N0", PtBr)));
        Totals.Add(new("Resolvidos por receita", report.RecipeShare.ToString("P0", PtBr)));
        Totals.Add(new("Tokens por pedido", report.AverageTokensPerRequest.ToString("N0", PtBr)));
        foreach (var (currency, value) in report.CostByCurrency.OrderBy(c => c.Key, StringComparer.Ordinal))
            Totals.Add(new($"Custo estimado ({LlmPrices.Symbol(currency)})", LlmPrices.Format(value, currency)));

        Lines.Clear();
        foreach (var line in report.Lines)
            Lines.Add(new UsageLineRow(
                line.Role == LlmUseRole.Planner ? "Planejamento" : "Receitas",
                LlmSettings.ProviderName(line.Provider),
                line.Model,
                line.Requests,
                line.Calls,
                $"{line.PromptTokens.ToString("N0", PtBr)} / {line.CompletionTokens.ToString("N0", PtBr)}" +
                (line.CacheReadTokens > 0 ? $" ({line.CacheReadTokens.ToString("N0", PtBr)} do cache)" : ""),
                line.Cost is { } cost && line.Currency is { } currency ? LlmPrices.Format(cost, currency) : "sem preço"));
        OnPropertyChanged(nameof(IsEmpty));

        var notes = new List<string> { "Tokens de entrada / saída. Custos estimados com os preços de referência (IA.md, seção 2); a DeepSeek cobra em ¥ e no pico." };
        if (report.Lines.Any(l => l.CacheReadTokens > 0))
            notes.Add("Entrada lida do cache de prompt custa 10% do preço de entrada; a gravada no cache, 125%.");
        if (report.Unpriced > 0)
            notes.Add($"{report.Unpriced.ToString("N0", PtBr)} tokens são de modelos sem preço cadastrado e ficaram fora do custo.");
        Note = string.Join(" ", notes);
    }
}
