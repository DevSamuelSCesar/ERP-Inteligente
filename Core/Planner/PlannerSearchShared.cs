using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace erp_inteligente.Core.Planner;

/// <summary>Helpers shared by the search tools (zones, locations, lots, orders, partners).</summary>
internal static partial class PlannerTools
{
    [GeneratedRegex(@"^[A-Za-z]{1,4}\z")] private static partial Regex ZoneFilter();
    [GeneratedRegex(@"^[0-9]{4}-[0-9]{2}-[0-9]{2}\z")] private static partial Regex IsoDate();

    private static string? ArgText(JsonElement args, string key) =>
        args.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool? ArgFlag(JsonElement args, string key) =>
        args.TryGetProperty(key, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;

    private static int ArgNumber(JsonElement args, string key, int fallback) =>
        args.TryGetProperty(key, out var v) && v.TryGetInt32(out var n) ? n : fallback;

    /// <summary>Common paging arguments: limit (1..50, default 20) and offset (0..10000).</summary>
    private static (int Limit, int Offset) ArgPage(JsonElement args) =>
        (Math.Clamp(ArgNumber(args, "limit", DefaultSearchLimit), 1, MaxRows), Math.Clamp(ArgNumber(args, "offset", 0), 0, 10_000));

    /// <summary>Adds one folded LIKE condition per free-text term over <paramref name="haystack"/> (a constant SQL expression).</summary>
    private static void AddTermConditions(IReadOnlyList<string> terms, string haystack, List<string> where, List<(string, object)> parameters)
    {
        for (var i = 0; i < terms.Count; i++)
        {
            where.Add($"nexus_fold({haystack}) LIKE $t{i} ESCAPE '\\'");
            parameters.Add(($"$t{i}", "%" + LikeLiteral(terms[i]) + "%"));
        }
    }

    /// <summary>Same as <see cref="AddTermConditions"/> for an aggregate (HAVING) expression.</summary>
    private static string TermsHaving(IReadOnlyList<string> terms, string haystack, List<(string, object)> parameters)
    {
        if (terms.Count == 0) return "1";
        var parts = new List<string>();
        for (var i = 0; i < terms.Count; i++)
        {
            parts.Add($"nexus_fold({haystack}) LIKE $t{i} ESCAPE '\\'");
            parameters.Add(($"$t{i}", "%" + LikeLiteral(terms[i]) + "%"));
        }
        return string.Join(" AND ", parts);
    }

    private static void Bind(SqliteCommand command, IEnumerable<(string Name, object Value)> parameters)
    {
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
    }

    private static object Nullable(object? value) => value ?? DBNull.Value;

    /// <summary>Checks the arguments the JSON Schema cannot express exactly (\z instead of $) for the new search tools.</summary>
    private static IEnumerable<string> InvalidSharedFields(string tool, JsonElement args)
    {
        if (ArgText(args, "query") is { } query && SearchTerms(query).Count == 0)
            yield return "query";
        if (ArgText(args, "sku") is { } sku && !SkuFragment().IsMatch(sku))
            yield return "sku";
        if (ArgText(args, "zone") is { } zone && !ZoneFilter().IsMatch(zone))
            yield return "zone";
        if (ArgText(args, "expires_before") is { } date &&
            !(IsoDate().IsMatch(date) && DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)))
            yield return "expires_before";
    }

    private static string SearchStatusFor(string label, JsonElement args)
    {
        if (ArgText(args, "query") is { } q) return $"Pesquisando {label}: {Truncate(q, 40)}";
        return $"Pesquisando {label}";
    }

    /// <summary>
    /// One page of results within <see cref="MaxResultBytes"/>; has_more tells the model to page with offset.
    /// <paramref name="extra"/> adds top-level properties that are never trimmed (e.g. next_free_codes).
    /// </summary>
    private static ToolOutcome SearchPage(List<JsonObject> items, long total, int offset, string emptyHint, JsonObject? extra = null)
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
            if (total == 0) node["hint"] = emptyHint;
            if (extra is not null)
                foreach (var (key, value) in extra) node[key] = value?.DeepClone();
            var text = Serialize(node);
            if (Encoding.UTF8.GetByteCount(text) <= MaxResultBytes || items.Count == 0)
                return new ToolOutcome(text, true, "ok");
            items = items.Take(items.Count / 2).ToList();
        }
    }
}
