using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json.Nodes;
using static erp_inteligente.Core.Engine.StockStore;

namespace erp_inteligente.Core.Engine;

/// <summary>receive_goods: goods enter through a dock location, optionally against an inbound order.</summary>
internal sealed class ReceiveGoodsHandler : IOperationHandler
{
    private const string DockZone = "DOCA";

    private sealed record Plan(ProductRow Product, LocationRow Location, int Qty, string? LotCode, string? ExpiresOn, LotRow? Lot,
        long? OrderId, long? LineId);

    public string Op => "receive_goods";

    public IReadOnlyList<PlanError> Validate(EngineContext ctx, PlanStepDocument step, JsonObject args)
    {
        var errors = new List<PlanError>();
        Resolve(ctx, step, args, errors);
        return errors;
    }

    public string Apply(EngineContext ctx, PlanStepDocument step, JsonObject args)
    {
        var errors = new List<PlanError>();
        var plan = Resolve(ctx, step, args, errors) ?? throw new InvalidOperationException("Step applied without validation.");
        var lotId = plan.Lot?.Id;
        if (plan.LotCode is not null && lotId is null)
            lotId = InsertLot(ctx, plan.Product.Id, plan.LotCode, plan.ExpiresOn);

        var (before, after) = AddToBalance(ctx, plan.Product.Id, plan.Location.Id, lotId, plan.Qty);
        InsertMovement(ctx, plan.Product.Id, lotId, null, plan.Location.Id, "receive", plan.Qty, null);
        ctx.Diff.AddDirectBalance(plan.Product.Sku, plan.Location.Code, plan.LotCode, before, after, plan.Product.Unit);
        ctx.Diff.AddReceipt(plan.Product.Sku, plan.Qty, plan.Product.Unit);

        if (plan.OrderId is long orderId && plan.LineId is long lineId)
        {
            using (var line = ctx.Command("UPDATE inbound_order_lines SET received_qty = received_qty + $q WHERE id = $id"))
            {
                line.Parameters.AddWithValue("$q", plan.Qty);
                line.Parameters.AddWithValue("$id", lineId);
                line.ExecuteNonQuery();
            }
            using var status = ctx.Command(
                "UPDATE inbound_orders SET status = CASE WHEN EXISTS (SELECT 1 FROM inbound_order_lines " +
                "WHERE inbound_order_id = $o AND received_qty < expected_qty) THEN 'receiving' ELSE 'received' END WHERE id = $o");
            status.Parameters.AddWithValue("$o", orderId);
            status.ExecuteNonQuery();
        }
        return plan.Product.Sku;
    }

    private static Plan? Resolve(EngineContext ctx, PlanStepDocument step, JsonObject args, List<PlanError> errors)
    {
        var sku = Str(args, "sku");
        var product = FindProduct(ctx, sku);
        if (product is null)
        {
            errors.Add(Error(step, "sku", "business.unknown_sku", $"o SKU {sku} não existe no cadastro."));
            return null;
        }
        var locCode = Str(args, "location");
        var location = FindLocation(ctx, locCode);
        if (location is null)
        {
            errors.Add(Error(step, "location", "business.unknown_location", UnknownLocation(ctx, locCode)));
            return null;
        }
        var qty = Int(args, "qty") ?? 0;
        if (qty < 1)
        {
            errors.Add(Error(step, "qty", "business.invalid_qty", "a quantidade recebida deve ser maior que zero."));
            return null;
        }

        var before = errors.Count;
        if (!string.Equals(location.ZoneCode, DockZone, StringComparison.Ordinal))
            errors.Add(Error(step, "location", "business.not_dock", $"o recebimento só pode ser feito em uma doca, e {location.Code} não é uma doca."));

        var lotCode = Str(args, "lot");
        var expiresOn = Str(args, "expires_on");
        if (product.TracksExpiry && (lotCode is null || expiresOn is null))
            errors.Add(Error(step, lotCode is null ? "lot" : "expires_on", "business.expiry_required",
                $"o produto {product.Sku} controla validade: informe o lote e a validade."));
        if (expiresOn is not null && lotCode is null && !product.TracksExpiry)
            errors.Add(Error(step, "lot", "business.lot_required", "a validade só pode ser informada junto com o lote."));
        if (expiresOn is not null && !DateOnly.TryParseExact(expiresOn, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            errors.Add(Error(step, "expires_on", "business.invalid_date", "a validade informada não é uma data válida."));

        LotRow? lot = null;
        if (lotCode is not null)
        {
            lot = FindLot(ctx, product.Id, lotCode);
            if (lot is not null && expiresOn is not null && !string.Equals(lot.ExpiresOn, expiresOn, StringComparison.Ordinal))
                errors.Add(Error(step, "expires_on", "business.lot_expiry_mismatch",
                    $"o lote {lotCode} já existe com outra validade ({lot.ExpiresOn ?? "sem validade"})."));
        }

        var occupied = Occupied(ctx, location.Id);
        if (occupied + qty > location.Capacity)
            errors.Add(Error(step, "qty", "business.capacity", OutOfCapacity(location, occupied, qty)));

        long? orderId = null, lineId = null;
        var orderNumber = Str(args, "inbound_order");
        if (orderNumber is not null)
            CheckOrder(ctx, step, product, orderNumber, qty, errors, out orderId, out lineId);

        return errors.Count > before ? null : new Plan(product, location, qty, lotCode, expiresOn, lot, orderId, lineId);
    }

    private static void CheckOrder(
        EngineContext ctx, PlanStepDocument step, ProductRow product, string number, int qty, List<PlanError> errors,
        out long? orderId, out long? lineId)
    {
        orderId = lineId = null;
        string status;
        using (var cmd = ctx.Command("SELECT id, status FROM inbound_orders WHERE document_number = $n AND warehouse_id = $wh"))
        {
            cmd.Parameters.AddWithValue("$n", number);
            cmd.Parameters.AddWithValue("$wh", ctx.WarehouseId);
            using var r = cmd.ExecuteReader();
            if (!r.Read())
            {
                errors.Add(Error(step, "inbound_order", "business.unknown_order", $"o pedido de entrada {number} não existe neste armazém."));
                return;
            }
            orderId = r.GetInt64(0);
            status = r.GetString(1);
        }
        if (status is "cancelled" or "received")
        {
            errors.Add(Error(step, "inbound_order", "business.order_closed",
                $"o pedido de entrada {number} está {(status == "received" ? "totalmente recebido" : "cancelado")}."));
            orderId = null;
            return;
        }
        using var line = ctx.Command(
            "SELECT id, expected_qty, received_qty FROM inbound_order_lines WHERE inbound_order_id = $o AND product_id = $p " +
            "ORDER BY id LIMIT 1");
        line.Parameters.AddWithValue("$o", orderId!.Value);
        line.Parameters.AddWithValue("$p", product.Id);
        using var lr = line.ExecuteReader();
        if (!lr.Read())
        {
            errors.Add(Error(step, "inbound_order", "business.order_no_line", $"o pedido de entrada {number} não prevê o SKU {product.Sku}."));
            orderId = null;
            return;
        }
        var expected = lr.GetInt64(1);
        var received = lr.GetInt64(2);
        if (received + qty > expected)
        {
            errors.Add(Error(step, "qty", "business.over_receipt",
                $"o pedido {number} prevê {expected} de {product.Sku} e já recebeu {received}; não é possível receber mais {qty}."));
            orderId = null;
            return;
        }
        lineId = lr.GetInt64(0);
    }
}
