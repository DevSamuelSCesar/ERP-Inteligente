using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using erp_inteligente.Core.Data;

namespace erp_inteligente.Core.Planner;

/// <summary>
/// <c>search_locations</c> (replaces the old <c>find_location</c>): locations of the session's warehouse by partial code
/// ("a-02", "doca", "A01"), zone, aisle, free space, active flag and emptiness, ranked and paged. When a zone is given
/// it also returns <c>next_free_codes</c> (the next unused codes <c>ZONE-AA-LL</c>, unique across all warehouses) and the
/// zone's <c>typical_capacity</c>, so the model can plan new locations with sensible defaults.
/// </summary>
internal static partial class PlannerTools
{
    internal const string SearchLocations = "search_locations";
    internal const int NextFreeCodeCount = 5;

    [GeneratedRegex(@"^[A-Z]{1,4}-(?<aisle>[0-9]{2})-(?<level>[0-9]{2})\z")] private static partial Regex AisleLevelCode();

    private const string SearchLocationsDescription =
        "Pesquisa endereços do armazém por parte do código (a-02, doca), zona, corredor, espaço livre mínimo, ativo e vazio. " +
        "Devolve código, zona, capacidade, ocupado, livre e is_active. Com zone devolve também next_free_codes (próximos 5 " +
        "códigos livres, únicos no sistema) e typical_capacity (capacidade mais comum da zona), úteis para criar endereços. " +
        "Para destino de movimentação use active=true e min_free.";

    private const string SearchLocationsSchema = """
        {"type":"object","additionalProperties":false,"properties":{
          "query":{"type":"string","minLength":1,"maxLength":100,"description":"Parte do código: A-02, a02, doca. Maiúsculas não importam."},
          "zone":{"type":"string","pattern":"^[A-Za-z]{1,4}$","description":"Código da zona, ex.: A, DOCA, Q."},
          "aisle":{"type":"integer","minimum":1,"maximum":99,"description":"Corredor (o 02 de A-02-01)."},
          "min_free":{"type":"integer","minimum":1,"maximum":100000,"description":"Espaço livre mínimo."},
          "active":{"type":"boolean","description":"true: só ativos; false: só inativos. Sem o argumento, todos."},
          "empty":{"type":"boolean","description":"true: só sem saldo; false: só com saldo."},
          "limit":{"type":"integer","minimum":1,"maximum":50,"description":"Itens por página (padrão 20)."},
          "offset":{"type":"integer","minimum":0,"maximum":10000,"description":"Quantos itens pular (paginação)."}
        }}
        """;

