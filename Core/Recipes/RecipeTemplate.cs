using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using erp_inteligente.Core.Engine;
using Json.Schema;

namespace erp_inteligente.Core.Recipes;

/// <summary>
/// Turns an executed plan into a parameterized template and back (references/ARQUITETURA.md, section 7). In a template
/// a string <c>"$name"</c> is a parameter when <c>name</c> is a property of the parameter schema; any other <c>"$x"</c> is
/// the usual DSL reference to an earlier step. Extraction never lets the two share a name.
/// </summary>
public static partial class RecipeTemplate
{
    public const int MaxParameters = 30;

    [GeneratedRegex(@"^[a-z][a-z0-9_]{0,31}\z")]
    private static partial Regex ParameterName();

    private static readonly EvaluationOptions EvalOptions = new() { OutputFormat = OutputFormat.List };

    /// <summary>
    /// The structure of a plan: one <c>op(arg,...)</c> per step, arguments sorted, references and result names replaced by
    /// the step they point to (<c>zone=@1</c>, <c>&gt;@1</c>), so the names the planner chose do not matter.
    /// </summary>
    public static string Signature(PlanDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var position = document.Steps.Where(s => s.As is not null).ToDictionary(s => s.As!, s => s.Index + 1, StringComparer.Ordinal);
        var parts = document.Steps.Select(step =>
        {
            var args = step.Args.Select(p => p.Key).OrderBy(k => k, StringComparer.Ordinal).Select(key =>
                ReferenceOf(step.Args[key]) is { } reference && position.TryGetValue(reference, out var at) ? $"{key}=@{at}" : key);
            var text = $"{step.Op}({string.Join(",", args)})";
            return step.As is null ? text : text + ">@" + (step.Index + 1).ToString(CultureInfo.InvariantCulture);
        });
        return string.Join(";", parts);
    }

    /// <summary>
    /// Every literal argument of every step becomes a required parameter (named after the argument, or
    /// <c>argument_N</c> when it repeats), typed by the operation's own JSON Schema; references stay as they are.
    /// </summary>
    public static ExtractedTemplate Extract(PlanDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var resultNames = new HashSet<string>(document.Steps.Where(s => s.As is not null).Select(s => s.As!), StringComparer.Ordinal);
        var literalKeys = document.Steps
            .SelectMany(s => s.Args.Where(p => ReferenceOf(p.Value) is null).Select(p => p.Key))
            .GroupBy(k => k, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        var properties = new JsonObject();
        var required = new JsonArray();
        var example = new JsonObject();
        var steps = new JsonArray();
        foreach (var step in document.Steps)
        {
            var schema = JsonNode.Parse(OperationCatalog.SchemaJson(step.Op))!["properties"] as JsonObject;
            var args = new JsonObject();
            foreach (var (key, value) in step.Args)
            {
                if (ReferenceOf(value) is not null)
                {
                    args[key] = value!.DeepClone();
                    continue;
                }
                var name = literalKeys[key] > 1 ? $"{key}_{step.Index + 1}" : key;
                while (resultNames.Contains(name) || properties.ContainsKey(name)) name += "_v";
                if (!ParameterName().IsMatch(name))
                    throw new RecipeStoreException("O plano tem um argumento que não pode virar parâmetro de receita.");

                properties[name] = ParameterSchema(schema?[key], name);
                required.Add(name);
                example[name] = value?.DeepClone();
                args[key] = "$" + name;
            }
            var node = new JsonObject { ["op"] = step.Op, ["args"] = args };
            if (step.As is not null) node["as"] = step.As;
            steps.Add(node);
        }
        if (properties.Count > MaxParameters)
            throw new RecipeStoreException($"O plano tem mais de {MaxParameters} valores variáveis para virar receita.");

        var template = new JsonObject { ["intent"] = document.Intent, ["steps"] = steps };
        var parameters = new JsonObject
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["required"] = required,
            ["properties"] = properties,
        };
        return new ExtractedTemplate(template.ToJsonString(), parameters.ToJsonString(), example, Signature(document));
    }

