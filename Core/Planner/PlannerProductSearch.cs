using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using erp_inteligente.Core.Data;
using Microsoft.Data.Sqlite;

namespace erp_inteligente.Core.Planner;

/// <summary>
/// <c>search_products</c>: catalog search for the Planner, with or without a name, with or without a SKU.
/// Free text matches SKU, name and description term by term (all terms must match), ignoring case and accents; a SKU
/// may be complete or partial ("ABC", "ABC-1", "abc123"). Filters: stock in the session's warehouse, expiry control
/// and unit. Results are ranked (exact SKU, SKU prefix, name prefix, other matches) and paged.
/// Runs on the AI read-only connection over <c>v_ai_products</c> and <c>v_ai_stock</c>; never reads the unit cost.
/// </summary>
internal static partial class PlannerTools
{
    internal const string SearchProducts = "search_products";

    internal const int MaxSearchTerms = 6;
    internal const int DefaultSearchLimit = 20;
    internal const int MaxDescriptionLength = 200;

    // Letters (any alphabet, accents removed later), digits and '-' only; everything else separates terms.
    [GeneratedRegex(@"[^\p{L}\p{Nd}\-]+")] private static partial Regex TermSeparator();
    [GeneratedRegex(@"^[A-Za-z]{1,3}(-?[0-9]{0,3})?\z")] private static partial Regex SkuFragment();
    [GeneratedRegex(@"^[a-z]{1,4}\z")] private static partial Regex Unit();

    private const string SearchProductsSchema = """
        {"type":"object","additionalProperties":false,"properties":{
          "query":{"type":"string","minLength":1,"maxLength":100,"description":"Texto livre: parte do nome, da descrição ou do SKU. Maiúsculas e acentos não importam; todos os termos precisam aparecer."},
          "sku":{"type":"string","pattern":"^[A-Za-z]{1,3}(-?[0-9]{0,3})?$","description":"SKU completo (ABC-123) ou início dele (ABC, ABC-1, abc12)."},
          "has_stock":{"type":"boolean","description":"true: só com saldo no armazém; false: só sem saldo (ex.: recém-cadastrados)."},
          "tracks_expiry":{"type":"boolean","description":"Filtra produtos que controlam (true) ou não (false) validade."},
          "unit":{"type":"string","pattern":"^[a-z]{1,4}$","description":"Unidade de medida, ex.: cx, un, kg."},
          "limit":{"type":"integer","minimum":1,"maximum":50,"description":"Itens por página (padrão 20)."},
          "offset":{"type":"integer","minimum":0,"maximum":10000,"description":"Quantos itens pular (paginação)."}
        }}
        """;

    private const string SearchProductsDescription =
        "Pesquisa o catálogo de produtos, com ou sem nome, com ou sem SKU (completo ou parcial). Sem argumentos lista " +
        "todos. Use para descobrir SKUs pelo nome, ver produtos recém-cadastrados (has_stock=false) ou conferir se um " +
        "produto existe. Devolve saldo e disponível no armazém, total encontrado e has_more para paginar.";

