using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using erp_inteligente.Core.Recipes;

namespace erp_inteligente.Services.Sqlite;

/// <summary>Bridges the Receitas screen to <see cref="RecipeStore"/>, always as the logged-in user and off the UI thread.</summary>
public sealed class SqliteRecipeService(RecipeStore store, ISessionService session) : IRecipeService
{
    public Task<IReadOnlyList<RecipeRecord>> ListAsync() => Task.Run(() => store.List(session.Current));

    public Task<RecipeInstance> BuildAsync(long id, JsonObject parameters) =>
        Task.Run(() => store.Instantiate(session.Current, id, parameters));

    public Task PromoteAsync(long id, string name, string description) =>
        Task.Run(() => store.Promote(session.Current, id, name, description));

    public Task SuspendAsync(long id) => Task.Run(() => store.Suspend(session.Current, id));

    public Task ReactivateAsync(long id) => Task.Run(() => store.Reactivate(session.Current, id));

    public Task DemoteAsync(long id) => Task.Run(() => store.Demote(session.Current, id));
}