    /// <summary>The parameters of a schema in declaration order, with Portuguese labels and the example values as text.</summary>
    public static IReadOnlyList<RecipeParameter> Parameters(string paramsSchemaJson, string? exampleJson)
    {
        if (JsonNode.Parse(paramsSchemaJson) is not JsonObject schema || schema["properties"] is not JsonObject properties)
            return [];
        var required = (schema["required"] as JsonArray)?.Select(r => r?.GetValue<string>()).ToHashSet() ?? [];
        var example = exampleJson is null ? null : JsonNode.Parse(exampleJson) as JsonObject;
        return properties.Select(p =>
        {
            var type = TypeOf(p.Value);
            var label = p.Value?["title"] is JsonValue t && t.TryGetValue<string>(out var title) ? title : Label(p.Key);
            return new RecipeParameter(p.Key, label, type, required.Contains(p.Key), FormText(example?[p.Key]));
        }).ToList();
    }

    /// <summary>The operations of a template, in step order.</summary>
    public static IReadOnlyList<string> Operations(string templateJson) =>
        JsonNode.Parse(templateJson)?["steps"] is JsonArray steps
            ? steps.Select(s => s?["op"] is JsonValue v && v.TryGetValue<string>(out var op) ? op : "?").ToList()
            : [];

    /// <summary>
    /// Validates <paramref name="parameters"/> against the recipe's schema and fills the template. The result is a full
    /// DSL document; the engine still validates, simulates and approves it like any other plan.
    /// </summary>
    public static RecipeInstance Instantiate(RecipeRecord recipe, JsonObject parameters)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        ArgumentNullException.ThrowIfNull(parameters);
        var errors = new List<string>();
        var schemaNode = JsonNode.Parse(recipe.ParamsSchemaJson) as JsonObject;
        var known = (schemaNode?["properties"] as JsonObject)?.Select(p => p.Key).ToHashSet(StringComparer.Ordinal) ?? [];

