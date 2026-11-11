using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using erp_inteligente.Core.Audit;
using erp_inteligente.Core.Data;
using erp_inteligente.Core.Engine;
using erp_inteligente.Core.Policy;
using erp_inteligente.Core.Safety;
using erp_inteligente.Core.Skills;
using erp_inteligente.Models;
using Microsoft.Data.Sqlite;

namespace erp_inteligente.Core.Recipes;

/// <summary>
/// Recipes (references/ARQUITETURA.md, section 7; ROADMAP.md, feature 7).
/// <list type="bullet">
/// <item><b>Learning:</b> when the same plan structure has been executed <see cref="PromotionThreshold"/> times (never by a
/// recipe), it becomes a <c>candidate</c>. Detection is automatic; promotion is human.</item>
/// <item><b>Promotion and management</b> need <c>recipe.manage</c> (the supervisor; the AI role is denied by trigger).</item>
/// <item><b>Use</b> needs <c>plan.submit</c>: the plan built from a recipe goes through the engine like any other.</item>
/// <item><b>Invalidation:</b> an active recipe whose DSL or skill version changed is suspended until revalidated;
/// <see cref="DemotionStreak"/> failures in a row demote it.</item>
/// </list>
/// Every change is audited (<c>recipe.*</c>), without parameter values.
/// </summary>
public sealed partial class RecipeStore(NexusDatabase database, PolicyEngine policy, AuditLog audit, TimeProvider time, SkillCatalog? skills = null)
{
    public const int PromotionThreshold = 3;
    public const int DemotionStreak = 3;
    public const int LearningWindow = 200;
    public const int MinDescriptionLength = 10;
    public const int MaxDescriptionLength = 300;

    [GeneratedRegex(@"^[a-z][a-z0-9_]{2,39}\z")]
    private static partial Regex NamePattern();

    private const string SelectSql = """
        SELECT r.id, r.name, r.description, r.intent, r.skill, r.skill_version, r.dsl_version, r.status, r.success_count,
               r.failure_count, r.failure_streak, r.plan_template_json, r.params_schema_json, r.example_params_json,
               r.signature, r.source_plan_id, u.display_name, r.created_at, r.promoted_at, r.last_used_at, r.status_reason
        FROM recipes r LEFT JOIN users u ON u.id = r.promoted_by
        """;

    public static bool IsValidName(string? name) => name is not null && NamePattern().IsMatch(name);

    // ---- reads ---------------------------------------------------------------------------------------------

    /// <summary>All recipes (every status) for the Receitas screen. Revalidates versions first (see <see cref="RefreshValidity"/>).</summary>
    public IReadOnlyList<RecipeRecord> List(UserSession user)
    {
        ArgumentNullException.ThrowIfNull(user);
        policy.Require(user, Permissions.StockRead);
        RefreshValidity();
        return Query(SelectSql + " ORDER BY r.status = 'active' DESC, r.success_count DESC, r.id");
    }

    /// <summary>Active recipes whose every operation is within <paramref name="rights"/> (for the Recipe Matcher).</summary>
    public IReadOnlyList<RecipeRecord> Active(IReadOnlySet<string> rights)
    {
        ArgumentNullException.ThrowIfNull(rights);
        RefreshValidity();
        return Query(SelectSql + " WHERE r.status = 'active' ORDER BY r.success_count DESC, r.id")
            .Where(r => r.Operations.Count > 0 && r.Operations.All(op => OperationCatalog.TryGet(op, out var d) && rights.Contains(d.Permission)))
            .ToList();
    }

    public RecipeRecord? Find(long id) => Query(SelectSql + " WHERE r.id = $id", ("$id", id)).FirstOrDefault();

    /// <summary>
    /// Builds the plan of an active recipe for <paramref name="user"/>. Nothing is written: the caller previews or
    /// submits it through the engine with origin <c>recipe</c>.
    /// </summary>
    public RecipeInstance Instantiate(UserSession user, long id, JsonObject parameters)
    {
        ArgumentNullException.ThrowIfNull(user);
        policy.Require(user, Permissions.PlanSubmit);
        var recipe = Find(id) ?? throw new RecipeStoreException("Receita não encontrada.");
        if (recipe.Status != RecipeStatus.Active)
            return new RecipeInstance(null, [$"A receita {recipe.Name} não está ativa ({StatusText(recipe.Status)})."]);
        return RecipeTemplate.Instantiate(recipe, parameters);
    }

