using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using erp_inteligente.Core.Data;

namespace erp_inteligente.Core.Planner;

/// <summary>
/// <c>search_orders</c>: inbound and outbound orders of the session's warehouse, one row per order with its lines
/// summarized. Free text matches the order number, the partner name and the SKUs of the lines, term by term.
/// </summary>
internal static partial class PlannerTools
{
    internal const string SearchOrders = "search_orders";
    private const int MaxOrderLinesLength = 300;

    private const string SearchOrdersDescription =
        "Pesquisa pedidos de entrada (inbound) e de saída (outbound) do armazém, um por linha, com os itens resumidos " +
        "(SKU feito/total). Texto livre acha parte do número, do nome do fornecedor ou cliente e de SKUs. Status de entrada: " +
        "expected, receiving, received, cancelled; de saída: open, reserved, picking, shipped, cancelled.";

    private const string SearchOrdersSchema = """
        {"type":"object","additionalProperties":false,"properties":{
          "kind":{"type":"string","enum":["inbound","outbound"],"description":"inbound: entrada; outbound: saída. Sem o argumento, ambos."},
          "status":{"type":"string","enum":["expected","receiving","received","open","reserved","picking","shipped","cancelled"]},
          "query":{"type":"string","minLength":1,"maxLength":100,"description":"Parte do número do pedido, do nome do parceiro ou de um SKU."},
          "sku":{"type":"string","pattern":"^[A-Za-z]{1,3}(-?[0-9]{0,3})?$","description":"Só pedidos com este SKU (completo ou início)."},
          "limit":{"type":"integer","minimum":1,"maximum":50,"description":"Itens por página (padrão 20)."},
          "offset":{"type":"integer","minimum":0,"maximum":10000,"description":"Quantos itens pular (paginação)."}
        }}
        """;

    /// <summary>One SELECT per order kind; column names are constants, user values are parameters.</summary>
    private static string OrderSelect(string kind, string view, string number, string partner, string due, string total, string done,
        string termsHaving) =>
        $"SELECT '{kind}' AS kind, id, {number} AS number, status, {due} AS due_on, {partner} AS partner, " +
        $"COUNT(*) AS line_count, SUM({total}) AS total_qty, SUM({done}) AS done_qty, " +
        $"GROUP_CONCAT(sku || ' ' || {done} || '/' || {total}, '; ') AS lines " +
        $"FROM (SELECT * FROM {view} WHERE warehouse_id = $warehouse_id AND ($status IS NULL OR status = $status) ORDER BY sku) " +
        $"GROUP BY id, {number}, status, {due}, {partner} " +
        $"HAVING ($sku_prefix IS NULL OR SUM(CASE WHEN sku LIKE $sku_prefix ESCAPE '\\' THEN 1 ELSE 0 END) > 0) " +
        $"AND {termsHaving}";

    private static ToolOutcome ExecuteSearchOrders(NexusDatabase database, JsonElement args, long warehouseId)
    {
        var terms = SearchTerms(ArgText(args, "query"));
        var kind = ArgText(args, "kind");
        var sku = ArgText(args, "sku") is { } fragment ? NormalizeSkuFragment(fragment) : null;
        var (limit, offset) = ArgPage(args);

        using var connection = database.OpenAiReadOnly();
        connection.CreateFunction("nexus_fold", (string? value) => Fold(value), isDeterministic: true);

        var parameters = new List<(string, object)> { ("$warehouse_id", warehouseId) };
        var having = TermsHaving(terms, "{0} || ' ' || {1} || ' ' || GROUP_CONCAT(sku, ' ')", parameters);
        var selects = new List<string>();
        if (kind is null or "inbound")
            selects.Add(OrderSelect("inbound", "v_ai_inbound_orders", "document_number", "supplier_name", "expected_on",
                "expected_qty", "received_qty", string.Format(System.Globalization.CultureInfo.InvariantCulture, having, "document_number", "supplier_name")));
        if (kind is null or "outbound")
            selects.Add(OrderSelect("outbound", "v_ai_outbound_orders", "order_number", "customer_name", "due_on",
                "ordered_qty", "picked_qty", string.Format(System.Globalization.CultureInfo.InvariantCulture, having, "order_number", "customer_name")));
        var from = "FROM (" + string.Join(" UNION ALL ", selects) + ") o ";

        long total;
        using (var count = connection.CreateCommand())
        {
            count.CommandText = "SELECT COUNT(*) " + from;
            BindOrders(count);
            total = (long)count.ExecuteScalar()!;
        }

        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT kind, number, status, due_on, partner, line_count, total_qty, done_qty, lines " + from +
            "ORDER BY due_on, number LIMIT $limit OFFSET $offset";
        BindOrders(command);
        command.Parameters.AddWithValue("$limit", limit);
        command.Parameters.AddWithValue("$offset", offset);

        var items = new List<JsonObject>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                items.Add(new JsonObject
                {
                    ["kind"] = reader.GetString(0),
                    ["number"] = reader.GetString(1),
                    ["status"] = reader.GetString(2),
                    ["due_on"] = reader.IsDBNull(3) ? null : reader.GetString(3),
                    ["partner"] = reader.GetString(4),
                    ["line_count"] = reader.GetInt64(5),
                    ["total_qty"] = reader.GetInt64(6),
                    ["done_qty"] = reader.GetInt64(7),
                    ["lines"] = Truncate(reader.GetString(8), MaxOrderLinesLength),
                });
            }
        }
        return SearchPage(items, total, offset, "Nenhum pedido encontrado. Tente parte do número, do nome do parceiro ou outro status.");

        void BindOrders(Microsoft.Data.Sqlite.SqliteCommand target)
        {
            Bind(target, parameters);
            target.Parameters.AddWithValue("$status", Nullable(ArgText(args, "status")));
            target.Parameters.AddWithValue("$sku_prefix", Nullable(sku is null ? null : LikeLiteral(sku) + "%"));
        }
    }
}
