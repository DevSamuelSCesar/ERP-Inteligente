using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using static erp_inteligente.Core.Engine.ReservationStore;
using static erp_inteligente.Core.Engine.StockStore;

namespace erp_inteligente.Core.Engine;

/// <summary>cancel_outbound_order: cancels an order not yet shipped and releases all of its reservations.</summary>
internal sealed class CancelOutboundOrderHandler : IOperationHandler
{
    private sealed record Plan(OutboundOrderRow Order, List<ReservationRow> Open);

    public string Op => "cancel_outbound_order";

    public IReadOnlyList<PlanError> Validate(EngineContext ctx, PlanStepDocument step, JsonObject args)
    {
        var errors = new List<PlanError>();
        Resolve(ctx, step, args, errors);
        if (string.IsNullOrWhiteSpace(Str(args, "reason")))
            errors.Add(Error(step, "reason", "business.invalid_reason", "informe o motivo do cancelamento."));
        return errors;
    }

    public string Apply(EngineContext ctx, PlanStepDocument step, JsonObject args)
    {
        var errors = new List<PlanError>();
        var plan = Resolve(ctx, step, args, errors) ?? throw new InvalidOperationException("Step applied without validation.");
        foreach (var row in plan.Open)
        {
            SetState(ctx, row.Id, "released");
            AddReserved(ctx, row.BalanceId, -row.Quantity);
        }
        SetOrderStatus(ctx, plan.Order.Id, "cancelled");

        var text = $"motivo: {Str(args, "reason")!.Trim()}";
        var released = plan.Open.Sum(r => (long)r.Quantity);
        if (released > 0) text += $"; {released} unidades de reserva liberadas";
        var docks = plan.Open.Where(r => r.State == "picked").Select(r => r.LocationCode).Distinct().Order().ToList();
        if (docks.Count > 0) text += $"; o estoque já separado continua em {string.Join(", ", docks)}, sem reserva";
        ctx.Diff.AddUpdated("Pedido de saída cancelado", plan.Order.Number, text);
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
        if (CheckStatus(step, order, "cancelar", "open", "reserved", "picking") is { } status)
        {
            errors.Add(status);
            return null;
        }
        return new Plan(order, Reservations(ctx, order.Id, "reserved", "picked"));
    }
}
