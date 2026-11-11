using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using erp_inteligente.Core.Recipes;

namespace erp_inteligente.Services;

/// <summary>
/// Recipes for the Receitas screen (references/ARQUITETURA.md, section 7): everybody with <c>stock.read</c> lists them,
/// <c>plan.submit</c> builds a plan from an active one (no LLM), and only <c>recipe.manage</c> (the supervisor) promotes,
/// suspends, reactivates and demotes. The store checks the permission again and audits every change.
/// </summary>
public interface IRecipeService
{
    Task<IReadOnlyList<RecipeRecord>> ListAsync();

    /// <summary>The plan of an active recipe filled with <paramref name="parameters"/>; nothing is written.</summary>
    Task<RecipeInstance> BuildAsync(long id, JsonObject parameters);

    /// <exception cref="RecipeStoreException">Invalid name or description, or the recipe is not a candidate.</exception>
    Task PromoteAsync(long id, string name, string description);

    Task SuspendAsync(long id);

    /// <exception cref="RecipeStoreException">The recipe did not pass the revalidation.</exception>
    Task ReactivateAsync(long id);

    Task DemoteAsync(long id);
}
