using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using erp_inteligente.Core.Policy;
using erp_inteligente.Models;
using Microsoft.Data.Sqlite;

namespace erp_inteligente.Services.Sqlite;

/// <summary>
/// Read-only queries against the encrypted database, scoped to the warehouse of the current session through
/// <see cref="ApplicationDb"/>, which checks the permission and binds <c>$warehouse_id</c>.
/// </summary>
public sealed class SqliteQueryService(ApplicationDb db, ISessionService session) : IQueryService
{
    private const string StockSql = """
        SELECT p.sku, p.name, l.code, COALESCE(lt.lot_code, ''), lt.expires_on,
               b.quantity, b.reserved_qty, p.unit
        FROM stock_balances b
        JOIN products p ON p.id = b.product_id
        JOIN locations l ON l.id = b.location_id
        LEFT JOIN lots lt ON lt.id = b.lot_id
        WHERE b.warehouse_id = $warehouse_id AND b.quantity > 0
        ORDER BY p.sku, l.code, COALESCE(lt.lot_code, '')
        """;

    private const string LocationsSql = """
        SELECT l.code, z.name, l.capacity,
               (SELECT COALESCE(SUM(b.quantity), 0) FROM stock_balances b
                WHERE b.location_id = l.id AND b.warehouse_id = $warehouse_id) AS occupied
        FROM locations l
        JOIN zones z ON z.id = l.zone_id
        WHERE z.warehouse_id = $warehouse_id
        ORDER BY l.code
        """;

    private const string OrdersSql = """
        SELECT o.document_number AS number, 'Entrada' AS kind, s.name AS party,
               (SELECT COUNT(*) FROM inbound_order_lines il WHERE il.inbound_order_id = o.id) AS lines,
               (SELECT COALESCE(SUM(il.expected_qty), 0) FROM inbound_order_lines il WHERE il.inbound_order_id = o.id) AS units,
               o.status AS status, o.expected_on AS due_on
        FROM inbound_orders o
        JOIN suppliers s ON s.id = o.supplier_id
        WHERE o.warehouse_id = $warehouse_id
        UNION ALL
        SELECT o.order_number, 'Saída', c.name,
               (SELECT COUNT(*) FROM outbound_order_lines ol WHERE ol.outbound_order_id = o.id),
               (SELECT COALESCE(SUM(ol.ordered_qty), 0) FROM outbound_order_lines ol WHERE ol.outbound_order_id = o.id),
               o.status, o.due_on
        FROM outbound_orders o
        JOIN customers c ON c.id = o.customer_id
        WHERE o.warehouse_id = $warehouse_id
        ORDER BY due_on, number
        """;

    private const string RecipesSql = """
        SELECT name, skill_version, description, success_count, status
        FROM recipes
        ORDER BY name
        """;

    private const string AuditSql = """
        SELECT a.occurred_at, a.actor_type, a.action, a.target, a.outcome, ua.display_name, uo.display_name
        FROM audit_log a
        LEFT JOIN users ua ON ua.id = a.actor_id
        LEFT JOIN users uo ON uo.id = a.on_behalf_of
        ORDER BY a.id DESC
        LIMIT 500
        """;

    public Task<IReadOnlyList<StockRow>> GetStockAsync() =>
        db.QueryScopedAsync(session.Current, Permissions.StockRead, StockSql, ReadStock);

    public Task<IReadOnlyList<LocationRow>> GetLocationsAsync() =>
        db.QueryScopedAsync(session.Current, Permissions.StockRead, LocationsSql, ReadLocation);

    public Task<IReadOnlyList<OrderRow>> GetOrdersAsync() =>
        db.QueryScopedAsync(session.Current, Permissions.StockRead, OrdersSql, ReadOrder);

    public Task<IReadOnlyList<RecipeRow>> GetRecipesAsync() =>
        db.QueryGlobalAsync(session.Current, Permissions.StockRead, RecipesSql, ReadRecipe);

    public Task<IReadOnlyList<AuditRow>> GetAuditAsync() =>
        db.QueryGlobalAsync(session.Current, Permissions.AuditRead, AuditSql, ReadAudit);

    private static StockRow ReadStock(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3),
        SqliteFormat.ReadOptionalDate(reader, 4),
        reader.GetInt32(5),
        reader.GetInt32(6),
        reader.GetString(7));

    private static LocationRow ReadLocation(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetInt32(2),
        reader.GetInt32(3));

    /// <summary>An order without a date is shown with <see cref="DateOnly.MinValue"/>, because <see cref="OrderRow.DueOn"/> cannot hold null.</summary>
    private static OrderRow ReadOrder(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetInt32(3),
        reader.GetInt32(4),
        SqliteFormat.OrderStatusLabel(reader.GetString(5)),
        SqliteFormat.ReadDateOrMinValue(reader, 6));

    private static RecipeRow ReadRecipe(SqliteDataReader reader)
    {
        // skill_version is "<skill>@<version>", e.g. "armazenagem@1.0".
        var version = reader.GetString(1);
        var at = version.IndexOf('@');
        var skill = at < 0 ? version : version[..at];
        return new RecipeRow(
            reader.GetString(0),
            skill,
            reader.GetString(2),
            reader.GetInt32(3),
            SqliteFormat.RecipeStatusLabel(reader.GetString(4)));
    }

    private static AuditRow ReadAudit(SqliteDataReader reader)
    {
        var actorType = reader.GetString(1);
        var actorName = reader.IsDBNull(5) ? null : reader.GetString(5);
        var onBehalfName = reader.IsDBNull(6) ? null : reader.GetString(6);
        var outcome = reader.GetString(4);

        var user = actorType switch
        {
            "ai" => onBehalfName is null ? "Nexus" : $"Nexus por {onBehalfName}",
            "system" => onBehalfName is null ? "Sistema" : $"Sistema por {onBehalfName}",
            "user" => actorName ?? "Desconhecido",
            _ => actorType,
        };

        var suffix = outcome switch
        {
            "denied" => "negado",
            "failed" => "falhou",
            _ => null,
        };
        var target = reader.IsDBNull(3) ? null : reader.GetString(3);
        var detail = string.Join(" · ", new[] { target, suffix }.Where(part => !string.IsNullOrEmpty(part)));

        return new AuditRow(
            SqliteFormat.ParseUtc(reader.GetString(0)),
            user,
            SqliteFormat.AuditActionLabel(reader.GetString(2)),
            detail,
            outcome == "denied");
    }
}
