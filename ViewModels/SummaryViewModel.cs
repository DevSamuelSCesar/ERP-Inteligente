using System;
using System.Collections.Generic;
using System.Linq;
using erp_inteligente.Models;

namespace erp_inteligente.ViewModels;

public sealed record MoveFlowLine(string Sku, string Quantity, string From, string To);

/// <summary>Before/after bars; widths are in device-independent pixels.</summary>
public sealed record BalanceBarLine(string Label, string BeforeText, string AfterText, double BeforeWidth, double AfterWidth);

/// <summary>Visual summary after execution (references/UI.md, section 6): totals strip plus one visual per operation.</summary>
public sealed class SummaryViewModel : ViewModelBase
{
    public const double MaxBarWidth = 240;

    public SummaryViewModel(ExecutionSummary summary)
    {
        Totals = summary.Totals;
        Created = summary.CreatedItems;
        Flows = summary.Flows.Select(f => new MoveFlowLine(f.Sku, $"{f.Quantity} {f.Unit}", f.From, f.To)).ToList();

        var max = Math.Max(1, summary.Balances.Select(b => Math.Max(b.Before, b.After)).DefaultIfEmpty(0).Max());
        Balances = summary.Balances
            .Select(b => new BalanceBarLine($"{b.Sku} · {b.Location}", $"{b.Before} {b.Unit}", $"{b.After} {b.Unit}",
                MaxBarWidth * b.Before / max, MaxBarWidth * b.After / max))
            .ToList();
    }

    public IReadOnlyList<CreatedItem> Created { get; }
    public IReadOnlyList<SummaryTotal> Totals { get; }
    public IReadOnlyList<MoveFlowLine> Flows { get; }
    public IReadOnlyList<BalanceBarLine> Balances { get; }
    public bool HasFlows => Flows.Count > 0;
    public bool HasBalances => Balances.Count > 0;
    public bool HasCreated => Created.Count > 0;
}
