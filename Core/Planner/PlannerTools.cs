using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using erp_inteligente.Core.Data;
using erp_inteligente.Core.Engine;
using erp_inteligente.Core.Llm;
using erp_inteligente.Models;
using Json.Schema;
using Microsoft.Data.Sqlite;

namespace erp_inteligente.Core.Planner;

/// <summary>Result of running one tool: the JSON for the model and whether the call was accepted.</summary>
internal sealed record ToolOutcome(string Json, bool Allowed, string Code);

/// <summary>
/// The read tools of the Planner. Arguments are validated by JSON Schema (and a strict regex) before they touch the
/// database; every query is parameterized, runs on the AI read-only connection and is scoped to the session's warehouse.
/// </summary>
internal static partial class PlannerTools
{
    internal const int MaxRows = 50;
    internal const int MaxResultBytes = 8 * 1024;

    internal const string GetProduct = "get_product";
    internal const string GetStock = "get_stock";
    internal const string FindLocation = "find_location";
    internal const string ListInbound = "list_inbound_orders";
    internal const string ListOutbound = "list_outbound_orders";

    internal static readonly string[] ReadToolNames = [SearchProducts, GetProduct, GetStock, FindLocation, ListInbound, ListOutbound];

    private static readonly string[] InboundStatuses = ["expected", "receiving", "received", "cancelled"];
    private static readonly string[] OutboundStatuses = ["open", "reserved", "picking", "shipped", "cancelled"];

    private const string SkuRx = "^[A-Z]{3}-[0-9]{3}$";
    private const string LocationRx = "^[A-Z]{1,4}-[0-9]{2}(-[0-9]{2})?$";
    private const string ZoneRx = "^[A-Z]{1,4}$";

    // \z instead of $: the JSON Schema "$" would accept a trailing newline.
    [GeneratedRegex(@"^[A-Z]{3}-[0-9]{3}\z")] private static partial Regex Sku();
    [GeneratedRegex(@"^[A-Z]{1,4}-[0-9]{2}(-[0-9]{2})?\z")] private static partial Regex Location();
    [GeneratedRegex(@"^[A-Z]{1,4}\z")] private static partial Regex Zone();

    private static readonly EvaluationOptions EvalOptions = new() { OutputFormat = OutputFormat.List };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    private static readonly Dictionary<string, (string Description, string Schema)> Definitions = new(StringComparer.Ordinal)
    {
        [SearchProducts] = (SearchProductsDescription, SearchProductsSchema),
        [GetProduct] = ("Dados de um produto pelo SKU exato. Sem o SKU exato, use search_products.",
            ObjectSchema(("sku", $$"""{"type":"string","pattern":"{{SkuRx}}","description":"SKU, formato ABC-123"}"""), required: ["sku"])),
        [GetStock] = ("Saldo de estoque do armazém por SKU e/ou endereço (ao menos um dos dois).",
            ObjectSchema(
                ("sku", $$"""{"type":"string","pattern":"{{SkuRx}}"}"""),
                ("location", $$"""{"type":"string","pattern":"{{LocationRx}}","description":"Endereço, ex.: A-01-01 ou DOCA-01"}"""))),
        [FindLocation] = ("Endereços ativos do armazém com capacidade, ocupação e espaço livre.",
            ObjectSchema(
                ("zone", $$"""{"type":"string","pattern":"{{ZoneRx}}","description":"Código da zona, ex.: A"}"""),
                ("min_free", """{"type":"integer","minimum":1,"maximum":100000,"description":"Espaço livre mínimo"}"""))),
        [ListInbound] = ("Pedidos de entrada (recebimento) do armazém, uma linha por item.",
            ObjectSchema(("status", EnumSchema(InboundStatuses)))),
        [ListOutbound] = ("Pedidos de saída (expedição) do armazém, uma linha por item.",
            ObjectSchema(("status", EnumSchema(OutboundStatuses)))),
    };

    private static readonly Dictionary<string, JsonSchema> Compiled =
        Definitions.ToDictionary(d => d.Key, d => JsonSchema.FromText(d.Value.Schema), StringComparer.Ordinal);

    internal static IEnumerable<LlmTool> ReadTools() =>
        ReadToolNames.Select(n => new LlmTool(n, Definitions[n].Description, Definitions[n].Schema));

    internal static bool IsReadTool(string name) => Definitions.ContainsKey(name);

