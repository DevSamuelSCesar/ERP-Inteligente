using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json.Nodes;
using static erp_inteligente.Core.Engine.StockStore;

namespace erp_inteligente.Core.Engine;

/// <summary>One validated line of an order to create.</summary>
internal sealed record OrderLine(ProductRow Product, int Quantity);

/// <summary>Parameterized lookups and checks shared by the order operations.</summary>
internal static class OrderStore
{
    /// <summary>
    /// Finds a customer or supplier by exact name (case-insensitive, accents included, compared in C# because SQLite
    /// folds only ASCII). Returns the id and canonical name, whether it is active; null when no row has that name.
    /// <paramref name="table"/> is a constant of the callers, never input.
    /// </summary>
    public static (long Id, string Name, bool Active)? FindPartner(EngineContext ctx, string table, string? name)
    {
        var wanted = name?.Trim();
        if (string.IsNullOrEmpty(wanted)) return null;
        (long, string, bool)? inactive = null;
        using var cmd = ctx.Command($"SELECT id, name, is_active FROM {table} ORDER BY id");
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var row = (r.GetInt64(0), r.GetString(1), r.GetInt64(2) == 1);
            if (!string.Equals(row.Item2.Trim(), wanted, StringComparison.OrdinalIgnoreCase)) continue;
            if (row.Item3) return row;
            inactive ??= row;
        }
        return inactive;
    }

    /// <summary>Checks that a date argument, when present, is a real calendar date.</summary>
    public static void CheckDate(PlanStepDocument step, JsonObject args, string field, string label, List<PlanError> errors)
    {
        var text = Str(args, field);
        if (text is not null && !DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            errors.Add(Error(step, field, "business.invalid_date", $"{label} informada não é uma data válida."));
    }

    /// <summary>Resolves the <c>lines</c> argument: every SKU must exist and appear once. Errors are appended to <paramref name="errors"/>.</summary>
    public static List<OrderLine> ReadLines(EngineContext ctx, PlanStepDocument step, JsonObject args, List<PlanError> errors)
    {
        var lines = new List<OrderLine>();
        if (args["lines"] is not JsonArray array) return lines;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in array)
        {
            var sku = node is JsonObject o ? Str(o, "sku") : null;
            var qty = node is JsonObject q ? Int(q, "qty") ?? 0 : 0;
            if (sku is null || qty < 1)
            {
                errors.Add(Error(step, "lines", "business.invalid_line", "cada linha precisa de um SKU e de uma quantidade maior que zero."));
                continue;
            }
            if (!seen.Add(sku))
            {
                errors.Add(Error(step, "lines", "business.duplicate_sku", $"o SKU {sku} aparece em mais de uma linha; some as quantidades em uma só."));
                continue;
            }
            var product = FindProduct(ctx, sku);
            if (product is null)
            {
                errors.Add(Error(step, "lines", "business.unknown_sku", $"o SKU {sku} não existe no cadastro."));
                continue;
            }
            lines.Add(new OrderLine(product, qty));
        }
        return lines;
    }

    public static void InsertLines(EngineContext ctx, string table, string orderColumn, string qtyColumn, long orderId, List<OrderLine> lines)
    {
        // table and columns are constants of the callers.
        foreach (var line in lines)
        {
            using var cmd = ctx.Command($"INSERT INTO {table} ({orderColumn}, product_id, {qtyColumn}) VALUES ($o, $p, $q)");
            cmd.Parameters.AddWithValue("$o", orderId);
            cmd.Parameters.AddWithValue("$p", line.Product.Id);
            cmd.Parameters.AddWithValue("$q", line.Quantity);
            cmd.ExecuteNonQuery();
        }
    }

    public static string Describe(int lineCount, long units) =>
        $"{lineCount} {(lineCount == 1 ? "linha" : "linhas")}, {units} {(units == 1 ? "unidade" : "unidades")}";
}
