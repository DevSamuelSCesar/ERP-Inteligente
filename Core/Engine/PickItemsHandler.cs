using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using static erp_inteligente.Core.Engine.ReservationStore;
using static erp_inteligente.Core.Engine.StockStore;

namespace erp_inteligente.Core.Engine;

/// <summary>pick_items: moves reserved stock of an order to a dock location, where it stays reserved for the order.</summary>
internal sealed class PickItemsHandler : IOperationHandler
{
    private sealed record Pickup(OrderLineRow Line, ReservationRow Row, int Quantity);

    private sealed record Plan(OutboundOrderRow Order, LocationRow Dock, List<Pickup> Pickups);

    public string Op => "pick_items";

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
        foreach (var pickup in plan.Pickups)
        {
            var row = pickup.Row;
            var line = pickup.Line;
            var lot = row.LotCode;
            long dockBalance = row.BalanceId;
            if (row.LocationId != plan.Dock.Id)
            {
                var sourceBefore = BalanceQuantity(ctx, row.BalanceId);
                using (var source = ctx.Command(
                    "UPDATE stock_balances SET quantity = quantity - $q, reserved_qty = reserved_qty - $q, updated_at = $now WHERE id = $id"))
                {
                    source.Parameters.AddWithValue("$q", pickup.Quantity);
                    source.Parameters.AddWithValue("$now", ctx.Timestamp);
                    source.Parameters.AddWithValue("$id", row.BalanceId);
                    source.ExecuteNonQuery();
                }
                var (dockBefore, dockAfter) = AddToBalance(ctx, row.ProductId, plan.Dock.Id, row.LotId, pickup.Quantity);
                dockBalance = BalanceId(ctx, row.ProductId, plan.Dock.Id, row.LotId)
                    ?? throw new InvalidOperationException("Dock balance missing after pick.");
                AddReserved(ctx, dockBalance, pickup.Quantity);
                InsertMovement(ctx, row.ProductId, row.LotId, row.LocationId, plan.Dock.Id, "pick", pickup.Quantity, null);
                ctx.Diff.AddMoveBalance(line.Sku, row.LocationCode, lot, sourceBefore, sourceBefore - pickup.Quantity, line.Unit);
                ctx.Diff.AddMoveBalance(line.Sku, plan.Dock.Code, lot, dockBefore, dockAfter, line.Unit);
                ctx.Diff.AddMove(line.Sku, lot, row.LocationCode, plan.Dock.Code, pickup.Quantity, line.Unit);
            }
            PickRow(ctx, row, pickup.Quantity, dockBalance);
            AddPicked(ctx, line.Id, pickup.Quantity);
        }
        SetOrderStatus(ctx, plan.Order.Id, "picking");
        foreach (var group in plan.Pickups.GroupBy(p => p.Line.Sku))
            ctx.Diff.AddUpdated("Separação de pedido", plan.Order.Number, $"{group.Key}: {group.Sum(p => p.Quantity)} em {plan.Dock.Code}");
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
        if (CheckStatus(step, order, "separar itens", "reserved", "picking") is { } status)
        {
            errors.Add(status);
            return null;
        }

        var dockCode = Str(args, "to");
        var dock = FindLocation(ctx, dockCode);
        if (dock is null)
        {
            errors.Add(Error(step, "to", "business.unknown_location", UnknownLocation(ctx, dockCode)));
            return null;
        }
        if (!string.Equals(dock.ZoneCode, DockZone, StringComparison.Ordinal))
        {
            errors.Add(Error(step, "to", "business.not_dock", $"a separação só pode ser levada a uma doca, e {dock.Code} não é uma doca."));
            return null;
        }

        var lines = Lines(ctx, order.Id);
        var reserved = Reservations(ctx, order.Id, "reserved");
        var before = errors.Count;
        var requests = ReadRequests(step, args, errors);
        var wanted = new List<(OrderLineRow Line, int? Qty)>();
        if (requests is null)
            wanted.AddRange(lines.Where(l => reserved.Any(r => r.LineId == l.Id)).Select(l => (l, (int?)null)));
        else
            foreach (var request in requests)
            {
                var line = lines.Find(l => string.Equals(l.Sku, request.Sku, StringComparison.Ordinal));
                if (line is null)
                    errors.Add(Error(step, "lines", "business.sku_not_in_order", $"o pedido {order.Number} não tem o SKU {request.Sku}."));
                else
                    wanted.Add((line, request.Quantity));
            }
        if (errors.Count > before) return null;
        if (wanted.Count == 0)
        {
            errors.Add(Error(step, "order_number", "business.nothing_to_pick", $"o pedido {order.Number} não tem estoque reservado para separar."));
            return null;
        }

        var pickups = new List<Pickup>();
        foreach (var (line, qty) in wanted)
        {
            var rows = reserved.Where(r => r.LineId == line.Id).ToList();
            var available = rows.Sum(r => r.Quantity);
            if (available == 0)
            {
                errors.Add(Error(step, "lines", "business.nothing_to_pick", $"o SKU {line.Sku} não tem estoque reservado para separar no pedido {order.Number}."));
                continue;
            }
            var left = qty ?? available;
            if (left > available)
            {
                errors.Add(Error(step, "lines", "business.over_pick", $"o SKU {line.Sku} tem {available} reservado no pedido {order.Number}; não é possível separar {left}."));
                continue;
            }
            foreach (var row in rows)
            {
                if (left == 0) break;
                var take = Math.Min(left, row.Quantity);
                pickups.Add(new Pickup(line, row, take));
                left -= take;
            }
        }
        if (errors.Count > before) return null;

        var incoming = pickups.Where(p => p.Row.LocationId != dock.Id).Sum(p => p.Quantity);
        var occupied = Occupied(ctx, dock.Id);
        if (occupied + incoming > dock.Capacity)
        {
            errors.Add(Error(step, "to", "business.capacity", OutOfCapacity(dock, occupied, incoming)));
            return null;
        }
        return new Plan(order, dock, pickups);
    }
}
