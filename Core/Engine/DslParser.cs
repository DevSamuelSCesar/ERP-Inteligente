using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace erp_inteligente.Core.Engine;

/// <summary>Result of <see cref="DslParser.Parse"/>: a document when there are no errors, otherwise the errors.</summary>
public sealed record DslParseResult(PlanDocument? Document, IReadOnlyList<PlanError> Errors);

/// <summary>
/// Parses the plan DSL v1 (references/ARQUITETURA.md, section 5). Everything is untrusted: size, depth,
/// shape and references are checked here; argument contents are checked by <see cref="SchemaValidator"/>.
/// </summary>
public static partial class DslParser
{
    /// <summary>The only DSL version this parser understands.</summary>
    public const string SupportedVersion = "1.0";

    /// <summary>Maximum size of a plan document, in UTF-8 bytes.</summary>
    public const int MaxBytes = 64 * 1024;

    /// <summary>Maximum number of steps.</summary>
    public const int MaxSteps = 50;

    /// <summary>Maximum JSON nesting depth.</summary>
    public const int MaxDepth = 16;

    private const int DiagnosticDepth = 128;

    private static readonly string[] RootProperties = ["dsl_version", "intent", "skill", "steps"];
    private static readonly string[] StepProperties = ["op", "args", "as"];

    // \z (not $) so that a trailing newline never matches.
    [GeneratedRegex(@"^[a-z_]{1,40}\z")]
    private static partial Regex NamePattern();

    [GeneratedRegex(@"^[a-z][a-z0-9_]{0,31}\z")]
    private static partial Regex ResultNamePattern();

    [GeneratedRegex(@"^\$[a-z][a-z0-9_]{0,31}\z")]
    internal static partial Regex ReferencePattern();

