using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using static erp_inteligente.Core.Engine.OrderStore;
using static erp_inteligente.Core.Engine.StockStore;

namespace erp_inteligente.Core.Engine;

/// <summary>create_inbound_order: an expected delivery from an active supplier; the document is unique per warehouse.</summary>
internal sealed class CreateInboundOrderHandler : IOperationHandler
{
    public string Op => "create_inbound_order";

    public IReadOnlyList<PlanError> Validate(EngineContext ctx, PlanStepDocument step, JsonObject args)
    {
        var errors = new List<PlanError>();
        Check(ctx, step, args, errors);
        return errors;
    }

    public string Apply(EngineContext ctx, PlanStepDocument step, JsonObject args)
    {
        var errors = new List<PlanError>();
        var (supplier, lines) = Check(ctx, step, args, errors);
        if (errors.Count > 0 || supplier is null) throw new InvalidOperationException("Step applied without validation.");
        var document = Str(args, "document_number")!;

        long orderId;
        using (var insert = ctx.Command(
            "INSERT INTO inbound_orders (warehouse_id, supplier_id, document_number, status, expected_on) " +
            "VALUES ($wh, $supplier, $doc, 'expected', $on) RETURNING id"))
        {
            insert.Parameters.AddWithValue("$wh", ctx.WarehouseId);
            insert.Parameters.AddWithValue("$supplier", supplier.Value.Id);
            insert.Parameters.AddWithValue("$doc", document);
            insert.Parameters.AddWithValue("$on", (object?)Str(args, "expected_on") ?? DBNull.Value);
            orderId = Convert.ToInt64(insert.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        }
        InsertLines(ctx, "inbound_order_lines", "inbound_order_id", "expected_qty", orderId, lines);
        ctx.Diff.AddCreated("Pedido de entrada", document, $"{supplier.Value.Name}: {Describe(lines.Count, lines.Sum(l => (long)l.Quantity))}");
        return document;
    }

    private static ((long Id, string Name)? Supplier, List<OrderLine> Lines) Check(
        EngineContext ctx, PlanStepDocument step, JsonObject args, List<PlanError> errors)
    {
        (long, string)? supplier = null;
        var name = Str(args, "supplier");
        var found = FindPartner(ctx, "suppliers", name);
        if (found is null)
            errors.Add(Error(step, "supplier", "business.unknown_supplier", $"o fornecedor {name} não existe no cadastro."));
        else if (!found.Value.Active)
            errors.Add(Error(step, "supplier", "business.inactive_supplier", $"o fornecedor {found.Value.Name} está inativo."));
        else
            supplier = (found.Value.Id, found.Value.Name);

        var document = Str(args, "document_number");
        using (var cmd = ctx.Command("SELECT 1 FROM inbound_orders WHERE document_number = $d AND warehouse_id = $wh"))
        {
            cmd.Parameters.AddWithValue("$d", document);
            cmd.Parameters.AddWithValue("$wh", ctx.WarehouseId);
            if (cmd.ExecuteScalar() is not null)
                errors.Add(Error(step, "document_number", "business.duplicate_document", $"já existe um pedido de entrada com o documento {document} neste armazém."));
        }
        CheckDate(step, args, "expected_on", "a data prevista", errors);
        var lines = ReadLines(ctx, step, args, errors);
        return (supplier, lines);
    }
}
