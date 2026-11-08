using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace erp_inteligente.Core.Engine;

internal sealed record ProductRow(long Id, string Sku, string Unit, bool TracksExpiry);

internal sealed record LocationRow(long Id, string Code, int Capacity, string ZoneCode);

internal sealed record LotRow(long Id, string Code, string? ExpiresOn);

internal sealed record BalanceRow(long Id, long? LotId, string? LotCode, int Quantity, int Reserved)
{
    public int Available => Quantity - Reserved;
}

/// <summary>Parameterized reads and writes shared by the operation handlers. Everything is scoped to the session warehouse.</summary>
internal static class StockStore
{
    public static string? Str(JsonObject args, string name) =>
        args[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    public static int? Int(JsonObject args, string name) =>
        args[name] is JsonValue v && v.TryGetValue<int>(out var i) ? i : null;

    public static bool? Bool(JsonObject args, string name) =>
        args[name] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;

    public static double? Dbl(JsonObject args, string name) =>
        args[name] is JsonValue v && v.TryGetValue<double>(out var d) ? d : null;

    public static decimal? Dec(JsonObject args, string name)
    {
        if (args[name] is not JsonValue v) return null;
        if (v.TryGetValue<decimal>(out var m)) return m;
        return v.TryGetValue<double>(out var d) ? (decimal)d : null;
    }

    /// <summary>Stores the unit cost encrypted (AES-GCM, AAD <c>products.unit_cost_enc#id</c>); it is never written in clear.</summary>
    public static void StoreUnitCost(EngineContext ctx, long productId, decimal cost)
    {
        var cipher = ctx.Cipher.Encrypt(cost.ToString(System.Globalization.CultureInfo.InvariantCulture), "products", "unit_cost_enc", productId);
        using var update = ctx.Command("UPDATE products SET unit_cost_enc = $c WHERE id = $id");
        update.Parameters.AddWithValue("$c", cipher);
        update.Parameters.AddWithValue("$id", productId);
        update.ExecuteNonQuery();
    }

    public static PlanError Error(PlanStepDocument step, string? field, string code, string message) =>
        new(step.Index, field, code, $"Passo {step.Index + 1}: {message}");

    public static ProductRow? FindProduct(EngineContext ctx, string? sku)
    {
        if (sku is null) return null;
        using var cmd = ctx.Command("SELECT id, sku, unit, tracks_expiry FROM products WHERE sku = $sku");
        cmd.Parameters.AddWithValue("$sku", sku);
        using var r = cmd.ExecuteReader();
        return r.Read() ? new ProductRow(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetInt64(3) == 1) : null;
    }

    /// <summary>
    /// Finds an active location, in an active zone, of the session warehouse; other warehouses are indistinguishable
    /// from unknown codes. Use <see cref="UnknownLocation(EngineContext, string?)"/> to explain a miss.
    /// </summary>
    public static LocationRow? FindLocation(EngineContext ctx, string? code)
    {
        if (code is null) return null;
        using var cmd = ctx.Command(
            "SELECT l.id, l.code, l.capacity, z.code FROM locations l JOIN zones z ON z.id = l.zone_id " +
            "WHERE l.code = $code AND z.warehouse_id = $wh AND l.is_active = 1 AND z.is_active = 1");
        cmd.Parameters.AddWithValue("$code", code);
        cmd.Parameters.AddWithValue("$wh", ctx.WarehouseId);
        using var r = cmd.ExecuteReader();
        return r.Read() ? new LocationRow(r.GetInt64(0), r.GetString(1), (int)r.GetInt64(2), r.GetString(3)) : null;
    }

    public static int Occupied(EngineContext ctx, long locationId)
    {
        using var cmd = ctx.Command("SELECT COALESCE(SUM(quantity), 0) FROM stock_balances WHERE location_id = $loc AND warehouse_id = $wh");
        cmd.Parameters.AddWithValue("$loc", locationId);
        cmd.Parameters.AddWithValue("$wh", ctx.WarehouseId);
        return (int)Convert.ToInt64(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    public static LotRow? FindLot(EngineContext ctx, long productId, string lotCode)
    {
        using var cmd = ctx.Command("SELECT id, lot_code, expires_on FROM lots WHERE product_id = $p AND lot_code = $lot");
        cmd.Parameters.AddWithValue("$p", productId);
        cmd.Parameters.AddWithValue("$lot", lotCode);
        using var r = cmd.ExecuteReader();
        return r.Read() ? new LotRow(r.GetInt64(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2)) : null;
    }

    public static long InsertLot(EngineContext ctx, long productId, string lotCode, string? expiresOn)
    {
        using var cmd = ctx.Command("INSERT INTO lots (product_id, lot_code, expires_on) VALUES ($p, $lot, $exp) RETURNING id");
        cmd.Parameters.AddWithValue("$p", productId);
        cmd.Parameters.AddWithValue("$lot", lotCode);
        cmd.Parameters.AddWithValue("$exp", (object?)expiresOn ?? DBNull.Value);
        return Convert.ToInt64(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>All balance rows (including empty ones) of a product at a location.</summary>
    public static List<BalanceRow> Balances(EngineContext ctx, long productId, long locationId)
    {
        using var cmd = ctx.Command(
            "SELECT b.id, b.lot_id, l.lot_code, b.quantity, b.reserved_qty FROM stock_balances b " +
            "LEFT JOIN lots l ON l.id = b.lot_id " +
            "WHERE b.product_id = $p AND b.location_id = $loc AND b.warehouse_id = $wh ORDER BY b.id");
        cmd.Parameters.AddWithValue("$p", productId);
        cmd.Parameters.AddWithValue("$loc", locationId);
        cmd.Parameters.AddWithValue("$wh", ctx.WarehouseId);
        var rows = new List<BalanceRow>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            rows.Add(new BalanceRow(r.GetInt64(0), r.IsDBNull(1) ? null : r.GetInt64(1), r.IsDBNull(2) ? null : r.GetString(2),
                (int)r.GetInt64(3), (int)r.GetInt64(4)));
        return rows;
    }

    /// <summary>Outcome of choosing the balance row an operation works on.</summary>
    internal enum Pick { Found, NoStock, NeedLot, UnknownLot }

    /// <summary>
    /// Chooses the balance row: the given lot, or the only row holding stock. With <paramref name="allowEmpty"/>
    /// (positive adjustments) an empty single row is used too.
    /// </summary>
    public static Pick PickBalance(List<BalanceRow> rows, string? lotCode, bool allowEmpty, out BalanceRow? row)
    {
        row = null;
        if (lotCode is not null)
        {
            row = rows.Find(b => string.Equals(b.LotCode, lotCode, StringComparison.Ordinal));
            return row is null ? Pick.NoStock : Pick.Found;
        }
        var active = rows.FindAll(b => b.Quantity > 0);
        if (active.Count > 1) return Pick.NeedLot;
        if (active.Count == 1) { row = active[0]; return Pick.Found; }
        if (!allowEmpty || rows.Count == 0) return Pick.NoStock;
        if (rows.Count > 1) return Pick.NeedLot;
        row = rows[0];
        return Pick.Found;
    }

    /// <summary>Adds <paramref name="delta"/> to the balance of (product, location, lot), creating the row if needed.</summary>
    public static (int Before, int After) AddToBalance(EngineContext ctx, long productId, long locationId, long? lotId, int delta)
    {
        int before;
        long? id = null;
        using (var find = ctx.Command(
            "SELECT id, quantity FROM stock_balances WHERE product_id = $p AND location_id = $loc AND lot_id IS $lot"))
        {
            find.Parameters.AddWithValue("$p", productId);
            find.Parameters.AddWithValue("$loc", locationId);
            find.Parameters.AddWithValue("$lot", (object?)lotId ?? DBNull.Value);
            using var r = find.ExecuteReader();
            if (r.Read()) { id = r.GetInt64(0); before = (int)r.GetInt64(1); } else before = 0;
        }
        var after = before + delta;
        if (id is null)
        {
            using var insert = ctx.Command(
                "INSERT INTO stock_balances (warehouse_id, product_id, location_id, lot_id, quantity, reserved_qty, updated_at) " +
                "VALUES ($wh, $p, $loc, $lot, $q, 0, $now)");
            insert.Parameters.AddWithValue("$wh", ctx.WarehouseId);
            insert.Parameters.AddWithValue("$p", productId);
            insert.Parameters.AddWithValue("$loc", locationId);
            insert.Parameters.AddWithValue("$lot", (object?)lotId ?? DBNull.Value);
            insert.Parameters.AddWithValue("$q", after);
            insert.Parameters.AddWithValue("$now", ctx.Timestamp);
            insert.ExecuteNonQuery();
        }
        else
        {
            using var update = ctx.Command("UPDATE stock_balances SET quantity = $q, updated_at = $now WHERE id = $id");
            update.Parameters.AddWithValue("$q", after);
            update.Parameters.AddWithValue("$now", ctx.Timestamp);
            update.Parameters.AddWithValue("$id", id.Value);
            update.ExecuteNonQuery();
        }
        return (before, after);
    }

    public static void InsertMovement(
        EngineContext ctx, long productId, long? lotId, long? fromId, long? toId, string kind, int quantity, string? reason)
    {
        using var cmd = ctx.Command(
            "INSERT INTO stock_movements (warehouse_id, product_id, lot_id, from_location_id, to_location_id, kind, quantity, reason, plan_id, user_id, created_at) " +
            "VALUES ($wh, $p, $lot, $from, $to, $kind, $q, $reason, $plan, $user, $now)");
        cmd.Parameters.AddWithValue("$wh", ctx.WarehouseId);
        cmd.Parameters.AddWithValue("$p", productId);
        cmd.Parameters.AddWithValue("$lot", (object?)lotId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$from", (object?)fromId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$to", (object?)toId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$kind", kind);
        cmd.Parameters.AddWithValue("$q", quantity);
        cmd.Parameters.AddWithValue("$reason", (object?)reason ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$plan", (object?)ctx.PlanId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$user", ctx.Requester.UserId);
        cmd.Parameters.AddWithValue("$now", ctx.Timestamp);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Why <see cref="FindLocation"/> found nothing: unknown in this warehouse, deactivated, or in a deactivated zone.</summary>
    public static string UnknownLocation(EngineContext ctx, string? code)
    {
        if (code is not null)
        {
            using var cmd = ctx.Command(
                "SELECT l.is_active, z.is_active, z.code FROM locations l JOIN zones z ON z.id = l.zone_id " +
                "WHERE l.code = $code AND z.warehouse_id = $wh");
            cmd.Parameters.AddWithValue("$code", code);
            cmd.Parameters.AddWithValue("$wh", ctx.WarehouseId);
            using var r = cmd.ExecuteReader();
            if (r.Read())
            {
                if (r.GetInt64(1) == 0) return $"a zona {r.GetString(2)} do endereço {code} está desativada.";
                if (r.GetInt64(0) == 0) return $"o endereço {code} está desativado.";
            }
        }
        return $"o endereço {code} não existe neste armazém.";
    }

    public static string OutOfCapacity(LocationRow location, int occupied, int qty) =>
        $"o endereço {location.Code} não comporta mais {qty} (ocupação {occupied} de {location.Capacity}).";
}
