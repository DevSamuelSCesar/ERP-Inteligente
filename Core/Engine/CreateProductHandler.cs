using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json.Nodes;
using static erp_inteligente.Core.Engine.StockStore;

namespace erp_inteligente.Core.Engine;

/// <summary>create_product: adds a catalog product; the unit cost is stored encrypted (column AES-GCM).</summary>
internal sealed class CreateProductHandler : IOperationHandler
{
    public string Op => "create_product";

    public IReadOnlyList<PlanError> Validate(EngineContext ctx, PlanStepDocument step, JsonObject args)
    {
        var sku = Str(args, "sku");
        if (FindProduct(ctx, sku) is not null)
            return [Error(step, "sku", "business.duplicate_sku", $"o SKU {sku} já existe no cadastro.")];
        return [];
    }

    public string Apply(EngineContext ctx, PlanStepDocument step, JsonObject args)
    {
        var sku = Str(args, "sku") ?? throw new InvalidOperationException("Missing sku.");
        var name = Str(args, "name") ?? throw new InvalidOperationException("Missing name.");
        var unit = Str(args, "unit") ?? throw new InvalidOperationException("Missing unit.");
        var description = Str(args, "description");

        long id;
        using (var insert = ctx.Command(
            "INSERT INTO products (sku, name, description, unit, weight_kg, tracks_expiry) " +
            "VALUES ($sku, $name, $desc, $unit, $weight, $exp) RETURNING id"))
        {
            insert.Parameters.AddWithValue("$sku", sku);
            insert.Parameters.AddWithValue("$name", name);
            insert.Parameters.AddWithValue("$desc", (object?)description ?? DBNull.Value);
            insert.Parameters.AddWithValue("$unit", unit);
            insert.Parameters.AddWithValue("$weight", ReadDouble(args, "weight_kg") is double w ? w : DBNull.Value);
            insert.Parameters.AddWithValue("$exp", args["tracks_expiry"] is JsonValue t && t.TryGetValue<bool>(out var b) && b ? 1 : 0);
            id = Convert.ToInt64(insert.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        if (ReadDecimal(args, "unit_cost") is decimal cost)
        {
            var cipher = ctx.Cipher.Encrypt(cost.ToString(CultureInfo.InvariantCulture), "products", "unit_cost_enc", id);
            using var update = ctx.Command("UPDATE products SET unit_cost_enc = $c WHERE id = $id");
            update.Parameters.AddWithValue("$c", cipher);
            update.Parameters.AddWithValue("$id", id);
            update.ExecuteNonQuery();
        }
        ctx.Diff.AddCreated("Produto", sku, name);
        return sku;
    }

    private static double? ReadDouble(JsonObject args, string name) =>
        args[name] is JsonValue v && v.TryGetValue<double>(out var d) ? d : null;

    private static decimal? ReadDecimal(JsonObject args, string name)
    {
        if (args[name] is not JsonValue v) return null;
        if (v.TryGetValue<decimal>(out var m)) return m;
        return v.TryGetValue<double>(out var d) ? (decimal)d : null;
    }
}
