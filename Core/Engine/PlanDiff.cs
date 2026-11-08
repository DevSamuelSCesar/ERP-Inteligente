using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using erp_inteligente.Models;

namespace erp_inteligente.Core.Engine;

/// <summary>Balance of a SKU at a location (and lot) before and after a plan.</summary>
public sealed record BalanceDelta(string Sku, string Location, string? Lot, int Before, int After, string Unit);

/// <summary>Stock moved between two locations.</summary>
public sealed record MoveDelta(string Sku, string? Lot, string From, string To, int Quantity, string Unit);

/// <summary>A catalog record created by the plan (<c>Produto</c> or <c>Endereço</c>).</summary>
public sealed record CreatedRecord(string Kind, string Code, string Description);

/// <summary>A signed quantity of one SKU (a receipt or an adjustment).</summary>
public sealed record QuantityDelta(string Sku, int Quantity, string Unit);

/// <summary>
/// Immutable result of simulating or executing a plan. <see cref="Balances"/> holds every balance change
/// (moves included); <see cref="DirectBalances"/> only those caused by receipts and adjustments.
/// </summary>
public sealed class PlanDiff
{
    public PlanDiff(
        IReadOnlyList<BalanceDelta> balances,
        IReadOnlyList<BalanceDelta> directBalances,
        IReadOnlyList<MoveDelta> moves,
        IReadOnlyList<CreatedRecord> created,
        IReadOnlyList<QuantityDelta> receipts,
        IReadOnlyList<QuantityDelta> adjustments)
    {
        Balances = balances;
        DirectBalances = directBalances;
        Moves = moves;
        Created = created;
        Receipts = receipts;
        Adjustments = adjustments;
    }

    public IReadOnlyList<BalanceDelta> Balances { get; }
    public IReadOnlyList<BalanceDelta> DirectBalances { get; }
    public IReadOnlyList<MoveDelta> Moves { get; }
    public IReadOnlyList<CreatedRecord> Created { get; }
    public IReadOnlyList<QuantityDelta> Receipts { get; }
    public IReadOnlyList<QuantityDelta> Adjustments { get; }

    /// <summary>True when the plan changed nothing.</summary>
    public bool IsEmpty => Balances.Count == 0 && Moves.Count == 0 && Created.Count == 0;

    /// <summary>Value equality of all lists (used to compare a simulation with the real execution).</summary>
    public bool SameAs(PlanDiff other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return Balances.SequenceEqual(other.Balances) && DirectBalances.SequenceEqual(other.DirectBalances)
            && Moves.SequenceEqual(other.Moves) && Created.SequenceEqual(other.Created)
            && Receipts.SequenceEqual(other.Receipts) && Adjustments.SequenceEqual(other.Adjustments);
    }

