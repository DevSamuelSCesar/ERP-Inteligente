using System;
using System.Globalization;

namespace erp_inteligente.Core.Engine;

internal sealed record ZoneRow(long Id, string Code, string Name, bool IsActive);

/// <summary>A location in any state (active or not), with its zone, for the structure operations.</summary>
internal sealed record LocationInfo(long Id, string Code, int Capacity, bool IsActive, string ZoneCode, bool ZoneActive);

/// <summary>Parameterized lookups shared by the zone and location operations. Everything is scoped to the session warehouse.</summary>
internal static class StructureStore
{
    public static string N(long value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Finds a zone of the session warehouse, active or not.</summary>
    public static ZoneRow? FindZone(EngineContext ctx, string? code)
    {
        if (code is null) return null;
        using var cmd = ctx.Command("SELECT id, code, name, is_active FROM zones WHERE code = $code AND warehouse_id = $wh");
        cmd.Parameters.AddWithValue("$code", code);
        cmd.Parameters.AddWithValue("$wh", ctx.WarehouseId);
        using var r = cmd.ExecuteReader();
        return r.Read() ? new ZoneRow(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetInt64(3) == 1) : null;
    }

    /// <summary>Total quantity held in every location of a zone (reserved included).</summary>
    public static long ZoneStock(EngineContext ctx, long zoneId)
    {
        using var cmd = ctx.Command(
            "SELECT COALESCE(SUM(b.quantity), 0) FROM stock_balances b JOIN locations l ON l.id = b.location_id " +
            "WHERE l.zone_id = $zone AND b.warehouse_id = $wh AND b.quantity > 0");
        cmd.Parameters.AddWithValue("$zone", zoneId);
        cmd.Parameters.AddWithValue("$wh", ctx.WarehouseId);
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>Finds a location of the session warehouse in any state, with its zone's state.</summary>
    public static LocationInfo? FindAnyLocation(EngineContext ctx, string? code)
    {
        if (code is null) return null;
        using var cmd = ctx.Command(
            "SELECT l.id, l.code, l.capacity, l.is_active, z.code, z.is_active FROM locations l JOIN zones z ON z.id = l.zone_id " +
            "WHERE l.code = $code AND z.warehouse_id = $wh");
        cmd.Parameters.AddWithValue("$code", code);
        cmd.Parameters.AddWithValue("$wh", ctx.WarehouseId);
        using var r = cmd.ExecuteReader();
        return r.Read()
            ? new LocationInfo(r.GetInt64(0), r.GetString(1), (int)r.GetInt64(2), r.GetInt64(3) == 1, r.GetString(4), r.GetInt64(5) == 1)
            : null;
    }

    /// <summary>Location codes are unique across the whole database (column <c>locations.code</c>), not per warehouse.</summary>
    public static bool LocationCodeInUse(EngineContext ctx, string code)
    {
        using var cmd = ctx.Command("SELECT 1 FROM locations WHERE code = $code");
        cmd.Parameters.AddWithValue("$code", code);
        return cmd.ExecuteScalar() is not null;
    }

    public static string ActiveText(bool active, bool feminine) =>
        active ? (feminine ? "ativa" : "ativo") : (feminine ? "desativada" : "desativado");
}
