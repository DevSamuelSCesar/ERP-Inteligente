using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using erp_inteligente.Core.Data;

namespace erp_inteligente.Core.Planner;

/// <summary>
/// <c>search_lots</c>: lots by SKU (complete or partial), free text over SKU and lot code, and expiry date, with the
/// quantity held in the session's warehouse. Ordered by expiry (first to expire first) and paged.
/// </summary>
internal static partial class PlannerTools
{
    internal const string SearchLots = "search_lots";

    private const string SearchLotsDescription =
        "Pesquisa lotes por SKU (completo ou parcial), texto livre (código do lote ou SKU) e validade (expires_before = " +
        "vencem até a data, AAAA-MM-DD). Devolve sku, lot_code, expires_on e a quantidade no armazém, do que vence primeiro " +
        "ao que vence depois. Use para conferir se um lote existe e para achar o que está perto de vencer.";

    private const string SearchLotsSchema = """
        {"type":"object","additionalProperties":false,"properties":{
          "sku":{"type":"string","pattern":"^[A-Za-z]{1,3}(-?[0-9]{0,3})?$","description":"SKU completo (ABC-123) ou início dele."},
          "query":{"type":"string","minLength":1,"maxLength":100,"description":"Parte do código do lote ou do SKU."},
          "expires_before":{"type":"string","pattern":"^[0-9]{4}-[0-9]{2}-[0-9]{2}$","description":"Só lotes que vencem até esta data (AAAA-MM-DD)."},
          "limit":{"type":"integer","minimum":1,"maximum":50,"description":"Itens por página (padrão 20)."},
          "offset":{"type":"integer","minimum":0,"maximum":10000,"description":"Quantos itens pular (paginação)."}
        }}
        """;

    private static ToolOutcome ExecuteSearchLots(NexusDatabase database, JsonElement args, long warehouseId)
    {
        var terms = SearchTerms(ArgText(args, "query"));
        var sku = ArgText(args, "sku") is { } fragment ? NormalizeSkuFragment(fragment) : null;
        var expiresBefore = ArgText(args, "expires_before");
        var (limit, offset) = ArgPage(args);

        using var connection = database.OpenAiReadOnly();
        connection.CreateFunction("nexus_fold", (string? value) => Fold(value), isDeterministic: true);

        var where = new List<string>();
        var parameters = new List<(string, object)> { ("$warehouse_id", warehouseId) };
        AddTermConditions(terms, "t.sku || ' ' || REPLACE(t.sku, '-', '') || ' ' || t.lot_code", where, parameters);
        if (sku is not null)
        {
            where.Add("t.sku LIKE $sku_prefix ESCAPE '\\'");
            parameters.Add(("$sku_prefix", LikeLiteral(sku) + "%"));
        }
        if (expiresBefore is not null)
        {
            where.Add("t.expires_on IS NOT NULL AND t.expires_on <= $expires_before");
            parameters.Add(("$expires_before", expiresBefore));
        }

        const string from =
            "FROM v_ai_lots t " +
            "LEFT JOIN (SELECT sku, lot_code, SUM(quantity) AS quantity FROM v_ai_stock " +
            "           WHERE warehouse_id = $warehouse_id AND lot_code IS NOT NULL GROUP BY sku, lot_code) s " +
            "ON s.sku = t.sku AND s.lot_code = t.lot_code ";
        var filter = where.Count == 0 ? "" : "WHERE " + string.Join(" AND ", where) + " ";

        long total;
        using (var count = connection.CreateCommand())
        {
            count.CommandText = "SELECT COUNT(*) " + from + filter;
            Bind(count, parameters);
            total = (long)count.ExecuteScalar()!;
        }

        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT t.sku, t.lot_code, t.expires_on, COALESCE(s.quantity, 0) " +
            from + filter +
            "ORDER BY t.expires_on IS NULL, t.expires_on, t.sku, t.lot_code LIMIT $limit OFFSET $offset";
        Bind(command, parameters);
        command.Parameters.AddWithValue("$limit", limit);
        command.Parameters.AddWithValue("$offset", offset);

        var items = new List<JsonObject>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                items.Add(new JsonObject
                {
                    ["sku"] = reader.GetString(0),
                    ["lot_code"] = reader.GetString(1),
                    ["expires_on"] = reader.IsDBNull(2) ? null : reader.GetString(2),
                    ["quantity"] = reader.GetInt64(3),
                });
            }
        }
        return SearchPage(items, total, offset, "Nenhum lote encontrado. Tente só o início do SKU ou parte do código do lote.");
    }
}