    /// <summary>Builds the operator-facing summary, in Portuguese, from this diff only.</summary>
    public ExecutionSummary ToSummary(RiskLevel risk)
    {
        var totals = new List<SummaryTotal>();
        if (Moves.Count > 0)
            totals.Add(new SummaryTotal("Quantidade movimentada", FormatQuantity(Moves.Sum(m => m.Quantity), Moves.Select(m => m.Unit))));
        if (Receipts.Count > 0)
            totals.Add(new SummaryTotal("Quantidade recebida", FormatQuantity(Receipts.Sum(r => r.Quantity), Receipts.Select(r => r.Unit))));
        if (Adjustments.Count > 0)
            totals.Add(new SummaryTotal("Ajuste líquido",
                FormatQuantity(Adjustments.Sum(a => a.Quantity), Adjustments.Select(a => a.Unit), signed: true)));

        var locations = Balances.Select(b => b.Location).Concat(Moves.SelectMany(m => new[] { m.From, m.To })).Distinct().Count();
        if (locations > 0)
            totals.Add(new SummaryTotal("Endereços afetados", locations.ToString(CultureInfo.InvariantCulture)));
        var skus = Balances.Select(b => b.Sku).Concat(Moves.Select(m => m.Sku)).Distinct().Count();
        if (skus > 0)
            totals.Add(new SummaryTotal("SKUs", skus.ToString(CultureInfo.InvariantCulture)));
        if (Created.Count > 0)
            totals.Add(new SummaryTotal("Cadastros", Created.Count.ToString(CultureInfo.InvariantCulture)));
        totals.Add(new SummaryTotal("Risco", risk switch
        {
            RiskLevel.Critical => "Crítico",
            RiskLevel.Reversible => "Reversível",
            _ => "Leitura",
        }));

        var flows = Moves.Select(m => new MoveFlow(m.Sku, m.Quantity, m.Unit, m.From, m.To)).ToList();
        var balances = new List<BalanceChange>();
        foreach (var group in DirectBalances.GroupBy(b => (b.Sku, b.Location)))
        {
            var items = group.ToList();
            balances.Add(new BalanceChange(group.Key.Sku, group.Key.Location,
                items.Sum(i => i.Before), items.Sum(i => i.After), items[0].Unit));
        }
        var created = Created.Select(c => new CreatedItem(c.Kind, c.Code, c.Description)).ToList();
        return new ExecutionSummary(totals, flows, balances, created);
    }

    private static string FormatQuantity(int total, IEnumerable<string> units, bool signed = false)
    {
        var text = signed && total > 0
            ? "+" + total.ToString(CultureInfo.InvariantCulture)
            : total.ToString(CultureInfo.InvariantCulture);
        var distinct = units.Distinct(StringComparer.Ordinal).ToList();
        return distinct.Count == 1 ? $"{text} {distinct[0]}" : text;
    }
}

/// <summary>Collects the effects of the steps of one run and produces an immutable <see cref="PlanDiff"/>.</summary>
public sealed class PlanDiffBuilder
{
    private readonly Dictionary<(string, string, string), BalanceDelta> _all = [];
    private readonly Dictionary<(string, string, string), BalanceDelta> _direct = [];
    private readonly List<MoveDelta> _moves = [];
    private readonly List<CreatedRecord> _created = [];
    private readonly List<QuantityDelta> _receipts = [];
    private readonly List<QuantityDelta> _adjustments = [];

    /// <summary>Records a balance change caused by a move (shown as a flow only in the summary).</summary>
    public void AddMoveBalance(string sku, string location, string? lot, int before, int after, string unit) =>
        Merge(_all, new BalanceDelta(sku, location, lot, before, after, unit));

    /// <summary>Records a balance change caused by a receipt or an adjustment.</summary>
    public void AddDirectBalance(string sku, string location, string? lot, int before, int after, string unit)
    {
        var delta = new BalanceDelta(sku, location, lot, before, after, unit);
        Merge(_all, delta);
        Merge(_direct, delta);
    }

    public void AddMove(string sku, string? lot, string from, string to, int quantity, string unit) =>
        _moves.Add(new MoveDelta(sku, lot, from, to, quantity, unit));

    public void AddReceipt(string sku, int quantity, string unit) => _receipts.Add(new QuantityDelta(sku, quantity, unit));

    public void AddAdjustment(string sku, int quantity, string unit) => _adjustments.Add(new QuantityDelta(sku, quantity, unit));

    public void AddCreated(string kind, string code, string description) => _created.Add(new CreatedRecord(kind, code, description));

    public PlanDiff Build() => new(_all.Values.ToList(), _direct.Values.ToList(), _moves.ToList(), _created.ToList(),
        _receipts.ToList(), _adjustments.ToList());

    // Aggregates per sku+location+lot: before = first observed, after = last.
    private static void Merge(Dictionary<(string, string, string), BalanceDelta> target, BalanceDelta delta)
    {
        var key = (delta.Sku, delta.Location, delta.Lot ?? string.Empty);
        target[key] = target.TryGetValue(key, out var existing) ? existing with { After = delta.After } : delta;
    }
}
