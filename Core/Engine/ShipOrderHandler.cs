using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using static erp_inteligente.Core.Engine.ReservationStore;
using static erp_inteligente.Core.Engine.StockStore;

namespace erp_inteligente.Core.Engine;

/// <summary>ship_order: ships a fully picked order, taking its quantity and reservation out of the dock balances.</summary>
internal sealed class ShipOrderHandler : IOperationHandler
{
    private sealed record Plan(OutboundOrderRow Order, List<OrderLineRow> Lines, List<ReservationRow> Picked);

    public string Op => "ship_order";

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
        foreach (var row in plan.Picked)
        {
            var line = plan.Lines.First(l => l.Id == row.LineId);
            var before = BalanceQuantity(ctx, row.BalanceId);
            using (var update = ctx.Command(
                "UPDATE stock_balances SET quantity = quantity - $q, reserved_qty = reserved_qty - $q, updated_at = $now WHERE id = $id"))
            {
                update.Parameters.AddWithValue("$q", row.Quantity);
                update.Parameters.AddWithValue("$now", ctx.Timestamp);
                update.Parameters.AddWithValue("$id", row.BalanceId);
                update.ExecuteNonQuery();
            }
            InsertMovement(ctx, row.ProductId, row.LotId, row.LocationId, null, "ship", -row.Quantity, $"Pedido {plan.Order.Number}");
            SetState(ctx, row.Id, "shipped");
            ctx.Diff.AddDirectBalance(line.Sku, row.LocationCode, row.LotCode, before, before - row.Quantity, line.Unit);
        }
        SetOrderStatus(ctx, plan.Order.Id, "shipped");
        ctx.Diff.AddUpdated("Pedido expedido", plan.Order.Number,
            $"{plan.Lines.Count} {(plan.Lines.Count == 1 ? "linha" : "linhas")}, {plan.Picked.Sum(r => (long)r.Quantity)} unidades baixadas do estoque");
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
        if (CheckStatus(step, order, "expedir", "picking") is { } status)
        {
            errors.Add(status);
            return null;
        }

        var lines = Lines(ctx, order.Id);
        var picked = Reservations(ctx, order.Id, "picked");
        var missing = new List<string>();
        foreach (var line in lines)
        {
            var held = picked.Where(r => r.LineId == line.Id).Sum(r => r.Quantity);
            if (line.Picked < line.Ordered || held < line.Ordered)
                missing.Add($"{line.Ordered - Math.Min(line.Picked, held)} de {line.Sku}");
        }
        if (missing.Count > 0)
        {
            errors.Add(Error(step, "order_number", "business.not_fully_picked",
                $"o pedido {order.Number} ainda não foi totalmente separado: faltam {string.Join(", ", missing)}."));
            return null;
        }
        return new Plan(order, lines, picked);
    }
}