    // ---- supervisor actions --------------------------------------------------------------------------------

    /// <summary>Turns a candidate into an active recipe with a name and a description the supervisor wrote.</summary>
    public void Promote(UserSession user, long id, string name, string description)
    {
        Manage(user);
        name = (name ?? string.Empty).Trim();
        description = Planner.Planner.CleanInline(description);
        if (!IsValidName(name))
            throw new RecipeStoreException("O nome deve ter de 3 a 40 caracteres: letras minúsculas, dígitos e _, começando por letra.");
        if (description.Length is < MinDescriptionLength or > MaxDescriptionLength)
            throw new RecipeStoreException($"A descrição deve ter de {MinDescriptionLength} a {MaxDescriptionLength} caracteres.");
        // The description goes to the Recipe Matcher's prompt: it must read as a description, never as an order.
        if (InjectionDetector.Scan(description) is not null || SensitiveDataRedactor.Contains(description))
            throw new RecipeStoreException("A descrição parece uma instrução ou tem dado pessoal. Descreva só o que a receita faz.");

        var recipe = Require(id);
        if (recipe.Status != RecipeStatus.Candidate)
            throw new RecipeStoreException("Só uma receita candidata pode ser promovida.");
        if (Query(SelectSql + " WHERE r.name = $name AND r.id <> $id", ("$name", name), ("$id", id)).Count > 0)
            throw new RecipeStoreException("Já existe uma receita com esse nome.");

        Update(user, recipe, "recipe.promote",
            "UPDATE recipes SET status = 'active', name = $name, description = $description, promoted_by = $by, promoted_at = $at, " +
            "failure_streak = 0, status_reason = NULL WHERE id = $id AND status = 'candidate'",
            ("$name", name), ("$description", description), ("$by", user.UserId), ("$at", Now()));
    }

    /// <summary>Pauses an active recipe; it stays visible and can be reactivated.</summary>
    public void Suspend(UserSession user, long id)
    {
        Manage(user);
        var recipe = Require(id);
        if (recipe.Status != RecipeStatus.Active)
            throw new RecipeStoreException("Só uma receita ativa pode ser suspensa.");
        Update(user, recipe, "recipe.suspend",
            "UPDATE recipes SET status = 'suspended', status_reason = $reason WHERE id = $id AND status = 'active'",
            ("$reason", "Suspensa pelo supervisor."));
    }

    /// <summary>
    /// Revalidates a suspended or demoted recipe with its example values (DSL structure and argument schemas, not the
    /// current stock) against the current DSL and skill versions, and makes it active again.
    /// </summary>
    public void Reactivate(UserSession user, long id)
    {
        Manage(user);
        var recipe = Require(id);
        if (recipe.Status is not (RecipeStatus.Suspended or RecipeStatus.Demoted))
            throw new RecipeStoreException("Só uma receita suspensa ou despromovida pode ser reativada.");
        if (recipe.DslVersion != DslParser.SupportedVersion)
            throw new RecipeStoreException($"A receita foi feita para a DSL {recipe.DslVersion}; esta versão do Nexus usa a {DslParser.SupportedVersion}. Ela precisa ser refeita.");

        var problems = Revalidate(recipe);
        if (problems.Count > 0)
            throw new RecipeStoreException("A receita não passou na revalidação: " + string.Join(" ", problems.Take(3)));

        Update(user, recipe, "recipe.reactivate",
            "UPDATE recipes SET status = 'active', skill_version = $version, failure_streak = 0, status_reason = NULL " +
            "WHERE id = $id AND status IN ('suspended', 'demoted')",
            ("$version", CurrentSkillVersion(recipe.Skill) ?? recipe.SkillVersion));
    }

    /// <summary>Retires a recipe for good (it is kept for history and can only come back through <see cref="Reactivate"/>).</summary>
    public void Demote(UserSession user, long id)
    {
        Manage(user);
        var recipe = Require(id);
        if (recipe.Status == RecipeStatus.Demoted)
            throw new RecipeStoreException("A receita já está despromovida.");
        Update(user, recipe, "recipe.demote",
            "UPDATE recipes SET status = 'demoted', status_reason = $reason WHERE id = $id AND status <> 'demoted'",
            ("$reason", "Despromovida pelo supervisor."));
    }

