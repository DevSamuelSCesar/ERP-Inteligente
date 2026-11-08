using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using static erp_inteligente.Core.Engine.ReservationStore;
using static erp_inteligente.Core.Engine.StockStore;

namespace erp_inteligente.Core.Engine;

/// <summary>reserve_stock: reserves available stock for an outbound order, FEFO, never above what its lines still need.</summary>
internal sealed class ReserveStockHandler : IOperationHandler
{
    private sealed record Allocation(OrderLineRow Line, long BalanceId, string Location, string? Lot, int Quantity);

    private sealed record Plan(OutboundOrderRow Order, List<Allocation> Allocations);

    public string Op => "reserve_stock";

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
        foreach (var a in plan.Allocations)
        {
            InsertReservation(ctx, a.Line.Id, a.BalanceId, a.Quantity, "reserved");
            AddReserved(ctx, a.BalanceId, a.Quantity);
        }
        SetOrderStatus(ctx, plan.Order.Id, "reserved");
        foreach (var group in plan.Allocations.GroupBy(a => a.Line.Sku))
        {
            var parts = group.Select(a => $"{a.Quantity} em {a.Location}" + (a.Lot is null ? "" : $" (lote {a.Lot})"));
            ctx.Diff.AddUpdated("Reserva de estoque", plan.Order.Number, $"{group.Key}: {string.Join(", ", parts)}");
        }
        return plan.Order.Number;
    }

    private static Plan? Resolve(EngineContext ctx, PlanStepDocument step, JsonObject args, List<PlanError> errors)
    {
        var number = Str(args, "order_number");
        var order = FindOutbound(ctx, number);
        if (order is null)
        {
            errors.Add(UnknownOrder(step, number));
            return null;
        }
        if (CheckStatus(step, order, "reservar estoque", "open", "reserved") is { } status)
        {
            errors.Add(status);
            return null;
        }

        var lines = Lines(ctx, order.Id);
        var before = errors.Count;
        var requests = ReadRequests(step, args, errors);
        var wanted = new List<(OrderLineRow Line, int Qty, string? Location, string? Lot)>();
        if (requests is null)
        {
            wanted.AddRange(lines.Where(l => l.Remaining > 0).Select(l => (l, l.Remaining, (string?)null, (string?)null)));
            if (wanted.Count == 0)
                errors.Add(Error(step, "order_number", "business.nothing_to_reserve", $"todas as linhas do pedido {order.Number} já estão reservadas."));
        }
        else
        {
            foreach (var request in requests)
            {
                var line = lines.Find(l => string.Equals(l.Sku, request.Sku, StringComparison.Ordinal));
                if (line is null)
                {
                    errors.Add(Error(step, "lines", "business.sku_not_in_order", $"o pedido {order.Number} não tem o SKU {request.Sku}."));
                    continue;
                }
                var qty = request.Quantity ?? 0;
                if (qty < 1)
                    errors.Add(Error(step, "lines", "business.invalid_qty", $"informe a quantidade a reservar de {line.Sku}."));
                else if (qty > line.Remaining)
                    errors.Add(Error(step, "lines", "business.over_reservation",
                        $"o pedido {order.Number} pede {line.Ordered} de {line.Sku} e já tem {line.Held} reservado; não é possível reservar mais {qty} (faltam {line.Remaining})."));
                else
                    wanted.Add((line, qty, request.Location, request.Lot));
            }
        }
        if (errors.Count > before) return null;

        var allocations = new List<Allocation>();
        var shortfalls = new List<string>();
        foreach (var (line, qty, location, lot) in wanted)
        {
            if (location is not null && FindLocation(ctx, location) is null)
            {
                errors.Add(Error(step, "lines", "business.unknown_location", UnknownLocation(ctx, location)));
                continue;
            }
            var left = qty;
            foreach (var candidate in Candidates(ctx, line.ProductId, location, lot))
            {
                if (left == 0) break;
                var take = Math.Min(left, candidate.Available);
                allocations.Add(new Allocation(line, candidate.BalanceId, candidate.Location, candidate.Lot, take));
                left -= take;
            }
            if (left > 0)
                shortfalls.Add($"{left} de {line.Sku}");
        }
        if (shortfalls.Count > 0)
            errors.Add(Error(step, "lines", "business.insufficient_stock",
                $"estoque disponível insuficiente para reservar o pedido {order.Number}: faltam {string.Join(", ", shortfalls)}. Nada foi reservado."));
        return errors.Count > before ? null : new Plan(order, allocations);
    }

    private sealed record Candidate(long BalanceId, string Location, string? Lot, int Available);

    // FEFO: earliest expiry first (no expiry last), then location code. Dock locations only when asked for explicitly.
    private static List<Candidate> Candidates(EngineContext ctx, long productId, string? location, string? lot)
    {
        using var cmd = ctx.Command(
            "SELECT b.id, loc.code, t.lot_code, b.quantity - b.reserved_qty FROM stock_balances b " +
            "JOIN locations loc ON loc.id = b.location_id JOIN zones z ON z.id = loc.zone_id LEFT JOIN lots t ON t.id = b.lot_id " +
            "WHERE b.product_id = $p AND b.warehouse_id = $wh AND b.quantity - b.reserved_qty > 0 " +
            "AND loc.is_active = 1 AND z.is_active = 1 AND z.warehouse_id = $wh " +
            "AND (($loc IS NULL AND z.code <> $dock) OR loc.code = $loc) AND ($lot IS NULL OR t.lot_code = $lot) " +
            "ORDER BY t.expires_on IS NULL, t.expires_on, loc.code, b.id");
        cmd.Parameters.AddWithValue("$p", productId);
        cmd.Parameters.AddWithValue("$wh", ctx.WarehouseId);
        cmd.Parameters.AddWithValue("$loc", (object?)location ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$lot", (object?)lot ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$dock", DockZone);
        var rows = new List<Candidate>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            rows.Add(new Candidate(r.GetInt64(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2),
                Convert.ToInt32(r.GetInt64(3), CultureInfo.InvariantCulture)));
        return rows;
    }
}
