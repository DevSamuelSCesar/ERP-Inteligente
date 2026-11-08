using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using static erp_inteligente.Core.Engine.StockStore;

namespace erp_inteligente.Core.Engine;

internal sealed record OutboundOrderRow(long Id, string Number, string Status);

/// <summary>A line of an outbound order. <see cref="Held"/> is the quantity in open reservations (reserved or picked).</summary>
internal sealed record OrderLineRow(long Id, long ProductId, string Sku, string Unit, int Ordered, int Picked, int Held)
{
    public int Remaining => Ordered - Held;
}

/// <summary>An <c>order_reservations</c> row with the balance it points at.</summary>
internal sealed record ReservationRow(
    long Id, long LineId, long BalanceId, int Quantity, string State,
    long ProductId, long LocationId, string LocationCode, long? LotId, string? LotCode);

/// <summary>A line requested by a reservation operation (<c>lines</c> argument).</summary>
internal sealed record LineRequest(string Sku, int? Quantity, string? Location, string? Lot);

/// <summary>Parameterized reads and writes of order reservations. Everything is scoped to the session warehouse.</summary>
internal static class ReservationStore
{
    public const string DockZone = "DOCA";

    public static OutboundOrderRow? FindOutbound(EngineContext ctx, string? number)
    {
        if (number is null) return null;
        using var cmd = ctx.Command("SELECT id, order_number, status FROM outbound_orders WHERE order_number = $n AND warehouse_id = $wh");
        cmd.Parameters.AddWithValue("$n", number);
        cmd.Parameters.AddWithValue("$wh", ctx.WarehouseId);
        using var r = cmd.ExecuteReader();
        return r.Read() ? new OutboundOrderRow(r.GetInt64(0), r.GetString(1), r.GetString(2)) : null;
    }

    /// <summary>Why an order cannot take part in the operation, or null when its status is one of <paramref name="allowed"/>.</summary>
    public static PlanError? CheckStatus(PlanStepDocument step, OutboundOrderRow order, string what, params string[] allowed)
    {
        if (Array.IndexOf(allowed, order.Status) >= 0) return null;
        var state = order.Status switch
        {
            "open" => "aberto, sem reservas",
            "reserved" => "reservado e ainda não separado",
            "picking" => "em separação",
            "shipped" => "já expedido",
            "cancelled" => "cancelado",
            _ => order.Status,
        };
        return Error(step, "order_number", "business.order_status", $"o pedido {order.Number} está {state}; não é possível {what}.");
    }

    public static PlanError UnknownOrder(PlanStepDocument step, string? number) =>
        Error(step, "order_number", "business.unknown_order", $"o pedido de saída {number} não existe neste armazém.");

    public static List<OrderLineRow> Lines(EngineContext ctx, long orderId)
    {
        using var cmd = ctx.Command(
            "SELECT l.id, l.product_id, p.sku, p.unit, l.ordered_qty, l.picked_qty, " +
            "COALESCE((SELECT SUM(r.quantity) FROM order_reservations r WHERE r.outbound_order_line_id = l.id " +
            "AND r.state IN ('reserved', 'picked')), 0) " +
            "FROM outbound_order_lines l JOIN products p ON p.id = l.product_id WHERE l.outbound_order_id = $o ORDER BY l.id");
        cmd.Parameters.AddWithValue("$o", orderId);
        var rows = new List<OrderLineRow>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            rows.Add(new OrderLineRow(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetString(3),
                (int)r.GetInt64(4), (int)r.GetInt64(5), (int)r.GetInt64(6)));
        return rows;
    }