    /// <summary>Parses a plan document. All problems found are returned, not only the first.</summary>
    /// <param name="json">The plan text.</param>
    /// <param name="storedDslVersion">Version stored beside the plan (<c>plans.dsl_version</c>), used when the document has none.</param>
    public static DslParseResult Parse(string json, string? storedDslVersion = null)
    {
        if (json is null || Encoding.UTF8.GetByteCount(json) > MaxBytes)
            return Fail(null, null, "dsl.too_large", "O plano é grande demais para ser analisado.");

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json, null, Options(MaxDepth));
        }
        catch (JsonException)
        {
            return Fail(null, null, IsTooDeep(json) ? "dsl.too_deep" : "dsl.invalid_json",
                IsTooDeep(json) ? "O plano tem aninhamento demais." : "O plano não é um documento válido.");
        }

        if (root is not JsonObject obj)
            return Fail(null, null, "dsl.invalid_structure", "O plano deve ser um documento com intenção e passos.");

        var errors = new List<PlanError>();

        foreach (var (name, _) in obj)
            if (Array.IndexOf(RootProperties, name) < 0)
                errors.Add(new PlanError(null, null, "dsl.unknown_property", "O plano contém um item desconhecido."));

        var version = ReadVersion(obj, storedDslVersion, errors);
        var intent = ReadName(obj, "intent", required: true, "intenção", errors);
        var skill = ReadName(obj, "skill", required: false, "skill", errors);
        var steps = ReadSteps(obj, errors);

        if (errors.Count > 0 || version is null || intent is null || steps is null)
            return new DslParseResult(null, errors);
        return new DslParseResult(new PlanDocument(version, intent, skill, steps), errors);
    }

    private static string? ReadVersion(JsonObject obj, string? stored, List<PlanError> errors)
    {
        string? version;
        if (obj.TryGetPropertyValue("dsl_version", out var node) && node is not null)
        {
            if (!TryString(node, out version))
            {
                errors.Add(new PlanError(null, "dsl_version", "dsl.bad_version", "A versão do formato do plano é inválida."));
                return null;
            }
        }
        else
        {
            version = stored;
            if (version is null)
            {
                errors.Add(new PlanError(null, "dsl_version", "dsl.missing_version", "O plano não informa a versão do formato."));
                return null;
            }
        }
        if (!string.Equals(version, SupportedVersion, StringComparison.Ordinal))
        {
            errors.Add(new PlanError(null, "dsl_version", "dsl.bad_version", "A versão do formato do plano não é suportada."));
            return null;
        }
        return version;
    }

    private static string? ReadName(JsonObject obj, string field, bool required, string label, List<PlanError> errors)
    {
        var present = obj.TryGetPropertyValue(field, out var node) && node is not null;
        if (!present)
        {
            if (required)
                errors.Add(new PlanError(null, field, "dsl.missing_property", $"O plano não informa a {label}."));
            return null;
        }
        if (!TryString(node!, out var value) || !NamePattern().IsMatch(value))
        {
            errors.Add(new PlanError(null, field, "dsl.bad_name",
                $"A {label} do plano deve ter de 1 a 40 letras minúsculas ou sublinhados."));
            return null;
        }
        return value;
    }

    private static List<PlanStepDocument>? ReadSteps(JsonObject obj, List<PlanError> errors)
    {
        if (!obj.TryGetPropertyValue("steps", out var node) || node is not JsonArray array)
        {
            errors.Add(new PlanError(null, "steps", "dsl.bad_steps", "O plano deve ter uma lista de passos."));
            return null;
        }
        if (array.Count < 1 || array.Count > MaxSteps)
        {
            errors.Add(new PlanError(null, "steps", "dsl.bad_steps", $"O plano deve ter de 1 a {MaxSteps} passos."));
            return null;
        }

        var steps = new List<PlanStepDocument>(array.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var ok = true;
        for (var i = 0; i < array.Count; i++)
        {
            var step = ReadStep(i, array[i], seen, errors);
            if (step is null)
                ok = false;
            else
                steps.Add(step);
        }
        return ok ? steps : null;
    }

    private static PlanStepDocument? ReadStep(int index, JsonNode? node, HashSet<string> earlier, List<PlanError> errors)
    {
        var n = index + 1;
        if (node is not JsonObject step)
        {
            errors.Add(new PlanError(index, null, "dsl.invalid_structure", $"Passo {n}: o passo está mal formado."));
            return null;
        }

        var ok = true;
        foreach (var (name, _) in step)
        {
            if (Array.IndexOf(StepProperties, name) < 0)
            {
                errors.Add(new PlanError(index, null, "dsl.unknown_property", $"Passo {n}: o passo contém um item desconhecido."));
                ok = false;
            }
        }

        string? op = null;
        if (!step.TryGetPropertyValue("op", out var opNode) || opNode is null || !TryString(opNode, out op))
        {
            errors.Add(new PlanError(index, "op", "dsl.missing_op", $"Passo {n}: falta a operação."));
            ok = false;
        }
        else if (!OperationCatalog.TryGet(op, out _))
        {
            errors.Add(new PlanError(index, "op", "dsl.unknown_op", $"Passo {n}: operação desconhecida."));
            ok = false;
        }

        JsonObject? args = null;
        if (!step.TryGetPropertyValue("args", out var argsNode) || argsNode is not JsonObject argsObject)
        {
            errors.Add(new PlanError(index, "args", "dsl.bad_args", $"Passo {n}: faltam os dados da operação."));
            ok = false;
        }
        else
        {
            args = argsObject;
            ok &= CheckReferences(index, args, earlier, errors);
        }

        string? alias = null;
        if (step.TryGetPropertyValue("as", out var asNode) && asNode is not null)
        {
            if (!TryString(asNode, out alias) || !ResultNamePattern().IsMatch(alias))
            {
                errors.Add(new PlanError(index, "as", "dsl.bad_as", $"Passo {n}: o nome do resultado é inválido."));
                alias = null;
                ok = false;
            }
            else if (!earlier.Add(alias))
            {
                errors.Add(new PlanError(index, "as", "dsl.duplicate_as", $"Passo {n}: o nome do resultado já foi usado em outro passo."));
                ok = false;
            }
        }

        return ok && op is not null && args is not null ? new PlanStepDocument(index, op, (JsonObject)args.DeepClone(), alias) : null;

    }

    /// <summary>Every <c>$name</c> string in the args must name the <c>as</c> of an earlier step.</summary>
    private static bool CheckReferences(int index, JsonNode? node, HashSet<string> earlier, List<PlanError> errors)
    {
        var ok = true;
        foreach (var (field, text) in Strings(node, null, 0))
        {
            if (!ReferencePattern().IsMatch(text))
                continue;
            if (!earlier.Contains(text[1..]))
            {
                errors.Add(new PlanError(index, field, "dsl.bad_reference",
                    $"Passo {index + 1}: a referência usada em {SchemaValidator.LabelOf(field)} não aponta para o resultado de um passo anterior."));
                ok = false;
            }
        }
        return ok;
    }

    /// <summary>Enumerates string leaves with their top-level property name.</summary>
    private static IEnumerable<(string? Field, string Text)> Strings(JsonNode? node, string? field, int depth)
    {
        if (depth > MaxDepth)
            yield break;
        switch (node)
        {
            case JsonObject o:
                foreach (var (name, child) in o)
                    foreach (var item in Strings(child, field ?? name, depth + 1))
                        yield return item;
                break;
            case JsonArray a:
                foreach (var child in a)
                    foreach (var item in Strings(child, field, depth + 1))
                        yield return item;
                break;
            case JsonValue v when v.TryGetValue<string>(out var text):
                yield return (field, text);
                break;
        }
    }

    private static bool TryString(JsonNode node, out string value)
    {
        if (node is JsonValue v && v.TryGetValue<string>(out var s))
        {
            value = s;
            return true;
        }
        value = string.Empty;
        return false;
    }

    private static JsonDocumentOptions Options(int maxDepth) => new()
    {
        MaxDepth = maxDepth,
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        AllowDuplicateProperties = false,
    };

    /// <summary>Distinguishes "too deep" from "malformed" by re-parsing with a larger, still bounded, depth.</summary>
    private static bool IsTooDeep(string json)
    {
        try
        {
            using var _ = JsonDocument.Parse(json, Options(DiagnosticDepth));
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static DslParseResult Fail(int? step, string? field, string code, string message) =>
        new(null, [new PlanError(step, field, code, message)]);
}
