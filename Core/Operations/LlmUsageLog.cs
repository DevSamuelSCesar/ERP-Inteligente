using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using erp_inteligente.Core.Audit;
using erp_inteligente.Core.Data;
using erp_inteligente.Core.Llm;
using erp_inteligente.Core.Policy;
using erp_inteligente.Models;

namespace erp_inteligente.Core.Operations;

/// <summary>
/// What one request consumed in one role: tokens summed over its calls. Never the text. The cache counts are the part of
/// <see cref="PromptTokens"/> read from or written to the provider's prompt cache.
/// </summary>
public sealed record LlmUsageEntry(LlmUseRole Role, LlmChoice Choice, int Calls, int PromptTokens, int CompletionTokens, string Result)
{
    public int CacheReadTokens { get; init; }
    public int CacheWriteTokens { get; init; }
}

/// <summary>One line of the cost panel: a role, provider and model in the period.</summary>
public sealed record UsageLine(
    LlmUseRole Role, LlmProviderKind Provider, string Model, long Requests, long Calls, long PromptTokens, long CompletionTokens,
    decimal? Cost, string? Currency, long CacheReadTokens = 0);

/// <summary>
/// Token usage of a period (feature 9). <see cref="Requests"/> counts operator requests: those planned by the Planner
/// plus those solved by a recipe through the Matcher. Costs are estimates from <see cref="LlmPrices"/>, per currency.
/// </summary>
public sealed record UsageReport(
    DateTimeOffset Since, long Requests, long ByRecipe, double AverageTokensPerRequest, IReadOnlyList<UsageLine> Lines,
    IReadOnlyDictionary<string, decimal> CostByCurrency, long Unpriced)
{
    public double RecipeShare => Requests == 0 ? 0 : (double)ByRecipe / Requests;
}

/// <summary>
/// Accounting of LLM tokens (table <c>llm_usage</c>, append-only). The Planner and the Recipe Matcher record one row per
/// request; recording never interrupts a request. The report needs <c>audit.read</c>, like the audit panel.
/// </summary>
public sealed class LlmUsageLog(NexusDatabase database, TimeProvider time, PolicyEngine? policy = null)
{
    public static readonly string[] Results = ["answer", "proposals", "recipe", "no_match", "escalated", "failed"];

    /// <summary>Stores one row. Never throws: a failure is traced and the request goes on.</summary>
    public void Record(UserSession user, LlmUsageEntry entry)
    {
        try
        {
            if (!Results.Contains(entry.Result)) return;
            using var connection = database.OpenApplication();
            using var command = connection.CreateCommand();
            command.CommandText =
                "INSERT INTO llm_usage (occurred_at, user_id, warehouse_id, role, provider, model, calls, prompt_tokens, completion_tokens, result, " +
                "cache_read_tokens, cache_write_tokens) " +
                "VALUES ($at, $user, $warehouse, $role, $provider, $model, $calls, $prompt, $completion, $result, $cacheRead, $cacheWrite)";
            command.Parameters.AddWithValue("$at", AuditHasher.FormatTimestamp(time.GetUtcNow()));
            command.Parameters.AddWithValue("$user", user.UserId);
            command.Parameters.AddWithValue("$warehouse", user.WarehouseId);
            command.Parameters.AddWithValue("$role", entry.Role == LlmUseRole.Planner ? "planner" : "matcher");
            command.Parameters.AddWithValue("$provider", entry.Choice.Provider.ToString().ToLowerInvariant());
            command.Parameters.AddWithValue("$model", entry.Choice.Model.Length > 80 ? entry.Choice.Model[..80] : entry.Choice.Model);
            command.Parameters.AddWithValue("$calls", Math.Max(0, entry.Calls));
            command.Parameters.AddWithValue("$prompt", Math.Max(0, entry.PromptTokens));
            command.Parameters.AddWithValue("$completion", Math.Max(0, entry.CompletionTokens));
            command.Parameters.AddWithValue("$result", entry.Result);
            // The cached part can never exceed the prompt it belongs to.
            var cacheRead = Math.Clamp(entry.CacheReadTokens, 0, Math.Max(0, entry.PromptTokens));
            command.Parameters.AddWithValue("$cacheRead", cacheRead);
            command.Parameters.AddWithValue("$cacheWrite", Math.Clamp(entry.CacheWriteTokens, 0, Math.Max(0, entry.PromptTokens) - cacheRead));
            command.ExecuteNonQuery();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            System.Diagnostics.Trace.TraceWarning("Recording LLM usage failed: " + ex.GetType().Name);
        }
    }