    private static ToolOutcome ExecuteSearchLocations(NexusDatabase database, JsonElement args, long warehouseId)
    {
        var terms = SearchTerms(ArgText(args, "query"));
        var zone = ArgText(args, "zone")?.ToUpperInvariant();
        var aisle = args.TryGetProperty("aisle", out var a) && a.TryGetInt32(out var av) ? av : (int?)null;
        var (limit, offset) = ArgPage(args);

        using var connection = database.OpenAiReadOnly();
        connection.CreateFunction("nexus_fold", (string? value) => Fold(value), isDeterministic: true);

        var where = new List<string> { "l.warehouse_id = $warehouse_id" };
        var parameters = new List<(string, object)> { ("$warehouse_id", warehouseId) };
        AddTermConditions(terms, "l.code || ' ' || REPLACE(l.code, '-', '')", where, parameters);
        if (zone is not null)
        {
            where.Add("l.zone_code = $zone");
            parameters.Add(("$zone", zone));
        }
        if (aisle is { } number)
        {
            // Digits only: the pattern cannot hold wildcards.
            where.Add("l.code LIKE $aisle_like");
            parameters.Add(("$aisle_like", "%-" + number.ToString("00", CultureInfo.InvariantCulture) + "-%"));
        }
        if (ArgFlag(args, "active") is { } active)
        {
            where.Add("l.is_active = $active");
            parameters.Add(("$active", active ? 1 : 0));
        }
        if (args.TryGetProperty("min_free", out var m) && m.TryGetInt64(out var minFree))
        {
            where.Add("l.capacity - COALESCE(o.occupied, 0) >= $min_free");
            parameters.Add(("$min_free", minFree));
        }
        if (ArgFlag(args, "empty") is { } empty)
            where.Add(empty ? "COALESCE(o.occupied, 0) = 0" : "COALESCE(o.occupied, 0) <> 0");

        const string from =
            "FROM v_ai_locations l " +
            "LEFT JOIN (SELECT location_code, SUM(quantity) AS occupied FROM v_ai_stock " +
            "           WHERE warehouse_id = $warehouse_id GROUP BY location_code) o ON o.location_code = l.code ";
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
            "SELECT l.code, l.zone_code, l.capacity, COALESCE(o.occupied, 0), l.is_active " +
            from + filter +
            "ORDER BY CASE WHEN $exact IS NOT NULL AND UPPER(l.code) = $exact THEN 0 " +
            "              WHEN $first IS NOT NULL AND nexus_fold(l.code) LIKE $first ESCAPE '\\' THEN 1 " +
            "              ELSE 2 END, l.code " +
            "LIMIT $limit OFFSET $offset";
        Bind(command, parameters);
        command.Parameters.AddWithValue("$exact", terms.Count == 1 ? terms[0].ToUpperInvariant() : DBNull.Value);
        command.Parameters.AddWithValue("$first", terms.Count == 0 ? DBNull.Value : LikeLiteral(terms[0]) + "%");
        command.Parameters.AddWithValue("$limit", limit);
        command.Parameters.AddWithValue("$offset", offset);

        var items = new List<JsonObject>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var capacity = reader.GetInt64(2);
                var occupied = reader.GetInt64(3);
                items.Add(new JsonObject
                {
                    ["code"] = reader.GetString(0),
                    ["zone"] = reader.GetString(1),
                    ["capacity"] = capacity,
                    ["occupied"] = occupied,
                    ["free"] = Math.Max(0, capacity - occupied),
                    ["is_active"] = reader.GetInt64(4) == 1,
                });
            }
        }

        JsonObject? extra = null;
        if (zone is not null)
            extra = ZoneSuggestions(connection, zone, aisle, warehouseId);
        return SearchPage(items, total, offset,
            "Nenhum endereço encontrado. Tente menos termos ou só a zona; se não existir, um plano pode criá-lo (create_location, create_location_range).",
            extra);
    }

    /// <summary>next_free_codes, typical_capacity and zone_exists for a zone (the codes are unique across warehouses).</summary>
    private static JsonObject ZoneSuggestions(Microsoft.Data.Sqlite.SqliteConnection connection, string zone, int? aisle, long warehouseId)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        var highestAisle = 1;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT code FROM v_ai_locations WHERE code LIKE $prefix ESCAPE '\\'";
            command.Parameters.AddWithValue("$prefix", LikeLiteral(zone) + "-%");
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var code = reader.GetString(0);
                used.Add(code);
                var match = AisleLevelCode().Match(code);
                if (match.Success && string.Equals(code[..code.IndexOf('-')], zone, StringComparison.Ordinal))
                    highestAisle = Math.Max(highestAisle, int.Parse(match.Groups["aisle"].Value, CultureInfo.InvariantCulture));
            }
        }

        var free = new JsonArray();
        for (var current = aisle ?? highestAisle; current <= (aisle ?? 99) && free.Count < NextFreeCodeCount; current++)
            for (var level = 1; level <= 99 && free.Count < NextFreeCodeCount; level++)
            {
                var code = string.Create(CultureInfo.InvariantCulture, $"{zone}-{current:00}-{level:00}");
                if (!used.Contains(code)) free.Add(code);
            }

        long? typical = null;
        bool exists;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT COUNT(*) FROM v_ai_zones WHERE warehouse_id = $warehouse_id AND code = $zone";
            command.Parameters.AddWithValue("$warehouse_id", warehouseId);
            command.Parameters.AddWithValue("$zone", zone);
            exists = (long)command.ExecuteScalar()! > 0;
        }
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "SELECT capacity FROM v_ai_locations WHERE warehouse_id = $warehouse_id AND zone_code = $zone " +
                "GROUP BY capacity ORDER BY COUNT(*) DESC, capacity DESC LIMIT 1";
            command.Parameters.AddWithValue("$warehouse_id", warehouseId);
            command.Parameters.AddWithValue("$zone", zone);
            if (command.ExecuteScalar() is long capacity) typical = capacity;
        }

        return new JsonObject
        {
            ["zone_exists"] = exists,
            ["next_free_codes"] = free,
            ["typical_capacity"] = typical,
        };
    }
}
