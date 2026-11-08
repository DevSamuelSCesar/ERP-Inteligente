using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using static erp_inteligente.Core.Engine.StockStore;
using static erp_inteligente.Core.Engine.StructureStore;

namespace erp_inteligente.Core.Engine;

/// <summary>
/// update_product: changes data of an existing product. The unit cost is encrypted like in create_product and never
/// appears in the diff; turning expiry tracking on is refused while there is balance without a lot.
/// </summary>
internal sealed class UpdateProductHandler : IOperationHandler
{
    private sealed record Current(string Name, string? Description, string Unit, double? WeightKg, bool TracksExpiry);

    private sealed record Change(long Id, string Sku, IReadOnlyList<(string Column, string Parameter, object Value)> Sets,
        decimal? UnitCost, string Description);

    public string Op => "update_product";

    public IReadOnlyList<PlanError> Validate(EngineContext ctx, PlanStepDocument step, JsonObject args)
    {
        var errors = new List<PlanError>();
        Resolve(ctx, step, args, errors);
        return errors;
    }

    public string Apply(EngineContext ctx, PlanStepDocument step, JsonObject args)
    {
        var errors = new List<PlanError>();
        var change = Resolve(ctx, step, args, errors) ?? throw new InvalidOperationException("Step applied without validation.");
        if (change.Sets.Count > 0)
        {
            // Column names come from the fixed list in Resolve; values are always parameters.
            var assignments = string.Join(", ", change.Sets.Select(s => $"{s.Column} = {s.Parameter}"));
            using var update = ctx.Command($"UPDATE products SET {assignments} WHERE id = $id");
            foreach (var (_, parameter, value) in change.Sets)
                update.Parameters.AddWithValue(parameter, value);
            update.Parameters.AddWithValue("$id", change.Id);
            update.ExecuteNonQuery();
        }
        if (change.UnitCost is decimal cost)
            StoreUnitCost(ctx, change.Id, cost);
        ctx.Diff.AddUpdated("Produto atualizado", change.Sku, change.Description);
        return change.Sku;
    }

    private static Change? Resolve(EngineContext ctx, PlanStepDocument step, JsonObject args, List<PlanError> errors)
    {
        var sku = Str(args, "sku");
        var product = FindProduct(ctx, sku);
        if (product is null)
        {
            errors.Add(Error(step, "sku", "business.unknown_sku", $"o SKU {sku} não existe no cadastro."));
            return null;
        }
        var current = ReadCurrent(ctx, product.Id);

        var sets = new List<(string, string, object)>();
        var changes = new List<string>();

        if (Str(args, "name")?.Trim() is { } name)
        {
            if (name.Length == 0)
            {
                errors.Add(Error(step, "name", "business.invalid_name", "informe o nome do produto."));
                return null;
            }
            if (!string.Equals(name, current.Name, StringComparison.Ordinal))
            {
                sets.Add(("name", "$name", name));
                changes.Add($"nome {current.Name} → {name}");
            }
        }
        if (Str(args, "description") is { } description)
        {
            var text = description.Trim();
            if (!string.Equals(text, current.Description ?? string.Empty, StringComparison.Ordinal))
            {
                sets.Add(("description", "$description", text.Length == 0 ? DBNull.Value : text));
                changes.Add("descrição alterada");
            }
        }
        if (Str(args, "unit") is { } unit && !string.Equals(unit, current.Unit, StringComparison.Ordinal))
        {
            sets.Add(("unit", "$unit", unit));
            changes.Add($"unidade {current.Unit} → {unit}");
        }
        if (Dbl(args, "weight_kg") is double weight && current.WeightKg != weight)
        {
            sets.Add(("weight_kg", "$weight", weight));
            changes.Add(current.WeightKg is double old
                ? $"peso {old.ToString(CultureInfo.InvariantCulture)} → {weight.ToString(CultureInfo.InvariantCulture)} kg"
                : $"peso definido em {weight.ToString(CultureInfo.InvariantCulture)} kg");
        }
        if (Bool(args, "tracks_expiry") is bool tracks && tracks != current.TracksExpiry)
        {
            if (tracks)
            {
                var unlotted = QuantityWithoutLot(ctx, product.Id);
                if (unlotted > 0)
                {
                    errors.Add(Error(step, "tracks_expiry", "business.balance_without_lot",
                        $"o produto {product.Sku} tem {N(unlotted)} {current.Unit} em estoque sem lote; " +
                        "regularize o saldo antes de controlar a validade."));
                    return null;
                }
            }
            sets.Add(("tracks_expiry", "$expiry", tracks ? 1 : 0));
            changes.Add($"controle de validade {(current.TracksExpiry ? "sim" : "não")} → {(tracks ? "sim" : "não")}");
        }
        var cost = Dec(args, "unit_cost");
        if (cost is not null)
            changes.Add("custo unitário alterado");

        if (changes.Count == 0)
        {
            errors.Add(Error(step, null, "business.nothing_to_change",
                HasAnyField(args) ? $"o produto {product.Sku} já está assim; não há o que alterar." : "informe ao menos um dado do produto a alterar."));
            return null;
        }
        return new Change(product.Id, product.Sku, sets.ConvertAll(s => (s.Item1, s.Item2, s.Item3)), cost, string.Join("; ", changes));
    }

    private static bool HasAnyField(JsonObject args) =>
        args["name"] is not null || args["description"] is not null || args["unit"] is not null || args["weight_kg"] is not null
        || args["tracks_expiry"] is not null || args["unit_cost"] is not null;

    private static Current ReadCurrent(EngineContext ctx, long id)
    {
        using var cmd = ctx.Command("SELECT name, description, unit, weight_kg, tracks_expiry FROM products WHERE id = $id");
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) throw new InvalidOperationException("Product vanished inside the transaction.");
        return new Current(r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.GetString(2),
            r.IsDBNull(3) ? null : r.GetDouble(3), r.GetInt64(4) == 1);
    }

    /// <summary>Quantity of the product held without a lot, in any warehouse (the catalog is shared by all of them).</summary>
    private static long QuantityWithoutLot(EngineContext ctx, long productId)
    {
        using var cmd = ctx.Command(
            "SELECT COALESCE(SUM(quantity), 0) FROM stock_balances WHERE product_id = $p AND lot_id IS NULL AND quantity > 0");
        cmd.Parameters.AddWithValue("$p", productId);
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }
}