    /// <summary>Lowercase without diacritics, for accent- and case-insensitive matching in SQL and in C#.</summary>
    internal static string Fold(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var builder = new StringBuilder(text.Length);
        foreach (var c in text.Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                builder.Append(char.ToLowerInvariant(c));
        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    /// <summary>Splits free text into at most <see cref="MaxSearchTerms"/> folded terms; hyphens alone are dropped.</summary>
    internal static IReadOnlyList<string> SearchTerms(string? query) =>
        string.IsNullOrWhiteSpace(query)
            ? []
            : TermSeparator().Split(Fold(query))
                .Select(t => t.Trim('-'))
                .Where(t => t.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .Take(MaxSearchTerms)
                .ToArray();

    /// <summary>"abc12" / "ABC-12" / "ABC" → canonical SKU prefix "ABC-12" / "ABC-12" / "ABC".</summary>
    internal static string NormalizeSkuFragment(string fragment)
    {
        var upper = fragment.ToUpperInvariant().Replace("-", "", StringComparison.Ordinal);
        var letters = new string(upper.TakeWhile(char.IsAsciiLetter).ToArray());
        var digits = upper[letters.Length..];
        return digits.Length == 0 ? letters : $"{letters}-{digits}";
    }

    /// <summary>Escapes LIKE wildcards so user text is always literal (ESCAPE '\').</summary>
    private static string LikeLiteral(string text) =>
        text.Replace(@"\", @"\\", StringComparison.Ordinal)
            .Replace("%", @"\%", StringComparison.Ordinal)
            .Replace("_", @"\_", StringComparison.Ordinal);

    /// <summary>Extra checks the JSON Schema cannot express exactly (\z instead of $).</summary>
    private static IEnumerable<string> InvalidSearchFields(JsonElement args)
    {
        if (args.TryGetProperty("sku", out var sku) && sku.ValueKind == JsonValueKind.String && !SkuFragment().IsMatch(sku.GetString()!))
            yield return "sku";
        if (args.TryGetProperty("unit", out var unit) && unit.ValueKind == JsonValueKind.String && !Unit().IsMatch(unit.GetString()!))
            yield return "unit";
        if (args.TryGetProperty("query", out var query) && query.ValueKind == JsonValueKind.String && SearchTerms(query.GetString()).Count == 0)
            yield return "query";
    }

    private static string SearchStatus(JsonElement args)
    {
        if (args.TryGetProperty("query", out var q) && q.ValueKind == JsonValueKind.String)
            return $"Pesquisando produtos: {Truncate(q.GetString()!, 40)}";
        if (args.TryGetProperty("sku", out var s) && s.ValueKind == JsonValueKind.String)
            return $"Pesquisando produtos {NormalizeSkuFragment(s.GetString()!)}";
        return "Pesquisando produtos";
    }

    private static ToolOutcome ExecuteSearchProducts(NexusDatabase database, JsonElement args, long warehouseId)
    {
        string? Text(string key) => args.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        bool? Flag(string key) => args.TryGetProperty(key, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.GetBoolean() : null;
        int Number(string key, int fallback) => args.TryGetProperty(key, out var v) && v.TryGetInt32(out var n) ? n : fallback;

        var terms = SearchTerms(Text("query"));
        var sku = Text("sku") is { } fragment ? NormalizeSkuFragment(fragment) : null;
        var hasStock = Flag("has_stock");
        var tracksExpiry = Flag("tracks_expiry");
        var unit = Text("unit");
        var limit = Math.Clamp(Number("limit", DefaultSearchLimit), 1, MaxRows);
        var offset = Math.Clamp(Number("offset", 0), 0, 10_000);

        using var connection = database.OpenAiReadOnly();
        connection.CreateFunction("nexus_fold", (string? value) => Fold(value), isDeterministic: true);

        // Every fragment below is a constant; user values only travel as parameters.
        var where = new List<string>();
        var parameters = new List<(string Name, object Value)> { ("$warehouse_id", warehouseId) };
        for (var i = 0; i < terms.Count; i++)
        {
            where.Add($"nexus_fold(p.sku || ' ' || REPLACE(p.sku, '-', '') || ' ' || p.name || ' ' || COALESCE(p.description, '')) LIKE $t{i} ESCAPE '\\'");
            parameters.Add(($"$t{i}", "%" + LikeLiteral(terms[i]) + "%"));
        }
        if (sku is not null)
        {
            where.Add("p.sku LIKE $sku_prefix ESCAPE '\\'");
            parameters.Add(("$sku_prefix", LikeLiteral(sku) + "%"));
        }
        if (hasStock is { } stocked)
            where.Add(stocked ? "COALESCE(s.quantity, 0) > 0" : "COALESCE(s.quantity, 0) = 0");
        if (tracksExpiry is { } tracks)
        {
            where.Add("p.tracks_expiry = $tracks_expiry");
            parameters.Add(("$tracks_expiry", tracks ? 1 : 0));
        }
        if (unit is not null)
        {
            where.Add("p.unit = $unit");
            parameters.Add(("$unit", unit));
        }

        const string from =
            "FROM v_ai_products p " +
            "LEFT JOIN (SELECT sku, SUM(quantity) AS quantity, SUM(available_qty) AS available " +
            "           FROM v_ai_stock WHERE warehouse_id = $warehouse_id GROUP BY sku) s ON s.sku = p.sku ";
        var filter = where.Count == 0 ? "" : "WHERE " + string.Join(" AND ", where) + " ";

        long total;
        using (var count = connection.CreateCommand())
        {
            count.CommandText = "SELECT COUNT(*) " + from + filter;
            foreach (var (name, value) in parameters) count.Parameters.AddWithValue(name, value);
            total = (long)count.ExecuteScalar()!;
        }

        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT p.sku, p.name, p.unit, p.description, p.weight_kg, p.tracks_expiry, " +
            "COALESCE(s.quantity, 0) AS quantity, COALESCE(s.available, 0) AS available " +
            from + filter +
            "ORDER BY CASE WHEN p.sku = $sku_exact THEN 0 " +
            "              WHEN $sku_exact IS NOT NULL AND p.sku LIKE $sku_like ESCAPE '\\' THEN 1 " +
            "              WHEN $first IS NOT NULL AND nexus_fold(p.name) LIKE $first ESCAPE '\\' THEN 2 " +
            "              ELSE 3 END, p.sku " +
            "LIMIT $limit OFFSET $offset";
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        command.Parameters.AddWithValue("$sku_exact", (object?)sku ?? DBNull.Value);
        command.Parameters.AddWithValue("$sku_like", sku is null ? DBNull.Value : LikeLiteral(sku) + "%");
        command.Parameters.AddWithValue("$first", terms.Count == 0 ? DBNull.Value : LikeLiteral(terms[0]) + "%");
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
                    ["name"] = reader.GetString(1),
                    ["unit"] = reader.GetString(2),
                    ["description"] = reader.IsDBNull(3) ? null : Truncate(reader.GetString(3), MaxDescriptionLength),
                    ["weight_kg"] = reader.IsDBNull(4) ? null : reader.GetDouble(4),
                    ["tracks_expiry"] = reader.GetInt64(5) == 1,
                    ["quantity"] = reader.GetInt64(6),
                    ["available"] = reader.GetInt64(7),
                });
            }
        }
        return SearchPage(items, total, offset);
    }

    /// <summary>One page of results within <see cref="MaxResultBytes"/>; has_more tells the model to page with offset.</summary>
    private static ToolOutcome SearchPage(List<JsonObject> items, long total, int offset)
    {
        while (true)
        {
            var node = new JsonObject
            {
                ["total"] = total,
                ["offset"] = offset,
                ["count"] = items.Count,
                ["has_more"] = offset + items.Count < total,
                ["items"] = new JsonArray(items.Select(r => (JsonNode?)r.DeepClone()).ToArray()),
            };
            if (total == 0)
                node["hint"] = "Nenhum produto encontrado. Tente menos termos, parte do nome ou só o início do SKU.";
            var text = Serialize(node);
            if (Encoding.UTF8.GetByteCount(text) <= MaxResultBytes || items.Count == 0)
                return new ToolOutcome(text, true, "ok");
            items = items.Take(items.Count / 2).ToList();
        }
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max] + "…";
}