    /// <summary>Status line (Portuguese) for a read tool; arguments are already validated.</summary>
    internal static string StatusFor(string tool, JsonElement args)
    {
        string? Get(string key) => args.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        return tool switch
        {
            SearchProducts => SearchStatus(args),
            GetProduct => $"Consultando o produto {Get("sku")}",
            GetStock when Get("sku") is { } sku => $"Consultando saldo do {sku}",
            GetStock => $"Consultando o endereço {Get("location")}",
            FindLocation => "Procurando endereços livres",
            ListInbound => "Consultando pedidos de entrada",
            ListOutbound => "Consultando pedidos de saída",
            _ => "Consultando",
        };
    }

    /// <summary>Parses and validates the arguments of a read tool. Returns an error outcome, or null when valid.</summary>
    internal static ToolOutcome? Validate(string tool, string argumentsJson, out JsonElement args)
    {
        args = default;
        JsonElement parsed;
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson,
                new JsonDocumentOptions { MaxDepth = 8 });
            parsed = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return Error("invalid_arguments", "Os argumentos não são um JSON válido.");
        }
        if (parsed.ValueKind != JsonValueKind.Object)
            return Error("invalid_arguments", "Os argumentos devem ser um objeto JSON.");

        var results = Compiled[tool].Evaluate(parsed, EvalOptions);
        var bad = new SortedSet<string>(StringComparer.Ordinal);
        if (!results.IsValid)
        {
            foreach (var detail in results.Details ?? [])
                if (!detail.IsValid)
                    bad.Add(detail.InstanceLocation.ToString().TrimStart('/'));
        }
        // Defense in depth: the strict regexes reject what the JSON Schema "$" would let through (trailing newline).
        foreach (var (key, rx) in new[] { ("sku", Sku()), ("location", Location()), ("zone", Zone()) })
            if (tool != SearchProducts && parsed.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String && !rx.IsMatch(v.GetString()!))
                bad.Add(key);

        if (tool == SearchProducts && results.IsValid)
            bad.UnionWith(InvalidSearchFields(parsed));

        if (tool == GetStock && !parsed.TryGetProperty("sku", out _) && !parsed.TryGetProperty("location", out _))
            return Error("invalid_arguments", "Informe sku, location ou os dois.");
        if (!results.IsValid || bad.Count > 0)
        {
            var fields = bad.Where(f => f.Length is > 0 and <= 20 && f.All(c => char.IsAsciiLetterOrDigit(c) || c == '_')).ToArray();
            return Error("invalid_arguments", fields.Length > 0
                ? "Argumentos inválidos: " + string.Join(", ", fields) + ". Siga o schema da ferramenta."
                : "Argumentos inválidos. Siga o schema da ferramenta.");
        }
        args = parsed;
        return null;
    }

    /// <summary>Runs an already validated read tool.</summary>
    internal static ToolOutcome Execute(NexusDatabase database, string tool, JsonElement args, long warehouseId)
    {
        if (tool == SearchProducts)
            return ExecuteSearchProducts(database, args, warehouseId);

        using var connection = database.OpenAiReadOnly();
        using var command = connection.CreateCommand();
        string? Get(string key) => args.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        command.Parameters.AddWithValue("$warehouse_id", warehouseId);

        switch (tool)
        {
            case GetProduct:
                command.CommandText =
                    "SELECT sku, name, description, unit, weight_kg, tracks_expiry FROM v_ai_products WHERE sku = $sku LIMIT 1";
                command.Parameters.AddWithValue("$sku", Get("sku")!);
                break;
            case GetStock:
                command.CommandText =
                    "SELECT sku, product_name, location_code, zone_code, lot_code, expires_on, quantity, reserved_qty, available_qty " +
                    "FROM v_ai_stock WHERE warehouse_id = $warehouse_id AND ($sku IS NULL OR sku = $sku) " +
                    "AND ($location IS NULL OR location_code = $location) AND quantity <> 0 " +
                    "ORDER BY sku, location_code, lot_code LIMIT $limit";
                command.Parameters.AddWithValue("$sku", (object?)Get("sku") ?? DBNull.Value);
                command.Parameters.AddWithValue("$location", (object?)Get("location") ?? DBNull.Value);
                break;
            case FindLocation:
                command.CommandText =
                    "SELECT l.code AS location_code, l.zone_code, l.capacity, " +
                    "COALESCE((SELECT SUM(s.quantity) FROM v_ai_stock s WHERE s.warehouse_id = l.warehouse_id AND s.location_code = l.code), 0) AS occupied " +
                    "FROM v_ai_locations l WHERE l.warehouse_id = $warehouse_id AND l.is_active = 1 " +
                    "AND ($zone IS NULL OR l.zone_code = $zone) ORDER BY l.code";
                command.Parameters.AddWithValue("$zone", (object?)Get("zone") ?? DBNull.Value);
                break;
            case ListInbound:
                command.CommandText =
                    "SELECT document_number, status, expected_on, supplier_name, sku, expected_qty, received_qty " +
                    "FROM v_ai_inbound_orders WHERE warehouse_id = $warehouse_id AND ($status IS NULL OR status = $status) " +
                    "ORDER BY expected_on, document_number, sku LIMIT $limit";
                command.Parameters.AddWithValue("$status", (object?)Get("status") ?? DBNull.Value);
                break;
            case ListOutbound:
                command.CommandText =
                    "SELECT order_number, status, due_on, customer_name, sku, ordered_qty, picked_qty " +
                    "FROM v_ai_outbound_orders WHERE warehouse_id = $warehouse_id AND ($status IS NULL OR status = $status) " +
                    "ORDER BY due_on, order_number, sku LIMIT $limit";
                command.Parameters.AddWithValue("$status", (object?)Get("status") ?? DBNull.Value);
                break;
            default:
                return Error("unknown_tool", "Ferramenta desconhecida.");
        }
        if (command.CommandText.Contains("$limit", StringComparison.Ordinal))
            command.Parameters.AddWithValue("$limit", MaxRows + 1);

        var rows = new List<JsonObject>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var row = new JsonObject();
                for (var i = 0; i < reader.FieldCount; i++)
                    row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i) switch
                    {
                        long l => JsonValue.Create(l),
                        double d => JsonValue.Create(d),
                        var other => JsonValue.Create(Convert.ToString(other, CultureInfo.InvariantCulture)),
                    };
                rows.Add(row);
            }
        }

        if (tool == FindLocation)
        {
            var min = args.TryGetProperty("min_free", out var m) && m.TryGetInt64(out var mv) ? mv : 0;
            var shaped = new List<JsonObject>();
            foreach (var row in rows)
            {
                var capacity = row["capacity"]!.GetValue<long>();
                var occupied = row["occupied"]!.GetValue<long>();
                if (capacity - occupied < min) continue;
                row["free"] = capacity - occupied;
                shaped.Add(row);
            }
            rows = shaped;
        }
        return Rows(rows);
    }

    /// <summary>Packs rows as compact JSON: at most <see cref="MaxRows"/> rows and <see cref="MaxResultBytes"/> bytes.</summary>
    internal static ToolOutcome Rows(List<JsonObject> rows)
    {
        var truncated = rows.Count > MaxRows;
        if (truncated) rows = rows.Take(MaxRows).ToList();
        while (true)
        {
            var node = new JsonObject { ["count"] = rows.Count, ["rows"] = new JsonArray(rows.Select(r => (JsonNode?)r.DeepClone()).ToArray()) };
            if (truncated) node["truncated"] = true;
            var text = Serialize(node);
            if (System.Text.Encoding.UTF8.GetByteCount(text) <= MaxResultBytes || rows.Count == 0)
                return new ToolOutcome(text, true, "ok");
            rows = rows.Take(rows.Count / 2).ToList();
            truncated = true;
        }
    }

    internal static ToolOutcome Error(string code, string message) =>
        new(Serialize(new JsonObject { ["error"] = code, ["message"] = message }), false, code);

    /// <summary>Compact JSON in which '&lt;' and '&gt;' are escaped, so data can never close the surrounding data block.</summary>
    internal static string Serialize(JsonNode node) =>
        node.ToJsonString(JsonOptions).Replace("<", "\\u003c", StringComparison.Ordinal).Replace(">", "\\u003e", StringComparison.Ordinal);

    private static string EnumSchema(string[] values) =>
        new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray()) }.ToJsonString();

    private static string ObjectSchema(params (string Name, string Schema)[] properties) =>
        ObjectSchema(properties, []);

    private static string ObjectSchema((string Name, string Schema) property, string[] required) =>
        ObjectSchema([property], required);

    private static string ObjectSchema((string Name, string Schema)[] properties, string[] required)
    {
        var props = new JsonObject();
        foreach (var (name, schema) in properties) props[name] = JsonNode.Parse(schema);
        var root = new JsonObject { ["type"] = "object", ["properties"] = props, ["additionalProperties"] = false };
        if (required.Length > 0) root["required"] = new JsonArray(required.Select(r => (JsonNode?)JsonValue.Create(r)).ToArray());
        return root.ToJsonString();
    }
}