    // ---- learning and outcomes (system, called by the engine) ------------------------------------------------

    /// <summary>
    /// A plan built from recipe <paramref name="recipeId"/> ended. Success resets the failure streak; a failure (including a
    /// rejection) counts, and <see cref="DemotionStreak"/> in a row demote an active recipe.
    /// </summary>
    public void RecordOutcome(long recipeId, long planId, bool succeeded)
    {
        using var connection = database.OpenApplication();
        using var transaction = connection.BeginTransaction(deferred: false);
        using (var update = Command(connection, transaction, succeeded
            ? "UPDATE recipes SET success_count = success_count + 1, failure_streak = 0, last_used_at = $at WHERE id = $id"
            : "UPDATE recipes SET failure_count = failure_count + 1, failure_streak = failure_streak + 1, last_used_at = $at WHERE id = $id"))
        {
            update.Parameters.AddWithValue("$at", Now());
            update.Parameters.AddWithValue("$id", recipeId);
            if (update.ExecuteNonQuery() != 1) return;
        }
        if (!succeeded)
        {
            using var demote = Command(connection, transaction,
                "UPDATE recipes SET status = 'demoted', status_reason = $reason WHERE id = $id AND status = 'active' AND failure_streak >= $limit");
            demote.Parameters.AddWithValue("$reason", $"Despromovida após {DemotionStreak} falhas seguidas.");
            demote.Parameters.AddWithValue("$id", recipeId);
            demote.Parameters.AddWithValue("$limit", DemotionStreak);
            if (demote.ExecuteNonQuery() == 1)
                audit.AppendWithin(connection, transaction, new AuditEntry(AuditActorType.System, null, null, "recipe.demote",
                    Target(recipeId), AuditOutcome.Allowed, new JsonObject { ["reason"] = "failure_streak", ["plan"] = planId }.ToJsonString()));
        }
        transaction.Commit();
    }

    /// <summary>
    /// An executed plan (not from a recipe): when <see cref="PromotionThreshold"/> executed plans in the last
    /// <see cref="LearningWindow"/> share its structure and no recipe has it yet, a candidate is created from it.
    /// Returns the new candidate's id, or null.
    /// </summary>
    public long? LearnFrom(long planId)
    {
        string? planJson = null;
        using (var connection = database.OpenApplication())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT plan_json, dsl_version FROM plans WHERE id = $id AND status = 'executed' AND planned_by <> 'recipe'";
            command.Parameters.AddWithValue("$id", planId);
            using var reader = command.ExecuteReader();
            if (reader.Read()) planJson = reader.GetString(0);
        }
        if (planJson is null || DslParser.Parse(planJson) is not { Document: { } document }) return null;

        var signature = RecipeTemplate.Signature(document);
        if (Query(SelectSql + " WHERE r.signature = $signature", ("$signature", signature)).Count > 0) return null;

