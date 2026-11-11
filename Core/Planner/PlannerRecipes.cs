using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using erp_inteligente.Core.Engine;
using erp_inteligente.Core.Policy;
using erp_inteligente.Models;

namespace erp_inteligente.Core.Planner;

/// <summary>
/// Feature 7 in the Planner: a request recognised by the <see cref="Recipes.RecipeMatcher"/> becomes one proposal built
/// from the recipe, without the planning loop. The proposal is simulated like any other and waits for the requester's
/// Confirm; a recipe whose plan does not pass the simulation (the stock changed, a value is wrong) falls back to planning.
/// </summary>
public sealed partial class Planner
{
    private async Task<(PlannerResult Result, PlannerTurn? Turn)?> TryRecipeAsync(
        UserSession user, long? aiUserId, IReadOnlySet<string> rights, string request, CancellationToken ct)
    {
        var match = await recipeMatcher!.MatchAsync(user, aiUserId, rights, request, ct);
        if (match is null) return null;

        EnginePreview preview;
        try
        {
            preview = await Task.Run(() => engine.Preview(user, match.PlanJson, "recipe"), ct);
        }
        catch (PolicyDeniedException)
        {
            recipeMatcher.RecordUsage(user, match.Choice, match.Usage, "no_match");
            return null;
        }
        // Counted as solved by a recipe only when its plan passed the simulation; otherwise the Planner takes over.
        var solved = preview.IsValid && preview.Document is not null;
        recipeMatcher.RecordUsage(user, match.Choice, match.Usage, solved ? "recipe" : "no_match");
        if (!solved) return null;

        var recipe = match.Recipe;
        var title = Shorten($"Receita {recipe.Name}", MaxTitleLength);
        var rationale = Shorten(CleanInline(recipe.Description), MaxRationaleLength);
        var assumptions = recipe.Parameters
            .Where(p => match.Parameters.ContainsKey(p.Name))
            .Select(p => Shorten($"{p.Label}: {CleanInline(match.Parameters[p.Name]?.ToJsonString().Trim('"'))}", MaxAssumptionLength))
            .Take(MaxAssumptions)
            .ToList();
        var item = new ProposalItem(title, rationale, assumptions, PlanDraft.FromJson(match.PlanJson, PlanOrigin.Recipe, recipe.Id), preview);
        var text = $"Usei a receita {recipe.Name}, que já funcionou {recipe.SuccessCount} vezes, sem planejar do zero. Confira os valores e confirme.";
        return (new PlannerProposals([item], text, []), Remember(request, text + " Plano: " + title + ".", null));
    }

    private static string Shorten(string text, int max) => text.Length <= max ? text : text[..(max - 1)].TrimEnd() + "…";
}