    /// <summary>The reservation rows of an order in one of the given states, oldest first.</summary>
    public static List<ReservationRow> Reservations(EngineContext ctx, long orderId, params string[] states)
    {
        // The state list is built from code constants (one parameter per state), never from input.
        var marks = string.Join(", ", Enumerable.Range(0, states.Length).Select(i => "$s" + i.ToString(CultureInfo.InvariantCulture)));
        using var cmd = ctx.Command(
            "SELECT r.id, r.outbound_order_line_id, r.balance_id, r.quantity, r.state, b.product_id, b.location_id, loc.code, b.lot_id, t.lot_code " +
            "FROM order_reservations r JOIN outbound_order_lines l ON l.id = r.outbound_order_line_id " +
            "JOIN stock_balances b ON b.id = r.balance_id JOIN locations loc ON loc.id = b.location_id " +
            "LEFT JOIN lots t ON t.id = b.lot_id " +
            $"WHERE l.outbound_order_id = $o AND r.warehouse_id = $wh AND r.state IN ({marks}) ORDER BY r.id");
        cmd.Parameters.AddWithValue("$o", orderId);
        cmd.Parameters.AddWithValue("$wh", ctx.WarehouseId);
        for (var i = 0; i < states.Length; i++)
            cmd.Parameters.AddWithValue("$s" + i.ToString(CultureInfo.InvariantCulture), states[i]);
        var rows = new List<ReservationRow>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            rows.Add(new ReservationRow(r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), (int)r.GetInt64(3), r.GetString(4),
                r.GetInt64(5), r.GetInt64(6), r.GetString(7), r.IsDBNull(8) ? null : r.GetInt64(8), r.IsDBNull(9) ? null : r.GetString(9)));
        return rows;
    }

    /// <summary>Parses the optional <c>lines</c> argument; null when it is absent. Repeated SKUs are reported once each.</summary>
    public static List<LineRequest>? ReadRequests(PlanStepDocument step, JsonObject args, List<PlanError> errors)
    {
        if (args["lines"] is not JsonArray array) return null;
        var requests = new List<LineRequest>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in array)
        {
            if (node is not JsonObject o || Str(o, "sku") is not { } sku)
            {
                errors.Add(Error(step, "lines", "business.invalid_line", "cada linha precisa de um SKU."));
                continue;
            }
            if (!seen.Add(sku))
            {
                errors.Add(Error(step, "lines", "business.duplicate_sku", $"o SKU {sku} aparece em mais de uma linha; some as quantidades em uma só."));
                continue;
            }
            requests.Add(new LineRequest(sku, Int(o, "qty"), Str(o, "location"), Str(o, "lot")));
        }
        return requests;
    }

    public static long? BalanceId(EngineContext ctx, long productId, long locationId, long? lotId)
    {
        using var cmd = ctx.Command("SELECT id FROM stock_balances WHERE product_id = $p AND location_id = $loc AND lot_id IS $lot");
        cmd.Parameters.AddWithValue("$p", productId);
        cmd.Parameters.AddWithValue("$loc", locationId);
        cmd.Parameters.AddWithValue("$lot", (object?)lotId ?? DBNull.Value);
        return cmd.ExecuteScalar() is { } id ? Convert.ToInt64(id, CultureInfo.InvariantCulture) : null;
    }

    public static int BalanceQuantity(EngineContext ctx, long balanceId)
    {
        using var cmd = ctx.Command("SELECT quantity FROM stock_balances WHERE id = $id");
        cmd.Parameters.AddWithValue("$id", balanceId);
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>Changes <c>reserved_qty</c> of a balance by <paramref name="delta"/> (negative to release).</summary>
    public static void AddReserved(EngineContext ctx, long balanceId, int delta)
    {
        using var cmd = ctx.Command("UPDATE stock_balances SET reserved_qty = reserved_qty + $d, updated_at = $now WHERE id = $id");
        cmd.Parameters.AddWithValue("$d", delta);
        cmd.Parameters.AddWithValue("$now", ctx.Timestamp);
        cmd.Parameters.AddWithValue("$id", balanceId);
        cmd.ExecuteNonQuery();
    }

    public static void InsertReservation(EngineContext ctx, long lineId, long balanceId, int quantity, string state)
    {
        using var cmd = ctx.Command(
            "INSERT INTO order_reservations (warehouse_id, outbound_order_line_id, balance_id, quantity, state, created_plan_id, created_at) " +
            "VALUES ($wh, $line, $bal, $q, $state, $plan, $now)");
        cmd.Parameters.AddWithValue("$wh", ctx.WarehouseId);
        cmd.Parameters.AddWithValue("$line", lineId);
        cmd.Parameters.AddWithValue("$bal", balanceId);
        cmd.Parameters.AddWithValue("$q", quantity);
        cmd.Parameters.AddWithValue("$state", state);
        cmd.Parameters.AddWithValue("$plan", (object?)ctx.PlanId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$now", ctx.Timestamp);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Moves a whole row to a new state (and, for a pick, to the dock balance).</summary>
    public static void SetState(EngineContext ctx, long rowId, string state, long? balanceId = null)
    {
        using var cmd = ctx.Command(
            "UPDATE order_reservations SET state = $s, balance_id = COALESCE($bal, balance_id), updated_plan_id = $plan WHERE id = $id");
        cmd.Parameters.AddWithValue("$s", state);
        cmd.Parameters.AddWithValue("$bal", (object?)balanceId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$plan", (object?)ctx.PlanId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$id", rowId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Releases <paramref name="take"/> of a row. A partial release closes the original row (its quantity never changes)
    /// and opens a new one with the rest, in the same state and balance.
    /// </summary>
    public static void ReleaseRow(EngineContext ctx, ReservationRow row, int take)
    {
        SetState(ctx, row.Id, "released");
        if (take < row.Quantity)
            InsertReservation(ctx, row.LineId, row.BalanceId, row.Quantity - take, row.State);
    }

    /// <summary>
    /// Marks <paramref name="take"/> of a reserved row as picked at the dock balance. A partial pick closes the original
    /// row and opens one picked row (the part taken) and one reserved row (the rest, still at the source balance).
    /// </summary>
    public static void PickRow(EngineContext ctx, ReservationRow row, int take, long dockBalanceId)
    {
        if (take == row.Quantity)
        {
            SetState(ctx, row.Id, "picked", dockBalanceId);
            return;
        }
        SetState(ctx, row.Id, "released");
        InsertReservation(ctx, row.LineId, dockBalanceId, take, "picked");
        InsertReservation(ctx, row.LineId, row.BalanceId, row.Quantity - take, "reserved");
    }

    public static void AddPicked(EngineContext ctx, long lineId, int delta)
    {
        using var cmd = ctx.Command("UPDATE outbound_order_lines SET picked_qty = picked_qty + $d WHERE id = $id");
        cmd.Parameters.AddWithValue("$d", delta);
        cmd.Parameters.AddWithValue("$id", lineId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Recomputes the order status from its reservations and picked quantities (open, reserved or picking).</summary>
    public static void RefreshStatus(EngineContext ctx, long orderId)
    {
        using var cmd = ctx.Command(
            "UPDATE outbound_orders SET status = CASE " +
            "WHEN EXISTS (SELECT 1 FROM outbound_order_lines WHERE outbound_order_id = $o AND picked_qty > 0) THEN 'picking' " +
            "WHEN EXISTS (SELECT 1 FROM order_reservations r JOIN outbound_order_lines l ON l.id = r.outbound_order_line_id " +
            "WHERE l.outbound_order_id = $o AND r.state IN ('reserved', 'picked')) THEN 'reserved' ELSE 'open' END " +
            "WHERE id = $o AND warehouse_id = $wh");
        cmd.Parameters.AddWithValue("$o", orderId);
        cmd.Parameters.AddWithValue("$wh", ctx.WarehouseId);
        cmd.ExecuteNonQuery();
    }

    public static void SetOrderStatus(EngineContext ctx, long orderId, string status)
    {
        using var cmd = ctx.Command("UPDATE outbound_orders SET status = $s WHERE id = $o AND warehouse_id = $wh");
        cmd.Parameters.AddWithValue("$s", status);
        cmd.Parameters.AddWithValue("$o", orderId);
        cmd.Parameters.AddWithValue("$wh", ctx.WarehouseId);
        cmd.ExecuteNonQuery();
    }
}