        var same = 0;
        using (var connection = database.OpenApplication())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT plan_json FROM plans WHERE status = 'executed' AND planned_by <> 'recipe' ORDER BY id DESC LIMIT $window";
            command.Parameters.AddWithValue("$window", LearningWindow);
            using var reader = command.ExecuteReader();
            while (reader.Read())
                if (DslParser.Parse(reader.GetString(0)) is { Document: { } other } && RecipeTemplate.Signature(other) == signature)
                    same++;
        }
        if (same < PromotionThreshold) return null;

        ExtractedTemplate extracted;
        try
        {
            extracted = RecipeTemplate.Extract(document);
        }
        catch (RecipeStoreException)
        {
            return null; // too many variable values: not worth a recipe
        }
        var skill = document.Skill ?? SkillMapping.ForOperation(document.Steps[0].Op);
        var name = UniqueName(document.Intent);
        var description = $"Candidata detectada em {same} planos executados iguais: " +
                          string.Join(", depois ", document.Steps.Select(s => OperationCatalog.TryGet(s.Op, out var d) ? d.Label.ToLowerInvariant() : s.Op).Distinct()) + ".";
        if (description.Length > MaxDescriptionLength) description = description[..(MaxDescriptionLength - 1)] + "…";

        using var write = database.OpenApplication();
        using var transaction = write.BeginTransaction(deferred: false);
        long id;
        using (var insert = Command(write, transaction,
            "INSERT INTO recipes (name, description, intent, plan_template_json, params_schema_json, dsl_version, skill_version, status, " +
            "skill, signature, example_params_json, source_plan_id, created_at) VALUES ($name, $description, $intent, $template, $params, " +
            "$dsl, $skill_version, 'candidate', $skill, $signature, $example, $plan, $at) " +
            "ON CONFLICT DO NOTHING RETURNING id"))
        {
            insert.Parameters.AddWithValue("$name", name);
            insert.Parameters.AddWithValue("$description", description);
            insert.Parameters.AddWithValue("$intent", document.Intent);
            insert.Parameters.AddWithValue("$template", extracted.TemplateJson);
            insert.Parameters.AddWithValue("$params", extracted.ParamsSchemaJson);
            insert.Parameters.AddWithValue("$dsl", document.DslVersion);
            insert.Parameters.AddWithValue("$skill_version", CurrentSkillVersion(skill) ?? skill + "@1");
            insert.Parameters.AddWithValue("$skill", skill);
            insert.Parameters.AddWithValue("$signature", signature);
            insert.Parameters.AddWithValue("$example", extracted.ExampleParams.ToJsonString());
            insert.Parameters.AddWithValue("$plan", planId);
            insert.Parameters.AddWithValue("$at", Now());
            if (insert.ExecuteScalar() is not long newId) return null; // a concurrent learner won
            id = newId;
        }
        audit.AppendWithin(write, transaction, new AuditEntry(AuditActorType.System, null, null, "recipe.candidate", Target(id),
            AuditOutcome.Allowed, new JsonObject { ["plans"] = same, ["source_plan"] = planId }.ToJsonString()));
        transaction.Commit();
        return id;
    }

    /// <summary>
    /// Suspends every active recipe whose DSL version is not the one this build runs, or whose skill has a newer version
    /// than the one it was made with. Audited as a system action with the reason.
    /// </summary>
    public void RefreshValidity()
    {
        var stale = new List<(long Id, string Reason, string Code)>();
        foreach (var recipe in Query(SelectSql + " WHERE r.status = 'active'"))
        {
            if (recipe.DslVersion != DslParser.SupportedVersion)
                stale.Add((recipe.Id, $"A versão da DSL mudou ({recipe.DslVersion} → {DslParser.SupportedVersion}). Revalide a receita.", "dsl_version"));
            else if (CurrentSkillVersion(recipe.Skill) is { } current && VersionNumber(current) != VersionNumber(recipe.SkillVersion))
                stale.Add((recipe.Id, $"A skill {recipe.Skill} mudou para a versão {VersionNumber(current)}. Revalide a receita.", "skill_version"));
        }
        if (stale.Count == 0) return;

        using var connection = database.OpenApplication();
        using var transaction = connection.BeginTransaction(deferred: false);
        foreach (var (id, reason, code) in stale)
        {
            using var update = Command(connection, transaction,
                "UPDATE recipes SET status = 'suspended', status_reason = $reason WHERE id = $id AND status = 'active'");
            update.Parameters.AddWithValue("$reason", reason);
            update.Parameters.AddWithValue("$id", id);
            if (update.ExecuteNonQuery() == 1)
                audit.AppendWithin(connection, transaction, new AuditEntry(AuditActorType.System, null, null, "recipe.suspend",
                    Target(id), AuditOutcome.Allowed, new JsonObject { ["reason"] = code }.ToJsonString()));
        }
        transaction.Commit();
    }

    // ---- helpers -------------------------------------------------------------------------------------------

    /// <summary>The example values against the DSL parser and the argument schemas (structure only, not the stock).</summary>
    private static List<string> Revalidate(RecipeRecord recipe)
    {
        var example = recipe.ExampleParamsJson is null ? null : JsonNode.Parse(recipe.ExampleParamsJson) as JsonObject;
        if (example is null) return ["A receita não tem valores de exemplo para revalidar."];
        var instance = RecipeTemplate.Instantiate(recipe with { DslVersion = DslParser.SupportedVersion }, example);
        if (!instance.IsValid) return instance.Errors.ToList();
        var parsed = DslParser.Parse(instance.PlanJson!);
        if (parsed.Document is null) return parsed.Errors.Select(e => e.Message).ToList();
        return SchemaValidator.Validate(parsed.Document).Select(e => e.Message).ToList();
    }

    private string? CurrentSkillVersion(string? skill)
    {
        if (skills is null || !SkillDocument.IsValidName(skill)) return null;
        return skills.Load(skill!) is { } content ? $"{skill}@{content.Version}" : null;
    }

    /// <summary>"armazenagem@1.0" and "armazenagem@1" are the same version 1.</summary>
    private static int VersionNumber(string skillVersion)
    {
        var at = skillVersion.LastIndexOf('@');
        var text = at < 0 ? skillVersion : skillVersion[(at + 1)..];
        var dot = text.IndexOf('.');
        if (dot >= 0) text = text[..dot];
        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : -1;
    }

    private string UniqueName(string intent)
    {
        var stem = new string((intent ?? "receita").ToLowerInvariant().Where(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_').ToArray());
        if (stem.Length == 0 || !char.IsAsciiLetterLower(stem[0])) stem = "receita_" + stem;
        if (stem.Length > 32) stem = stem[..32];
        var taken = Query(SelectSql).Select(r => r.Name).ToHashSet(StringComparer.Ordinal);
        for (var n = 1; ; n++)
        {
            var name = $"{stem}_{n}";
            if (!taken.Contains(name)) return name;
        }
    }

    private void Manage(UserSession user)
    {
        ArgumentNullException.ThrowIfNull(user);
        policy.Require(user, Permissions.RecipeManage);
    }

    private RecipeRecord Require(long id) => Find(id) ?? throw new RecipeStoreException("Receita não encontrada.");

    private void Update(UserSession user, RecipeRecord recipe, string action, string sql, params (string Name, object Value)[] parameters)
    {
        using var connection = database.OpenApplication();
        using var transaction = connection.BeginTransaction(deferred: false);
        using (var update = Command(connection, transaction, sql))
        {
            update.Parameters.AddWithValue("$id", recipe.Id);
            foreach (var (name, value) in parameters) update.Parameters.AddWithValue(name, value);
            if (update.ExecuteNonQuery() != 1)
                throw new RecipeStoreException("A receita foi alterada por outra pessoa. Recarregue.");
        }
        audit.AppendWithin(connection, transaction, new AuditEntry(AuditActorType.User, user.UserId, null, action, Target(recipe.Id),
            AuditOutcome.Allowed, new JsonObject { ["from"] = recipe.Status.ToDbValue() }.ToJsonString()));
        transaction.Commit();
    }

    private List<RecipeRecord> Query(string sql, params (string Name, object Value)[] parameters)
    {
        using var connection = database.OpenApplication();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        using var reader = command.ExecuteReader();
        var rows = new List<RecipeRecord>();
        while (reader.Read())
        {
            string? Text(int i) => reader.IsDBNull(i) ? null : reader.GetString(i);
            var record = new RecipeRecord(
                reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), Text(4), reader.GetString(5),
                reader.GetString(6), RecipeStatuses.FromDbValue(reader.GetString(7)), reader.GetInt64(8), reader.GetInt64(9),
                reader.GetInt64(10), reader.GetString(11), reader.GetString(12), Text(13), Text(14),
                reader.IsDBNull(15) ? null : reader.GetInt64(15), Text(16), Text(17), Text(18), Text(19), Text(20));
            rows.Add(record with
            {
                Parameters = RecipeTemplate.Parameters(record.ParamsSchemaJson, record.ExampleParamsJson),
                Operations = RecipeTemplate.Operations(record.TemplateJson),
            });
        }
        return rows;
    }

    public static string StatusText(RecipeStatus status) => status switch
    {
        RecipeStatus.Candidate => "candidata",
        RecipeStatus.Active => "ativa",
        RecipeStatus.Suspended => "suspensa",
        _ => "despromovida",
    };

    private string Now() => AuditHasher.FormatTimestamp(time.GetUtcNow());

    private static string Target(long id) => "recipe:" + id.ToString(CultureInfo.InvariantCulture);

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }
}
