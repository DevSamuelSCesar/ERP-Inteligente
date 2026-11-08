using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using static erp_inteligente.Core.Engine.ReservationStore;
using static erp_inteligente.Core.Engine.StockStore;

namespace erp_inteligente.Core.Engine;

/// <summary>release_reservation: gives not-yet-picked reservations of an order back to the available stock.</summary>
internal sealed class ReleaseReservationHandler : IOperationHandler
{
    private sealed record Release(OrderLineRow Line, ReservationRow Row, int Quantity);

    private sealed record Plan(OutboundOrderRow Order, List<Release> Releases);

    public string Op => "release_reservation";

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
        foreach (var r in plan.Releases)
        {
            ReleaseRow(ctx, r.Row, r.Quantity);
            AddReserved(ctx, r.Row.BalanceId, -r.Quantity);
        }
        RefreshStatus(ctx, plan.Order.Id);
        foreach (var group in plan.Releases.GroupBy(r => r.Line.Sku))
            ctx.Diff.AddUpdated("Reserva liberada", plan.Order.Number, $"{group.Key}: {group.Sum(r => r.Quantity)}");
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
        if (CheckStatus(step, order, "liberar reservas", "reserved", "picking") is { } status)
        {
            errors.Add(status);
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
            errors.Add(Error(step, "order_number", "business.nothing_to_release", $"o pedido {order.Number} não tem reservas a liberar."));
            return null;
        }

        var releases = new List<Release>();
        foreach (var (line, qty) in wanted)
        {
            var rows = reserved.Where(r => r.LineId == line.Id).ToList();
            var available = rows.Sum(r => r.Quantity);
            if (available == 0)
            {
                errors.Add(Error(step, "lines", "business.nothing_to_release", $"o SKU {line.Sku} não tem reserva a liberar no pedido {order.Number} (o que já foi separado não é liberado aqui)."));
                continue;
            }
            var left = qty ?? available;
            if (left > available)
            {
                errors.Add(Error(step, "lines", "business.over_release", $"o SKU {line.Sku} tem {available} reservado no pedido {order.Number}; não é possível liberar {left}."));
                continue;
            }
            // Newest reservations go first, so the earliest-expiring stock (reserved first) stays held.
            foreach (var row in Enumerable.Reverse(rows))
            {
                if (left == 0) break;
                var take = Math.Min(left, row.Quantity);
                releases.Add(new Release(line, row, take));
                left -= take;
            }
        }
        return errors.Count > before ? null : new Plan(order, releases);
    }
}
