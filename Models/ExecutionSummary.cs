using System.Collections.Generic;

namespace erp_inteligente.Models;

/// <summary>Label/value pair of a totals strip.</summary>
public sealed record SummaryTotal(string Label, string Value);

/// <summary>Stock moved from one location to another.</summary>
public sealed record MoveFlow(string Sku, int Quantity, string Unit, string From, string To);

/// <summary>Balance of a SKU at a location before and after the execution.</summary>
public sealed record BalanceChange(string Sku, string Location, int Before, int After, string Unit);

/// <summary>What was operated, built from the executor result (never from LLM text).</summary>
public sealed record ExecutionSummary(
    IReadOnlyList<SummaryTotal> Totals,
    IReadOnlyList<MoveFlow> Flows,
    IReadOnlyList<BalanceChange> Balances);
