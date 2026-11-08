using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using erp_inteligente.Core.Data;

namespace erp_inteligente.Core.Planner;

/// <summary>
/// <c>search_zones</c>: zones of the session's warehouse by partial code or name (case and accent insensitive), with the
/// number of active locations, total capacity, occupied and free quantity. Read-only AI connection, <c>v_ai_*</c> views.
/// </summary>
internal static partial class PlannerTools
{
    internal const string SearchZones = "search_zones";

    private const string SearchZonesDescription =
        "Pesquisa zonas do armazém por parte do código ou do nome (sem argumentos lista todas). Devolve código, nome, " +
        "is_active, endereços ativos, capacidade total, ocupado e livre. Use antes de criar uma zona, para ver se já existe.";

    private const string SearchZonesSchema = """
        {"type":"object","additionalProperties":false,"properties":{
          "query":{"type":"string","minLength":1,"maxLength":100,"description":"Parte do código ou do nome da zona (ex.: quarentena, Q, refrig)."},
          "active":{"type":"boolean","description":"true: só zonas ativas; false: só inativas."},
          "limit":{"type":"integer","minimum":1,"maximum":50,"description":"Itens por página (padrão 20)."},
          "offset":{"type":"integer","minimum":0,"maximum":10000,"description":"Quantos itens pular (paginação)."}
        }}
        """;

    private static ToolOutcome ExecuteSearchZones(NexusDatabase database, JsonElement args, long warehouseId)
    {
        var terms = SearchTerms(ArgText(args, "query"));
        var (limit, offset) = ArgPage(args);

        using var connection = database.OpenAiReadOnly();
        connection.CreateFunction("nexus_fold", (string? value) => Fold(value), isDeterministic: true);

        var where = new List<string> { "z.warehouse_id = $warehouse_id" };
        var parameters = new List<(string, object)> { ("$warehouse_id", warehouseId) };
        AddTermConditions(terms, "z.code || ' ' || z.name", where, parameters);
        if (ArgFlag(args, "active") is { } active)
        {
            where.Add("z.is_active = $active");
            parameters.Add(("$active", active ? 1 : 0));
        }

        const string from =
            "FROM v_ai_zones z " +
            "LEFT JOIN (SELECT zone_code, COUNT(*) AS n, SUM(capacity) AS cap FROM v_ai_locations " +
            "           WHERE warehouse_id = $warehouse_id AND is_active = 1 GROUP BY zone_code) l ON l.zone_code = z.code " +
            "LEFT JOIN (SELECT zone_code, SUM(quantity) AS occ FROM v_ai_stock " +
            "           WHERE warehouse_id = $warehouse_id GROUP BY zone_code) s ON s.zone_code = z.code ";
        var filter = "WHERE " + string.Join(" AND ", where) + " ";

        long total;
        using (var count = connection.CreateCommand())
        {
            count.CommandText = "SELECT COUNT(*) " + from + filter;
            Bind(count, parameters);
            total = (long)count.ExecuteScalar()!;
        }

        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT z.code, z.name, z.is_active, COALESCE(l.n, 0), COALESCE(l.cap, 0), COALESCE(s.occ, 0) " +
            from + filter +
            "ORDER BY CASE WHEN nexus_fold(z.code) = $exact THEN 0 " +
            "              WHEN $first IS NOT NULL AND (nexus_fold(z.code) LIKE $first ESCAPE '\\' OR nexus_fold(z.name) LIKE $first ESCAPE '\\') THEN 1 " +
            "              ELSE 2 END, z.code " +
            "LIMIT $limit OFFSET $offset";
        Bind(command, parameters);
        command.Parameters.AddWithValue("$exact", terms.Count == 1 ? terms[0] : "");
        command.Parameters.AddWithValue("$first", terms.Count == 0 ? DBNull.Value : LikeLiteral(terms[0]) + "%");
        command.Parameters.AddWithValue("$limit", limit);
        command.Parameters.AddWithValue("$offset", offset);

        var items = new List<JsonObject>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var capacity = reader.GetInt64(4);
                var occupied = reader.GetInt64(5);
                items.Add(new JsonObject
                {
                    ["code"] = reader.GetString(0),
                    ["name"] = reader.GetString(1),
                    ["is_active"] = reader.GetInt64(2) == 1,
                    ["locations"] = reader.GetInt64(3),
                    ["capacity"] = capacity,
                    ["occupied"] = occupied,
                    ["free"] = Math.Max(0, capacity - occupied),
                });
            }
        }
        return SearchPage(items, total, offset, "Nenhuma zona encontrada. Tente parte do nome ou do código; se não existir, um plano pode criá-la (create_zone).");
    }
}
