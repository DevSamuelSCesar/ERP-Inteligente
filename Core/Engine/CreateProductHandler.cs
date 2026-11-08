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
            insert.Parameters.AddWithValue("$weight", Dbl(args, "weight_kg") is double w ? w : DBNull.Value);
            insert.Parameters.AddWithValue("$exp", Bool(args, "tracks_expiry") == true ? 1 : 0);
            id = Convert.ToInt64(insert.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        if (Dec(args, "unit_cost") is decimal cost)
            StoreUnitCost(ctx, id, cost);
        ctx.Diff.AddCreated("Produto", sku, name);
        return sku;
    }
}
