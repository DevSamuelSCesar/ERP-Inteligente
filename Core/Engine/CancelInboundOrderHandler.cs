using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using static erp_inteligente.Core.Engine.StockStore;

namespace erp_inteligente.Core.Engine;

/// <summary>cancel_inbound_order: cancels an order of the session warehouse, only while nothing was received.</summary>
internal sealed class CancelInboundOrderHandler : IOperationHandler
{
    public string Op => "cancel_inbound_order";

    public IReadOnlyList<PlanError> Validate(EngineContext ctx, PlanStepDocument step, JsonObject args)
    {
        var errors = new List<PlanError>();
        Find(ctx, step, args, errors);
        if (string.IsNullOrWhiteSpace(Str(args, "reason")))
            errors.Add(Error(step, "reason", "business.invalid_reason", "informe o motivo do cancelamento."));
        return errors;
    }

    public string Apply(EngineContext ctx, PlanStepDocument step, JsonObject args)
    {
        var errors = new List<PlanError>();
        var id = Find(ctx, step, args, errors);
        if (errors.Count > 0 || id is null) throw new InvalidOperationException("Step applied without validation.");
        var document = Str(args, "document_number")!;
        using var update = ctx.Command("UPDATE inbound_orders SET status = 'cancelled' WHERE id = $id AND warehouse_id = $wh");
        update.Parameters.AddWithValue("$id", id.Value);
        update.Parameters.AddWithValue("$wh", ctx.WarehouseId);
        update.ExecuteNonQuery();
        ctx.Diff.AddUpdated("Pedido de entrada cancelado", document, $"motivo: {Str(args, "reason")!.Trim()}");
        return document;
    }

    private static long? Find(EngineContext ctx, PlanStepDocument step, JsonObject args, List<PlanError> errors)
    {
        var document = Str(args, "document_number");
        long id;
        string status;
        using (var cmd = ctx.Command("SELECT id, status FROM inbound_orders WHERE document_number = $d AND warehouse_id = $wh"))
        {
            cmd.Parameters.AddWithValue("$d", document);
            cmd.Parameters.AddWithValue("$wh", ctx.WarehouseId);
            using var r = cmd.ExecuteReader();
            if (!r.Read())
            {
                errors.Add(Error(step, "document_number", "business.unknown_order", $"o pedido de entrada {document} não existe neste armazém."));
                return null;
            }
            id = r.GetInt64(0);
            status = r.GetString(1);
        }
        var problem = status switch
        {
            "expected" => null,
            "cancelled" => $"o pedido de entrada {document} já está cancelado.",
            "receiving" => $"o pedido de entrada {document} já teve mercadoria recebida e não pode mais ser cancelado; conclua o recebimento ou corrija o estoque com um ajuste.",
            _ => $"o pedido de entrada {document} já foi totalmente recebido e não pode ser cancelado.",
        };
        if (problem is null) return id;
        errors.Add(Error(step, "document_number", "business.order_not_cancellable", problem));
        return null;
    }
}
