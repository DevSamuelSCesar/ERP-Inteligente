using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json.Nodes;
using Json.Schema;

namespace erp_inteligente.Core.Engine;

/// <summary>
/// Validates the arguments of every step against the JSON Schema of its operation and reports problems in
/// Portuguese business language (no JSON jargon, no untrusted text echoed back except sanitized names).
/// </summary>
public static class SchemaValidator
{
    private static readonly EvaluationOptions Options = new() { OutputFormat = OutputFormat.List };

    // field -> (article + label, capitalised article + label, kind of value)
    private static readonly Dictionary<string, (string Label, string Article, Kind Kind)> Fields = new(StringComparer.Ordinal)
    {
        ["sku"] = ("SKU", "o", Kind.Text),
        ["qty"] = ("quantidade", "a", Kind.Integer),
        ["location"] = ("endereço", "o", Kind.Text),
        ["from"] = ("origem", "a", Kind.Text),
        ["to"] = ("destino", "o", Kind.Text),
        ["lot"] = ("lote", "o", Kind.Text),
        ["reason"] = ("motivo", "o", Kind.Text),
        ["name"] = ("nome", "o", Kind.Text),
        ["unit"] = ("unidade", "a", Kind.Text),
        ["capacity"] = ("capacidade", "a", Kind.Integer),
        ["zone"] = ("zona", "a", Kind.Text),
        ["code"] = ("código", "o", Kind.Text),
        ["expires_on"] = ("validade", "a", Kind.Text),
        ["inbound_order"] = ("pedido de entrada", "o", Kind.Text),
        ["unit_cost"] = ("custo unitário", "o", Kind.Number),
        ["weight_kg"] = ("peso", "o", Kind.Number),
        ["description"] = ("descrição", "a", Kind.Text),
        ["tracks_expiry"] = ("controle de validade", "o", Kind.Boolean),
    };

    private static readonly Dictionary<string, string> PatternHints = new(StringComparer.Ordinal)
    {
        ["sku"] = "ter o formato ABC-123",
        ["location"] = "ter o formato A-01-01 ou DOCA-01",
        ["from"] = "ter o formato A-01-01 ou DOCA-01",
        ["to"] = "ter o formato A-01-01 ou DOCA-01",
        ["code"] = "ter o formato A-01-01 ou DOCA-01",
        ["unit"] = "ter de 1 a 4 letras minúsculas (por exemplo, un ou kg)",
        ["zone"] = "ter de 1 a 4 letras maiúsculas (por exemplo, A)",
        ["expires_on"] = "ter o formato AAAA-MM-DD",
    };

    private static readonly Dictionary<string, JsonNode?> SchemaNodes = BuildSchemaNodes();

    private enum Kind { Text, Integer, Number, Boolean }

    /// <summary>Validates all steps; an empty list means every step is schema-valid.</summary>
    public static IReadOnlyList<PlanError> Validate(PlanDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        var errors = new List<PlanError>();
        foreach (var step in doc.Steps)
        {
            if (!OperationCatalog.TryGet(step.Op, out var definition))
            {
                errors.Add(new PlanError(step.Index, "op", "dsl.unknown_op", $"Passo {step.Index + 1}: operação desconhecida."));
                continue;
            }
            ValidateStep(step, definition, errors);
        }
        return errors;
    }

    /// <summary>Portuguese label of an argument (for messages); unknown names get a neutral label.</summary>
    internal static string LabelOf(string? field) =>
        field is not null && Fields.TryGetValue(field, out var f) ? f.Label : "um campo";

    private static void ValidateStep(PlanStepDocument step, OperationDefinition definition, List<PlanError> errors)
    {
        var n = step.Index + 1;
        var schemaNode = SchemaNodes[definition.Name]!;
        var properties = (JsonObject)schemaNode["properties"]!;
        var unknown = new HashSet<string>(StringComparer.Ordinal);
        var seen = new HashSet<(string?, string)>();

        void Add(string? field, string code, string message)
        {
            if (seen.Add((field, code)))
                errors.Add(new PlanError(step.Index, field, code, message));
        }

        // Unknown arguments are reported by name (sanitized) and excluded from the rest.
        foreach (var (name, _) in step.Args)
        {
            if (!properties.ContainsKey(name))
            {
                unknown.Add(name);
                Add(name, "schema.additional_property", $"Passo {n}: o campo \"{Sanitize(name)}\" não existe na operação {definition.Label}.");
            }
        }

        var results = definition.ArgsSchema.Evaluate(System.Text.Json.JsonSerializer.SerializeToElement(step.Args), Options);
        if (results.IsValid && unknown.Count == 0 && !HasTrailingNewlineInPatternField(step.Args, properties))
            return;

        foreach (var detail in results.Details ?? [])
        {
            if (detail.IsValid || detail.Errors is null)
                continue;
            var path = detail.EvaluationPath.ToString();
            if (path.Contains("/anyOf/", StringComparison.Ordinal) || path.EndsWith("/not", StringComparison.Ordinal) || path.Contains("/not/", StringComparison.Ordinal))
                continue; // sub-schema noise: the anyOf / not node itself is reported once

            var pointer = detail.InstanceLocation.ToString();
            var field = FieldFromPointer(pointer);
            if (field is not null && unknown.Contains(field))
                continue;

            foreach (var (keyword, _) in detail.Errors)
            {
                switch (keyword)
                {
                    case "required":
                        foreach (var name in Required(schemaNode))
                            if (!step.Args.ContainsKey(name))
                                Add(name, "schema.required", $"Passo {n}: falta {Article(name)} {LabelOf(name)}.");
                        break;
                    case "additionalProperties":
                    case "false":
                    case "properties":
                        break; // handled above / aggregate of the per-property errors
                    default:
                        if (field is null)
                            Add(null, "schema." + keyword, $"Passo {n}: os dados da operação estão inválidos.");
                        else
                            Add(field, "schema." + keyword, Describe(keyword, n, field, step.Args[field], properties));
                        break;
                }
            }
        }

        foreach (var (name, value) in step.Args)
            if (!unknown.Contains(name) && !errors.Exists(e => e.StepIndex == step.Index && e.Field == name)
                && EndsWithNewlineWhereAnchored(name, value, properties))
                Add(name, "schema.pattern", $"Passo {n}: {Article(name)} {LabelOf(name)} deve {PatternHints.GetValueOrDefault(name, "ter o formato esperado")}.");

        // A pattern violation already says everything; drop the redundant length error of the same field.
        errors.RemoveAll(e => e.StepIndex == step.Index && e.Code == "schema.maxLength"
            && errors.Exists(o => o.StepIndex == step.Index && o.Field == e.Field && o.Code == "schema.pattern"));

        if (!results.IsValid && !HasAny(errors, step.Index))
            errors.Add(new PlanError(step.Index, null, "schema.invalid", $"Passo {n}: os dados da operação estão inválidos."));
    }

