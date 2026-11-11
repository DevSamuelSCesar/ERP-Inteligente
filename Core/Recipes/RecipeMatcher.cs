using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using erp_inteligente.Core.Audit;
using erp_inteligente.Core.Llm;
using erp_inteligente.Models;

namespace erp_inteligente.Core.Recipes;

/// <summary>A request recognised as a use of an active recipe: the recipe, the values extracted and the plan built.</summary>
/// <param name="Choice">Provider and model that answered; <paramref name="Usage"/> its tokens (recorded by the caller, feature 9).</param>
public sealed record RecipeMatch(RecipeRecord Recipe, JsonObject Parameters, string PlanJson, LlmChoice Choice, LlmUsage Usage);

/// <summary>
/// Recipe Matcher (references/ARQUITETURA.md, sections 4 and 7): before planning, one short call to the fast model only
/// says which active recipe the request is and extracts its parameters. It has no tools and writes nothing; whatever it
/// returns is checked here (the recipe must be one of those offered, the values must pass the recipe's schema) and the
/// plan still goes through the engine. Any doubt returns null and the normal Planner runs.
/// </summary>
/// <param name="settings">The model of the Matcher role (feature 8); without it, DeepSeek's fast model.</param>
/// <param name="usage">Records the tokens of a call that found no usable recipe (feature 9); a match is recorded by the caller,
/// which knows whether the recipe's plan passed the simulation.</param>
public sealed class RecipeMatcher(ILlmProvider llm, RecipeStore recipes, IAuditLog audit, LlmSettings? settings = null,
    Operations.LlmUsageLog? usage = null)
{
    public const int MaxTokens = 600;
    public const int MaxCandidates = 20;
    private const int MaxResponseChars = 4000;

    /// <summary>
    /// The instructions (<c>Core/Recipes/Prompts/matcher.md</c>, embedded). The fine-tuning dataset generator
    /// (<c>tools/finetune</c>) reads the same file, so a fine-tuned model is trained on exactly what it receives here.
    /// </summary>
    internal static string SystemPrompt { get; } = LoadPrompt();

    private static string LoadPrompt()
    {
        using var stream = typeof(RecipeMatcher).Assembly.GetManifestResourceStream("Nexus.Recipes.matcher.md")
            ?? throw new InvalidOperationException("Embedded resource not found: Nexus.Recipes.matcher.md");
        using var reader = new System.IO.StreamReader(stream, System.Text.Encoding.UTF8);
        return reader.ReadToEnd().Replace("\r\n", "\n").TrimEnd();
    }

    public async Task<RecipeMatch?> MatchAsync(UserSession user, long? aiUserId, IReadOnlySet<string> rights, string request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(rights);
        ArgumentNullException.ThrowIfNull(request);

        var candidates = (await Task.Run(() => recipes.Active(rights), ct))
            .Where(r => MightMatch(request, r)).Take(MaxCandidates).ToList();
        if (candidates.Count == 0) return null; // no LLM call for "oi" or a request unrelated to any recipe

        LlmResponse response;
        var choice = settings?.ChoiceFor(LlmUseRole.Matcher) ?? new LlmChoice(LlmProviderKind.DeepSeek, LlmSettings.FastModel);
        try
        {
            response = await llm.CompleteAsync(new LlmRequest(choice.Model,
                [LlmMessage.System(SystemPrompt + "\n\n" + Catalog(candidates)), LlmMessage.User(request)], [], MaxTokens,
                Provider: choice.Provider), ct);
        }
        catch (LlmException)
        {
            RecordUsage(user, choice, new LlmUsage(0, 0), "failed");
            return null; // the Planner reports the provider problem itself
        }

        var (recipe, parameters) = Parse(response.Content, candidates);
        if (recipe is null || parameters is null)
        {
            Audit(aiUserId, user, "none", AuditOutcome.Allowed, "no_match");
            RecordUsage(user, choice, response.Usage, "no_match");
            return null;
        }
        var instance = RecipeTemplate.Instantiate(recipe, parameters);
        if (!instance.IsValid)
        {
            Audit(aiUserId, user, Target(recipe), AuditOutcome.Denied, "invalid_parameters");
            RecordUsage(user, choice, response.Usage, "no_match");
            return null;
        }
        Audit(aiUserId, user, Target(recipe), AuditOutcome.Allowed, "matched");
        return new RecipeMatch(recipe, parameters, instance.PlanJson!, choice, response.Usage);
    }

    /// <summary>Records one Matcher call: <c>recipe</c> when it solved the request, otherwise <c>no_match</c> or <c>failed</c>.</summary>
    public void RecordUsage(UserSession user, LlmChoice choice, LlmUsage tokens, string result) =>
        usage?.Record(user, new Operations.LlmUsageEntry(LlmUseRole.Matcher, choice, 1, tokens.PromptTokens, tokens.CompletionTokens, result));


    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "para", "como", "mais", "quero", "favor", "fazer", "faca", "todos", "todas", "esse", "essa", "este", "esta", "isso",
        "onde", "qual", "quais", "sobre", "depois", "antes", "agora", "aqui", "pelo", "pela", "cada", "muito", "sempre",
        "estao", "entre", "ainda", "voce", "preciso", "pode", "poderia", "deve",
    };

    /// <summary>
    /// Cheap gate before the LLM: the request must carry a value (a digit: SKU, quantity, address) and share a word with the
    /// recipe's name or description (first 5 letters, accents and case ignored, so "armazene" meets "armazenar").
    /// </summary>
    internal static bool MightMatch(string request, RecipeRecord recipe)
    {
        if (!request.Any(char.IsAsciiDigit)) return false;
        var wanted = Words(request);
        if (wanted.Count == 0) return false;
        var offered = Words(recipe.Name.Replace('_', ' ') + " " + recipe.Description);
        return wanted.Overlaps(offered);
    }

    private static HashSet<string> Words(string text) =>
        System.Text.RegularExpressions.Regex.Split(erp_inteligente.Core.Memory.TextFold.Fold(text), @"[^a-z]+")
            .Where(w => w.Length >= 4 && !StopWords.Contains(w))
            .Select(w => w.Length > 5 ? w[..5] : w)
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>The offered recipes as reference data: name, description and the parameters with their types.</summary>
    internal static string Catalog(IReadOnlyList<RecipeRecord> candidates)
    {
        var list = new JsonArray(candidates.Select(r => (JsonNode?)new JsonObject
        {
            ["nome"] = r.Name,
            ["descricao"] = r.Description,
            ["parametros"] = new JsonObject(r.Parameters.Select(p =>
                new KeyValuePair<string, JsonNode?>(p.Name, $"{p.Type}: {p.Label}" + (p.Example is { Length: <= 40 } e ? $" (ex.: {e})" : "")))),
        }).ToArray());
        var json = list.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping })
            .Replace("<", "\\u003c", StringComparison.Ordinal).Replace(">", "\\u003e", StringComparison.Ordinal);
        return $"<dados fonte=\"receitas\">{json}</dados>";
    }

    /// <summary>
    /// Reads <c>{"receita": name, "parametros": {...}}</c> from the model's text (a code fence or text around it is
    /// tolerated). Only a recipe from <paramref name="candidates"/> is accepted.
    /// </summary>
    internal static (RecipeRecord? Recipe, JsonObject? Parameters) Parse(string? content, IReadOnlyList<RecipeRecord> candidates)
    {
        if (string.IsNullOrWhiteSpace(content) || content.Length > MaxResponseChars) return (null, null);
        var start = content.IndexOf('{');
        var end = content.LastIndexOf('}');
        if (start < 0 || end <= start) return (null, null);
        try
        {
            if (JsonNode.Parse(content[start..(end + 1)], null, new JsonDocumentOptions { MaxDepth = 8 }) is not JsonObject root)
                return (null, null);
            // Two shapes: the prompt's {"receita","parametros"} and the fine-tuning dataset's {"intent","recipe","params"}
            // (references/ARQUITETURA.md, section 10.1), where {"intent":"none"} means no recipe.
            var nameNode = root["receita"] ?? root["recipe"];
            if (nameNode is not JsonValue nameValue || !nameValue.TryGetValue<string>(out var name)) return (null, null);
            var recipe = candidates.FirstOrDefault(r => r.Name == name);
            if (recipe is null || (root["parametros"] ?? root["params"]) is not JsonObject parameters) return (null, null);
            return (recipe, (JsonObject)parameters.DeepClone());
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    private static string Target(RecipeRecord recipe) => "recipe:" + recipe.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private void Audit(long? aiUserId, UserSession user, string target, AuditOutcome outcome, string code) =>
        audit.Append(new AuditEntry(AuditActorType.Ai, aiUserId, user.UserId, "ai.recipe_match", target, outcome,
            new JsonObject { ["code"] = code }.ToJsonString()));
}