    /// <summary>Usage since <paramref name="since"/>, all warehouses.</summary>
    /// <exception cref="PolicyDeniedException">The user lacks <c>audit.read</c>.</exception>
    public UsageReport Report(UserSession user, DateTimeOffset since)
    {
        ArgumentNullException.ThrowIfNull(user);
        policy?.Require(user, Permissions.AuditRead);

        using var connection = database.OpenApplication();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT role, provider, model, SUM(CASE WHEN role = 'planner' OR result = 'recipe' THEN 1 ELSE 0 END), COUNT(*), " +
            "SUM(calls), SUM(prompt_tokens), SUM(completion_tokens), SUM(CASE WHEN result = 'recipe' THEN 1 ELSE 0 END), " +
            "SUM(cache_read_tokens), SUM(cache_write_tokens) " +
            "FROM llm_usage WHERE occurred_at >= $since GROUP BY role, provider, model ORDER BY role DESC, SUM(prompt_tokens) DESC";
        command.Parameters.AddWithValue("$since", AuditHasher.FormatTimestamp(since));

        var lines = new List<UsageLine>();
        long requests = 0, byRecipe = 0, tokens = 0, unpriced = 0;
        var costs = new Dictionary<string, decimal>(StringComparer.Ordinal);
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var role = reader.GetString(0) == "planner" ? LlmUseRole.Planner : LlmUseRole.Matcher;
                var provider = Enum.Parse<LlmProviderKind>(reader.GetString(1), ignoreCase: true);
                var model = reader.GetString(2);
                var prompt = reader.GetInt64(6);
                var completion = reader.GetInt64(7);
                var cacheRead = reader.GetInt64(9);
                var cacheWrite = reader.GetInt64(10);
                var price = LlmPrices.For(provider, model);
                decimal? cost = price is null ? null : LlmPrices.Cost(price, prompt, completion, cacheRead, cacheWrite);
                if (cost is { } value)
                    costs[price!.Currency] = costs.GetValueOrDefault(price.Currency) + value;
                else
                    unpriced += prompt + completion;
                // Requests are counted where they end: a planner row, or a matcher row that solved the request.
                requests += reader.GetInt64(3);
                byRecipe += reader.GetInt64(8);
                tokens += prompt + completion;
                // Requests of a line: planner requests, or every request the Matcher looked at (matched or not).
                lines.Add(new UsageLine(role, provider, model, reader.GetInt64(4), reader.GetInt64(5), prompt, completion, cost, price?.Currency, cacheRead));
            }
        }
        var average = requests == 0 ? 0 : (double)tokens / requests;
        return new UsageReport(since, requests, byRecipe, average, lines, costs, unpriced);
    }
}

/// <summary>A price per million tokens.</summary>
public sealed record LlmPrice(string Currency, decimal Input, decimal Output);

/// <summary>
/// Prices per million tokens used for the estimates (references/IA.md, section 2, consulted on 2026-10-27: Anthropic in
/// US$, DeepSeek in ¥ at peak hours). Models without a published price there have none here: the panel shows their
/// tokens without a cost instead of guessing. The local model costs nothing per token.
/// </summary>
public static class LlmPrices
{
    private static readonly Dictionary<(LlmProviderKind, string), LlmPrice> Table = new()
    {
        [(LlmProviderKind.Claude, LlmSettings.ClaudeTopModel)] = new("USD", 4.00m, 20.00m),
        [(LlmProviderKind.Claude, LlmSettings.ClaudeStrongModel)] = new("USD", 2.00m, 10.00m),
        [(LlmProviderKind.Claude, LlmSettings.ClaudeFastModel)] = new("USD", 0.10m, 0.50m),
        [(LlmProviderKind.DeepSeek, LlmSettings.StrongModel)] = new("CNY", 9.00m, 27.00m),
        [(LlmProviderKind.DeepSeek, LlmSettings.FastModel)] = new("CNY", 2.00m, 8.00m),
        [(LlmProviderKind.Local, LlmSettings.LocalModel)] = new("USD", 0m, 0m),
    };

    public static LlmPrice? For(LlmProviderKind provider, string model) => Table.GetValueOrDefault((provider, model));

    /// <summary>Price of a cache read and of a cache write (5-minute cache), as a share of the input price (Anthropic).</summary>
    public const decimal CacheReadShare = 0.10m;
    public const decimal CacheWriteShare = 1.25m;

    /// <summary>
    /// Cost of <paramref name="prompt"/> input tokens (of which <paramref name="cacheRead"/> were read from and
    /// <paramref name="cacheWrite"/> written to the prompt cache) and <paramref name="completion"/> output tokens.
    /// </summary>
    public static decimal Cost(LlmPrice price, long prompt, long completion, long cacheRead = 0, long cacheWrite = 0)
    {
        var uncached = Math.Max(0, prompt - cacheRead - cacheWrite);
        return (uncached * price.Input + cacheRead * price.Input * CacheReadShare + cacheWrite * price.Input * CacheWriteShare
                + completion * price.Output) / 1_000_000m;
    }

    public static string Symbol(string currency) => currency switch
    {
        "USD" => "US$",
        "CNY" => "¥",
        _ => currency,
    };

    public static string Format(decimal value, string currency) =>
        Symbol(currency) + " " + value.ToString(value < 1 ? "0.0000" : "N2", CultureInfo.GetCultureInfo("pt-BR"));
}
