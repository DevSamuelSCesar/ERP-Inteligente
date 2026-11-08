using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using static erp_inteligente.Core.Engine.OrderStore;
using static erp_inteligente.Core.Engine.StockStore;

namespace erp_inteligente.Core.Engine;

/// <summary>create_outbound_order: an open order for an active customer; the order number is unique in the whole system.</summary>
internal sealed class CreateOutboundOrderHandler : IOperationHandler
{
    public string Op => "create_outbound_order";

    public IReadOnlyList<PlanError> Validate(EngineContext ctx, PlanStepDocument step, JsonObject args)
    {
        var errors = new List<PlanError>();
        Check(ctx, step, args, errors);
        return errors;
    }

    public string Apply(EngineContext ctx, PlanStepDocument step, JsonObject args)
    {
        var errors = new List<PlanError>();
        var (customer, lines) = Check(ctx, step, args, errors);
        if (errors.Count > 0 || customer is null) throw new InvalidOperationException("Step applied without validation.");
        var number = Str(args, "order_number")!;

        long orderId;
        using (var insert = ctx.Command(
            "INSERT INTO outbound_orders (warehouse_id, customer_id, order_number, status, due_on) " +
            "VALUES ($wh, $customer, $number, 'open', $due) RETURNING id"))
        {
            insert.Parameters.AddWithValue("$wh", ctx.WarehouseId);
            insert.Parameters.AddWithValue("$customer", customer.Value.Id);
            insert.Parameters.AddWithValue("$number", number);
            insert.Parameters.AddWithValue("$due", (object?)Str(args, "due_on") ?? DBNull.Value);
            orderId = Convert.ToInt64(insert.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        }
        InsertLines(ctx, "outbound_order_lines", "outbound_order_id", "ordered_qty", orderId, lines);
        ctx.Diff.AddCreated("Pedido de saída", number, $"{customer.Value.Name}: {Describe(lines.Count, lines.Sum(l => (long)l.Quantity))}");
        return number;
    }

    private static ((long Id, string Name)? Customer, List<OrderLine> Lines) Check(
        EngineContext ctx, PlanStepDocument step, JsonObject args, List<PlanError> errors)
    {
        (long, string)? customer = null;
        var name = Str(args, "customer");
        var found = FindPartner(ctx, "customers", name);
        if (found is null)
            errors.Add(Error(step, "customer", "business.unknown_customer", $"o cliente {name} não existe no cadastro."));
        else if (!found.Value.Active)
            errors.Add(Error(step, "customer", "business.inactive_customer", $"o cliente {found.Value.Name} está inativo."));
        else
            customer = (found.Value.Id, found.Value.Name);

        // order_number is UNIQUE across all warehouses: checked globally, and the message never says where it lives.
        var number = Str(args, "order_number");
        using (var cmd = ctx.Command("SELECT 1 FROM outbound_orders WHERE order_number = $n"))
        {
            cmd.Parameters.AddWithValue("$n", number);
            if (cmd.ExecuteScalar() is not null)
                errors.Add(Error(step, "order_number", "business.duplicate_order", $"o número de pedido {number} já está em uso."));
        }
        CheckDate(step, args, "due_on", "a data de entrega", errors);
        var lines = ReadLines(ctx, step, args, errors);
        return (customer, lines);
    }
}