    private static string Describe(string keyword, int n, string field, JsonNode? value, JsonObject properties)
    {
        var label = LabelOf(field);
        var art = Article(field);
        var cap = $"{n}";
        var prop = properties[field] as JsonObject;
        switch (keyword)
        {
            case "type":
                return $"Passo {cap}: {art} {label} deve ser {KindText(field)}.";
            case "anyOf":
                if (value is not JsonValue v || !v.TryGetValue<string>(out _))
                    return $"Passo {cap}: {art} {label} deve ser um texto.";
                return $"Passo {cap}: {art} {label} deve {PatternHints.GetValueOrDefault(field, "ter o formato esperado")} ou ser o resultado de um passo anterior.";
            case "pattern":
                return $"Passo {cap}: {art} {label} deve {PatternHints.GetValueOrDefault(field, "ter o formato esperado")}.";
            case "minimum":
                return $"Passo {cap}: {art} {label} deve ser no mínimo {Bound(prop, "minimum")}.";
            case "maximum":
                return $"Passo {cap}: {art} {label} deve ser no máximo {Bound(prop, "maximum")}.";
            case "minLength":
                return $"Passo {cap}: {art} {label} deve ter no mínimo {Bound(prop, "minLength")} caractere(s).";
            case "maxLength":
                return $"Passo {cap}: {art} {label} deve ter no máximo {Bound(prop, "maxLength")} caracteres.";
            case "not":
                return $"Passo {cap}: {art} {label} não pode ser zero.";
            default:
                return $"Passo {cap}: {art} {label} tem um valor inválido.";
        }
    }

    private static string KindText(string field) =>
        (Fields.TryGetValue(field, out var f) ? f.Kind : Kind.Text) switch
        {
            Kind.Integer => "um número inteiro",
            Kind.Number => "um número",
            Kind.Boolean => "sim ou não",
            _ => "um texto",
        };

    private static string Article(string field) => Fields.TryGetValue(field, out var f) ? f.Article : "o";

    private static string Bound(JsonObject? prop, string keyword)
    {
        var node = prop?[keyword];
        return node is JsonValue v && v.TryGetValue<decimal>(out var d) ? d.ToString(CultureInfo.InvariantCulture) : "?";
    }

    private static IEnumerable<string> Required(JsonNode schema)
    {
        if (schema["required"] is JsonArray array)
            foreach (var item in array)
                if (item is JsonValue v && v.TryGetValue<string>(out var s))
                    yield return s;
    }

    private static string? FieldFromPointer(string pointer)
    {
        if (pointer.Length <= 1 || pointer[0] != '/')
            return null;
        var rest = pointer[1..];
        var slash = rest.IndexOf('/', StringComparison.Ordinal);
        if (slash >= 0)
            rest = rest[..slash];
        return rest.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
    }

    private static bool HasAny(List<PlanError> errors, int index)
    {
        foreach (var e in errors)
            if (e.StepIndex == index)
                return true;
        return false;
    }

    // Patterns are anchored with $, which also matches before a final newline; maxLength usually
    // catches that, but short references ("$abc\n") would slip through, so it is checked explicitly.
    private static bool HasTrailingNewlineInPatternField(JsonObject args, JsonObject properties)
    {
        foreach (var (name, value) in args)
            if (EndsWithNewlineWhereAnchored(name, value, properties))
                return true;
        return false;
    }

    private static bool EndsWithNewlineWhereAnchored(string name, JsonNode? value, JsonObject properties) =>
        value is JsonValue v && v.TryGetValue<string>(out var text) && text.EndsWith('\n')
        && properties[name] is JsonObject prop && prop.ToJsonString().Contains("\"pattern\"", StringComparison.Ordinal);

    private static string Sanitize(string text)
    {
        var chars = new List<char>(Math.Min(text.Length, 40));
        foreach (var c in text)
        {
            if (chars.Count == 40)
                break;
            chars.Add(c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_' ? c : '?');
        }
        return new string(chars.ToArray());
    }

    private static Dictionary<string, JsonNode?> BuildSchemaNodes()
    {
        var nodes = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        foreach (var op in OperationCatalog.V1)
            nodes[op.Name] = JsonNode.Parse(OperationCatalog.SchemaJson(op.Name));
        return nodes;
    }
}