        var results = JsonSchema.FromText(recipe.ParamsSchemaJson).Evaluate(JsonSerializer.SerializeToElement(parameters), EvalOptions);
        if (!results.IsValid)
        {
            var labels = recipe.Parameters.ToDictionary(p => p.Name, p => p.Label, StringComparer.Ordinal);
            var bad = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var detail in results.Details ?? [])
                if (!detail.IsValid) bad.Add(detail.InstanceLocation.ToString().TrimStart('/').Split('/')[0]);
            foreach (var missing in recipe.Parameters.Where(p => p.Required && !parameters.ContainsKey(p.Name)))
                errors.Add($"Informe {missing.Label}.");
            foreach (var name in bad.Where(n => n.Length > 0))
                errors.Add(known.Contains(name) && labels.TryGetValue(name, out var label)
                    ? $"Valor inválido para {label}."
                    : "A receita recebeu um parâmetro desconhecido.");
            if (errors.Count == 0) errors.Add("Os parâmetros não conferem com a receita.");
            return new RecipeInstance(null, errors.Distinct().ToList());
        }

        if (JsonNode.Parse(recipe.TemplateJson) is not JsonObject template || template["steps"] is not JsonArray steps)
            return new RecipeInstance(null, ["O modelo da receita está corrompido."]);

        foreach (var step in steps)
            if (step?["args"] is JsonObject args)
                foreach (var key in args.Select(p => p.Key).ToArray())
                    args[key] = Fill(args[key], parameters, known);

        var plan = new JsonObject
        {
            ["dsl_version"] = recipe.DslVersion,
            ["intent"] = recipe.Intent,
            ["skill"] = recipe.Skill,
            ["steps"] = steps.DeepClone(),
        };
        if (recipe.Skill is null) plan.Remove("skill");
        return new RecipeInstance(plan.ToJsonString(), []);
    }

    /// <summary>
    /// Converts what was typed in a form field to the parameter's JSON type. Returns false with a pt-BR message when the
    /// text does not fit (a letter in an integer, invalid JSON in a list).
    /// </summary>
    public static bool TryConvert(RecipeParameter parameter, string? text, out JsonNode? value, out string? error)
    {
        ArgumentNullException.ThrowIfNull(parameter);
        value = null;
        error = null;
        var trimmed = (text ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            error = $"Informe {parameter.Label}.";
            return false;
        }
        switch (parameter.Type)
        {
            case "integer":
                if (long.TryParse(trimmed, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var whole))
                {
                    value = JsonValue.Create(whole);
                    return true;
                }
                error = $"{Capitalize(parameter.Label)} deve ser um número inteiro.";
                return false;
            case "number":
                if (double.TryParse(trimmed.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number))
                {
                    value = JsonValue.Create(number);
                    return true;
                }
                error = $"{Capitalize(parameter.Label)} deve ser um número.";
                return false;
            case "boolean":
                var folded = trimmed.ToLowerInvariant();
                if (folded is "sim" or "true" or "s") { value = JsonValue.Create(true); return true; }
                if (folded is "não" or "nao" or "false" or "n") { value = JsonValue.Create(false); return true; }
                error = $"{Capitalize(parameter.Label)}: responda sim ou não.";
                return false;
            case "array":
            case "object":
                try
                {
                    value = JsonNode.Parse(trimmed, null, new JsonDocumentOptions { MaxDepth = 8 });
                    if ((parameter.Type == "array" && value is JsonArray) || (parameter.Type == "object" && value is JsonObject)) return true;
                }
                catch (JsonException)
                {
                }
                value = null;
                error = $"{Capitalize(parameter.Label)} deve seguir o formato do exemplo.";
                return false;
            default:
                value = JsonValue.Create(trimmed);
                return true;
        }
    }

    private static JsonNode? Fill(JsonNode? node, JsonObject parameters, HashSet<string> known)
    {
        switch (node)
        {
            case JsonValue v when v.TryGetValue<string>(out var text) && text.StartsWith('$') && known.Contains(text[1..]):
                return parameters[text[1..]]?.DeepClone();
            case JsonObject obj:
                var copy = new JsonObject();
                foreach (var (key, child) in obj) copy[key] = Fill(child, parameters, known);
                return copy;
            case JsonArray array:
                return new JsonArray(array.Select(child => Fill(child, parameters, known)).ToArray());
            default:
                return node?.DeepClone();
        }
    }

    /// <summary>The schema of one parameter: the operation's own, without the "$reference" alternative, plus a title.</summary>
    private static JsonNode ParameterSchema(JsonNode? opSchema, string parameterName)
    {
        JsonObject result;
        if (opSchema?["anyOf"] is JsonArray choices)
        {
            var literal = choices.OfType<JsonObject>().FirstOrDefault(c =>
                !(c["pattern"] is JsonValue p && p.TryGetValue<string>(out var pattern) && pattern.StartsWith(@"^\$", StringComparison.Ordinal)));
            result = (literal?.DeepClone() as JsonObject) ?? new JsonObject();
            if (opSchema["description"] is { } description) result["description"] = description.DeepClone();
        }
        else
            result = opSchema?.DeepClone() as JsonObject ?? new JsonObject();
        result["title"] = Label(parameterName);
        return result;
    }

    private static string Label(string key)
    {
        var baseKey = Regex.Replace(key, @"(_[0-9]+)?(_v)*\z", "");
        var label = baseKey switch
        {
            // Names used by hand-written recipes (the seeded armazenar_da_doca) that are not DSL arguments.
            "dock" => "doca",
            "target" => "endereço de destino",
            _ => SchemaValidator.LabelOf(baseKey),
        };
        if (label == "um campo") label = baseKey;
        var suffix = key.Length > baseKey.Length && Regex.Match(key, @"_([0-9]+)") is { Success: true } m ? $" (passo {m.Groups[1].Value})" : "";
        return label + suffix;
    }

    private static string TypeOf(JsonNode? schema) =>
        schema?["type"] is JsonValue v && v.TryGetValue<string>(out var type) ? type : "string";

    private static string? FormText(JsonNode? value) => value switch
    {
        null => null,
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonValue v when v.TryGetValue<bool>(out var b) => b ? "sim" : "não",
        _ => value.ToJsonString(),
    };

    private static string? ReferenceOf(JsonNode? value) =>
        value is JsonValue v && v.TryGetValue<string>(out var text) && DslParser.ReferencePattern().IsMatch(text) ? text[1..] : null;

    private static string Capitalize(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}
