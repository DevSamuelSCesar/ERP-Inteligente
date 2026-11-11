using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using erp_inteligente.Core.Audit;
using erp_inteligente.Core.Data;
using erp_inteligente.Core.Memory;
using erp_inteligente.Core.Skills;
using erp_inteligente.Core.Policy;
using erp_inteligente.Core.Recipes;
using erp_inteligente.Models;
using Microsoft.Data.Sqlite;

namespace erp_inteligente.Core.Engine;

/// <summary>Dry-run result. <see cref="Document"/> is null when the text could not be parsed; <see cref="Diff"/> is null when there are errors.</summary>
public sealed record EnginePreview(PlanDocument? Document, IReadOnlyList<PlanError> Errors, RiskLevel Risk, PlanDiff? Diff)
{
    public bool IsValid => Errors.Count == 0;
}

/// <summary>
/// Outcome of <see cref="PlanEngine.Submit"/>. <see cref="PlanId"/> is null when nothing was stored (the text is not JSON);
/// <see cref="Status"/> is then <c>invalid</c>, otherwise the stored <c>plans.status</c> after processing.
/// </summary>
public sealed record EngineSubmission(long? PlanId, string Status, IReadOnlyList<PlanError> Errors, PlanDiff? Diff);

/// <summary>
/// The plan state machine (references/ARQUITETURA.md, sections 4, 5 and 11): validating, then failed, pending_approval
/// or executed; pending plans end as executed, failed or rejected. Every transition is written together with its audit
/// row. The requester's permissions decide what a plan may do, even when someone else approves it. Methods are
/// synchronous; callers run them off the UI thread.
/// </summary>
public sealed class PlanEngine
{
    private const string DslVersion = "1.0";
    private const int MaxFailureReasonLength = 1000;
    private const int MaxRejectionReasonLength = 200;
    private const int MaxErrorCodesInAudit = 10;

    private readonly NexusDatabase _database;
    private readonly PolicyEngine _policy;
    private readonly AuditLog _audit;
    private readonly TimeProvider _time;
    private readonly PlanSimulator _simulator;
    private readonly PlanExecutor _executor;
    private readonly MemoryStore? _memories;
    private readonly RecipeStore? _recipes;
    private readonly MemoryFeedback? _feedback;

    /// <param name="memories">When set, rejecting an AI plan also saves a system correction memory (null = off).</param>
    /// <param name="recipes">When set, outcomes of recipe plans are counted and repeated plans become candidates (feature 7).</param>
    /// <param name="feedback">When set, the memories behind an AI plan gain or lose confidence with its outcome, and recurring
    /// errors become pattern memories (references/ARQUITETURA.md, section 8).</param>
    public PlanEngine(NexusDatabase database, PolicyEngine policy, AuditLog audit, TimeProvider time, MemoryStore? memories = null,
        RecipeStore? recipes = null, MemoryFeedback? feedback = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(time);
        _database = database;
        _policy = policy;
        _audit = audit;
        _time = time;
        _memories = memories;
        _recipes = recipes;
        _feedback = feedback;
        _simulator = new PlanSimulator(database, time);
        _executor = new PlanExecutor(database, time);
    }

    /// <summary>Validates and simulates a plan for <paramref name="requester"/>. Writes nothing.</summary>
    /// <param name="plannedBy"><c>user</c>, <c>ai</c> or <c>recipe</c>; a plan of the AI only gets the AI ∩ requester rights.</param>
    /// <exception cref="PolicyDeniedException">The requester (or the AI acting for them) lacks <c>plan.submit</c>.</exception>
    public EnginePreview Preview(UserSession requester, string planJson, string plannedBy = "user")
    {
        ArgumentNullException.ThrowIfNull(requester);
        ArgumentNullException.ThrowIfNull(planJson);
        RequireOrigin(plannedBy);
        RequireSubmit(requester, plannedBy);

        var parsed = DslParser.Parse(planJson);
        if (parsed.Document is null)
            return new EnginePreview(null, parsed.Errors, RiskLevel.Reversible, null);

        var result = _simulator.Simulate(requester, parsed.Document, AllowedFor(requester, plannedBy));
        return new EnginePreview(parsed.Document, result.Errors, Reportable(result.Risk), result.Succeeded ? result.Diff : null);
    }

    /// <summary>
    /// Stores the plan and processes it: invalid plans become <c>failed</c>, critical ones <c>pending_approval</c> and
    /// reversible ones execute at once. Text that is not JSON (or is too large) is not stored.
    /// </summary>
    /// <param name="plannedBy"><c>user</c>, <c>ai</c> or <c>recipe</c>; a plan of the AI only gets the AI ∩ requester rights.</param>
    /// <param name="recipeId">The recipe the plan was built from; stored only when <paramref name="plannedBy"/> is <c>recipe</c>.</param>
    /// <param name="memoryIds">The memories the Planner had in front of it when it proposed the plan (AI plans only); recorded
    /// with the submission so the outcome can raise or lower their confidence.</param>
    /// <exception cref="PolicyDeniedException">The requester (or the AI acting for them) lacks <c>plan.submit</c>.</exception>
    public EngineSubmission Submit(UserSession requester, string planJson, string plannedBy, long? recipeId = null,
        IReadOnlyCollection<long>? memoryIds = null)
    {
        var submission = SubmitCore(requester, planJson, plannedBy, plannedBy == "recipe" ? recipeId : null,
            plannedBy == "ai" ? memoryIds : null);
        if (submission.Status == "failed")
            _feedback?.ObserveErrors(requester, SkillOf(planJson), submission.Errors);
        if (submission.PlanId is { } id) AfterDecision(id);
        return submission;
    }

    private static string? SkillOf(string planJson) => DslParser.Parse(planJson).Document?.Skill;

    private EngineSubmission SubmitCore(UserSession requester, string planJson, string plannedBy, long? recipeId,
        IReadOnlyCollection<long>? memoryIds)
    {
        ArgumentNullException.ThrowIfNull(requester);
        ArgumentNullException.ThrowIfNull(planJson);
        RequireOrigin(plannedBy);
        RequireSubmit(requester, plannedBy);

        var parsed = DslParser.Parse(planJson);
        if (!IsStorable(planJson))
            return new EngineSubmission(null, "invalid", parsed.Errors.Count > 0 ? parsed.Errors : [InvalidDocument()], null);

        var document = parsed.Document;
        var risk = document is null ? RiskLevel.Reversible : Reportable(RiskOf(document));
        var planId = InsertPlan(requester, plannedBy, document, planJson, risk, recipeId, memoryIds);

        if (document is null)
            return Fail(planId, requester, parsed.Errors, null);

        var allowed = AllowedFor(requester, plannedBy);
        if (risk == RiskLevel.Critical)
        {
            var simulation = _simulator.Simulate(requester, document, allowed);
            if (!simulation.Succeeded)
                return Fail(planId, requester, simulation.Errors, null);

            var diff = simulation.Diff!;
            using var connection = _database.OpenApplication();
            using var transaction = connection.BeginTransaction(deferred: false);
            using (var update = Command(connection, transaction,
                "UPDATE plans SET status = 'pending_approval', simulated_diff_json = $diff WHERE id = $id AND status = 'validating'"))
            {
                update.Parameters.AddWithValue("$diff", PlanDiffJson.Serialize(diff));
                update.Parameters.AddWithValue("$id", planId);
                RequireOneRow(update.ExecuteNonQuery());
            }
            transaction.Commit();
            return new EngineSubmission(planId, "pending_approval", [], diff);
        }

        var run = _executor.Execute(requester, document, planId, allowed,
            (c, t, r) => MarkExecuted(c, t, planId, requester, null, "validating", document, r),
            (c, t, r) => MarkFailed(c, t, planId, requester, r.Errors, null));
        return run.Succeeded
            ? new EngineSubmission(planId, "executed", [], run.Diff)
            : new EngineSubmission(planId, "failed", run.Errors, null);
    }

    /// <summary>
    /// Approves a pending plan and executes it with the requester's permissions. If the state changed since the
    /// submission the plan ends as <c>failed</c> (no exception, nothing applied).
    /// </summary>
    /// <exception cref="UnauthorizedAccessException">The approver is the AI or the requester.</exception>
    /// <exception cref="PolicyDeniedException">The approver lacks <c>plan.approve</c>.</exception>
    /// <exception cref="InvalidOperationException">The plan does not exist for this warehouse or is not pending.</exception>
    public void Approve(UserSession approver, long planId)
    {
        ArgumentNullException.ThrowIfNull(approver);
        var row = LoadPendingForDecision(approver, planId, "plan.approve");

        var requester = LoadSession(row.RequestedBy);
        var parsed = DslParser.Parse(row.PlanJson, row.DslVersion);
        if (parsed.Document is null)
        {
            using var connection = _database.OpenApplication();
            using var transaction = connection.BeginTransaction(deferred: false);
            MarkFailed(connection, transaction, planId, requester, parsed.Errors, approver.UserId);
            transaction.Commit();
            AfterDecision(planId);
            return;
        }

        var document = parsed.Document;
        var run = _executor.Execute(requester, document, planId, AllowedFor(requester, row.PlannedBy),
            (c, t, r) => MarkExecuted(c, t, planId, requester, approver, "pending_approval", document, r),
            (c, t, r) => MarkFailed(c, t, planId, requester, r.Errors, approver.UserId));
        if (!run.Succeeded)
            _feedback?.ObserveErrors(requester, document.Skill, run.Errors);
        AfterDecision(planId);
    }

    /// <summary>Rejects a pending plan with a reason of 1 to 200 characters (trimmed).</summary>
    /// <exception cref="ArgumentException">The reason is empty or too long.</exception>
    /// <exception cref="UnauthorizedAccessException">The approver is the AI or the requester.</exception>
    /// <exception cref="PolicyDeniedException">The approver lacks <c>plan.approve</c>.</exception>
    /// <exception cref="InvalidOperationException">The plan does not exist for this warehouse or is not pending.</exception>
    public void Reject(UserSession approver, long planId, string reason)
    {
        ArgumentNullException.ThrowIfNull(approver);
        ArgumentNullException.ThrowIfNull(reason);
        var trimmed = reason.Trim();
        if (trimmed.Length is 0 or > MaxRejectionReasonLength)
            throw new ArgumentException($"A rejeição exige um motivo de 1 a {MaxRejectionReasonLength} caracteres.", nameof(reason));

        var row = LoadPendingForDecision(approver, planId, "plan.reject");

        using var connection = _database.OpenApplication();
        using var transaction = connection.BeginTransaction(deferred: false);
        using (var update = Command(connection, transaction,
            "UPDATE plans SET status = 'rejected', rejected_by = $by, rejection_reason = $reason " +
            "WHERE id = $id AND status = 'pending_approval'"))
        {
            update.Parameters.AddWithValue("$by", approver.UserId);
            update.Parameters.AddWithValue("$reason", trimmed);
            update.Parameters.AddWithValue("$id", planId);
            RequireOneRow(update.ExecuteNonQuery());
        }
        _audit.AppendWithin(connection, transaction, new AuditEntry(
            AuditActorType.User, approver.UserId, null, "plan.reject", PlanTarget(planId), AuditOutcome.Allowed,
            new JsonObject { ["reason"] = trimmed, ["requested_by"] = row.RequestedBy }.ToJsonString()));
        transaction.Commit();

        if (_memories is not null && row.PlannedBy == "ai")
            RememberRejection(approver, planId, trimmed);
        AfterDecision(planId);
    }

    /// <summary>
    /// Learning (references/ARQUITETURA.md, section 4, step 10): a plan built from a recipe counts as a success or a failure
    /// (a rejection is a failure); any other executed plan may turn its structure into a candidate recipe. Runs after the
    /// decision is committed; an error here never undoes or hides the decision.
    /// </summary>
    private void AfterDecision(long planId)
    {
        if (_recipes is null && _feedback is null) return;
        try
        {
            string plannedBy;
            string status;
            long? recipeId;
            using (var connection = _database.OpenApplication())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT planned_by, status, recipe_id FROM plans WHERE id = $id";
                command.Parameters.AddWithValue("$id", planId);
                using var reader = command.ExecuteReader();
                if (!reader.Read()) return;
                plannedBy = reader.GetString(0);
                status = reader.GetString(1);
                recipeId = reader.IsDBNull(2) ? null : reader.GetInt64(2);
            }
            if (status is "validating" or "pending_approval") return;
            // The memories behind an AI plan: executed means they helped, rejected means a supervisor contradicted them.
            // A failure (the stock changed, a value was wrong) says nothing about them and becomes an error pattern instead.
            if (_feedback is not null && plannedBy == "ai" && status is "executed" or "rejected")
                _feedback.PlanOutcome(planId, _feedback.MemoriesOfPlan(planId), status == "executed", RequesterOf(planId));
            if (_recipes is null) return;
            if (recipeId is { } recipe)
                _recipes.RecordOutcome(recipe, planId, status == "executed");
            else if (status == "executed" && plannedBy != "recipe")
                _recipes.LearnFrom(planId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            System.Diagnostics.Trace.TraceWarning("Recipe learning failed: " + ex.GetType().Name);
        }
    }

    /// <summary>
    /// The supervisor's reason becomes a system correction memory (scope: the plan's skill, else global). The memory filter
    /// applies; a refusal or any error saves nothing and never affects the rejection, which is already committed.
    /// </summary>
    private void RememberRejection(UserSession approver, long planId, string reason)
    {
        try
        {
            string? skill = null;
            string? intent = null;
            using (var connection = _database.OpenApplication())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT skill, intent FROM plans WHERE id = $id";
                command.Parameters.AddWithValue("$id", planId);
                using var reader = command.ExecuteReader();
                if (reader.Read())
                {
                    skill = reader.IsDBNull(0) ? null : reader.GetString(0);
                    intent = reader.IsDBNull(1) ? null : reader.GetString(1);
                }
            }
            var scope = SkillDocument.IsValidName(skill) ? MemoryScope.Skill : MemoryScope.Global;
            // The intent is model-written: only a short plain identifier is quoted.
            var quotedIntent = intent is { Length: > 0 and <= 40 } && intent.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-')
                ? $" (plano: {intent})"
                : string.Empty;
            _memories!.Remember(approver, MemoryKind.Correction, scope, scope == MemoryScope.Skill ? skill : null,
                "Plano rejeitado: " + reason + quotedIntent, MemoryOrigin.System, planId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            System.Diagnostics.Trace.TraceWarning("Saving the rejection memory failed: " + ex.GetType().Name);
        }
    }

    // ---- decisions -------------------------------------------------------------------------------------------

    /// <summary>
    /// Shared checks of approve and reject: the AI never decides, the permission is live, the plan belongs to the
    /// approver's warehouse and is pending, and the approver is not the requester.
    /// </summary>
    private PlanRow LoadPendingForDecision(UserSession approver, long planId, string auditAction)
    {
        if (IsAiRole(approver.RoleId))
            throw new UnauthorizedAccessException("A IA não aprova nem rejeita planos.");
        _policy.Require(approver, Permissions.PlanApprove);

        var row = LoadPlan(approver.WarehouseId, planId) ?? throw new InvalidOperationException("Plano não encontrado.");
        if (row.Status != "pending_approval")
            throw new InvalidOperationException("O plano não está aguardando aprovação.");
        if (row.RequestedBy == approver.UserId)
        {
            _audit.Append(new AuditEntry(AuditActorType.User, approver.UserId, null, auditAction, PlanTarget(planId),
                AuditOutcome.Denied, new JsonObject { ["reason"] = "self_decision" }.ToJsonString()));
            throw new UnauthorizedAccessException("O solicitante não pode decidir o próprio plano.");
        }
        return row;
    }

    // ---- transitions (run inside the caller's transaction) ---------------------------------------------------

    private void MarkExecuted(
        SqliteConnection connection, SqliteTransaction transaction, long planId, UserSession requester,
        UserSession? approver, string fromStatus, PlanDocument document, RunResult result)
    {
        var timestamp = AuditHasher.FormatTimestamp(_time.GetUtcNow());
        using (var update = Command(connection, transaction,
            "UPDATE plans SET status = 'executed', executed_at = $at, simulated_diff_json = $diff, " +
            "approved_by = COALESCE($approver, approved_by) WHERE id = $id AND status = $from"))
        {
            update.Parameters.AddWithValue("$at", timestamp);
            update.Parameters.AddWithValue("$diff", PlanDiffJson.Serialize(result.Diff!));
            update.Parameters.AddWithValue("$approver", (object?)approver?.UserId ?? DBNull.Value);
            update.Parameters.AddWithValue("$id", planId);
            update.Parameters.AddWithValue("$from", fromStatus);
            RequireOneRow(update.ExecuteNonQuery());
        }
        if (approver is not null)
            _audit.AppendWithin(connection, transaction, new AuditEntry(
                AuditActorType.User, approver.UserId, null, "plan.approve", PlanTarget(planId), AuditOutcome.Allowed,
                new JsonObject { ["requested_by"] = requester.UserId }.ToJsonString()));
        _audit.AppendWithin(connection, transaction, new AuditEntry(
            AuditActorType.System, null, requester.UserId, "plan.execute", PlanTarget(planId), AuditOutcome.Allowed,
            new JsonObject { ["steps"] = document.Steps.Count }.ToJsonString()));
    }

    private void MarkFailed(
        SqliteConnection connection, SqliteTransaction transaction, long planId, UserSession requester,
        IReadOnlyList<PlanError> errors, long? approverId)
    {
        using (var update = Command(connection, transaction,
            "UPDATE plans SET status = 'failed', failure_reason = $reason " +
            "WHERE id = $id AND status IN ('validating', 'pending_approval')"))
        {
            update.Parameters.AddWithValue("$reason", FailureReason(errors));
            update.Parameters.AddWithValue("$id", planId);
            RequireOneRow(update.ExecuteNonQuery());
        }
        var details = new JsonObject { ["errors"] = new JsonArray(errors.Select(e => e.Code).Distinct().Take(MaxErrorCodesInAudit)
            .Select(code => (JsonNode?)JsonValue.Create(code)).ToArray()) };
        if (approverId is { } approver)
            details["approver"] = approver;
        _audit.AppendWithin(connection, transaction, new AuditEntry(
            AuditActorType.System, null, requester.UserId, "plan.failed", PlanTarget(planId), AuditOutcome.Failed,
            details.ToJsonString()));
    }

    private EngineSubmission Fail(long planId, UserSession requester, IReadOnlyList<PlanError> errors, long? approverId)
    {
        using var connection = _database.OpenApplication();
        using var transaction = connection.BeginTransaction(deferred: false);
        MarkFailed(connection, transaction, planId, requester, errors, approverId);
        transaction.Commit();
        return new EngineSubmission(planId, "failed", errors, null);
    }

    private long? RequesterOf(long planId)
    {
        using var connection = _database.OpenApplication();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT requested_by FROM plans WHERE id = $id";
        command.Parameters.AddWithValue("$id", planId);
        return command.ExecuteScalar() is long id ? id : null;
    }

    private long InsertPlan(UserSession requester, string plannedBy, PlanDocument? document, string rawJson, RiskLevel risk, long? recipeId,
        IReadOnlyCollection<long>? memoryIds = null)
    {
        using var connection = _database.OpenApplication();
        using var transaction = connection.BeginTransaction(deferred: false);
        long id;
        using (var insert = Command(connection, transaction,
            "INSERT INTO plans (requested_by, planned_by, recipe_id, skill, intent, plan_json, dsl_version, risk, status, created_at) " +
            "VALUES ($by, $planned, $recipe, $skill, $intent, $json, $version, $risk, 'validating', $at) RETURNING id"))
        {
            insert.Parameters.AddWithValue("$by", requester.UserId);
            insert.Parameters.AddWithValue("$planned", plannedBy);
            insert.Parameters.AddWithValue("$recipe", (object?)recipeId ?? DBNull.Value);
            insert.Parameters.AddWithValue("$skill", (object?)document?.Skill ?? DBNull.Value);
            insert.Parameters.AddWithValue("$intent", document?.Intent ?? "unknown");
            insert.Parameters.AddWithValue("$json", document?.ToJson() ?? rawJson);
            insert.Parameters.AddWithValue("$version", DslVersion);
            insert.Parameters.AddWithValue("$risk", risk.ToDbValue());
            insert.Parameters.AddWithValue("$at", AuditHasher.FormatTimestamp(_time.GetUtcNow()));
            id = Convert.ToInt64(insert.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        }

        var onBehalf = plannedBy == "ai";
        long? aiUser = onBehalf ? FindAiUser(connection, transaction) : null;
        var aiDetails = new JsonObject { ["risk"] = risk.ToDbValue() };
        if (memoryIds is { Count: > 0 })
            aiDetails["memories"] = new JsonArray(memoryIds.Distinct().Take(MemoryFeedback.MaxMemoriesPerPlan)
                .Select(m => (JsonNode?)JsonValue.Create(m)).ToArray());
        var entry = onBehalf && aiUser is not null
            ? new AuditEntry(AuditActorType.Ai, aiUser, requester.UserId, "plan.submit", PlanTarget(id), AuditOutcome.Allowed,
                aiDetails.ToJsonString())
            : new AuditEntry(AuditActorType.User, requester.UserId, null, "plan.submit", PlanTarget(id), AuditOutcome.Allowed,
                new JsonObject { ["risk"] = risk.ToDbValue() }.ToJsonString());
        _audit.AppendWithin(connection, transaction, entry);
        transaction.Commit();
        return id;
    }

    // ---- reads -----------------------------------------------------------------------------------------------

    private sealed record PlanRow(long Id, long RequestedBy, string PlannedBy, string PlanJson, string DslVersion, string Status);

    private PlanRow? LoadPlan(long warehouseId, long planId)
    {
        using var connection = _database.OpenApplication();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT p.id, p.requested_by, p.planned_by, p.plan_json, p.dsl_version, p.status FROM plans p " +
            "JOIN users u ON u.id = p.requested_by WHERE p.id = $id AND u.warehouse_id = $warehouse_id";
        command.Parameters.AddWithValue("$id", planId);
        command.Parameters.AddWithValue("$warehouse_id", warehouseId);
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new PlanRow(reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5))
            : null;
    }

    /// <summary>Rebuilds a user's session from the database; permissions come from the live effective view.</summary>
    private UserSession LoadSession(long userId)
    {
        using var connection = _database.OpenApplication();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT u.username, u.display_name, u.role_id, r.name, u.warehouse_id, w.code FROM users u " +
            "JOIN roles r ON r.id = u.role_id JOIN warehouses w ON w.id = u.warehouse_id WHERE u.id = $id";
        command.Parameters.AddWithValue("$id", userId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            throw new InvalidOperationException("Solicitante do plano não encontrado.");
        var roleId = reader.GetInt64(2);
        var session = new UserSession(userId, reader.GetString(0), reader.GetString(1), roleId, reader.GetString(3),
            reader.GetInt64(4), reader.GetString(5), _policy.PermissionsForRole(roleId));
        return session;
    }

    private bool IsAiRole(long roleId)
    {
        using var connection = _database.OpenApplication();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT is_ai FROM roles WHERE id = $id";
        command.Parameters.AddWithValue("$id", roleId);
        return command.ExecuteScalar() is long flag && flag == 1;
    }

    private static long? FindAiUser(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = Command(connection, transaction,
            "SELECT u.id FROM users u JOIN roles r ON r.id = u.role_id WHERE r.is_ai = 1 ORDER BY u.id LIMIT 1");
        return command.ExecuteScalar() is long id ? id : null;
    }

    // ---- helpers ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The requester's effective permissions, read once just before a run: the check inside a transaction must not
    /// open more connections, and a change in the same instant would not be visible to a running plan anyway.
    /// </summary>
    /// <summary>
    /// Live rights a plan runs with: the requester's own, or for a plan of the AI the AI role ∩ requester
    /// (references/ARQUITETURA.md, section 12.5). Read again at approval, so a revoked right stops a pending plan.
    /// </summary>
    private Func<string, bool> AllowedFor(UserSession session, string plannedBy)
    {
        var permissions = plannedBy == "ai" ? _policy.EffectiveForAi(session) : _policy.PermissionsForRole(session.RoleId);
        return permissions.Contains;
    }

    private static void RequireOrigin(string plannedBy)
    {
        if (plannedBy is not ("user" or "ai" or "recipe"))
            throw new ArgumentException("plannedBy must be user, ai or recipe.", nameof(plannedBy));
    }

    /// <summary><c>plan.submit</c> for the requester and, for a plan of the AI, also for the AI role.</summary>
    private void RequireSubmit(UserSession requester, string plannedBy)
    {
        _policy.Require(requester, Permissions.PlanSubmit);
        if (plannedBy != "ai" || _policy.EffectiveForAi(requester).Contains(Permissions.PlanSubmit))
            return;

        _audit.Append(new AuditEntry(AuditActorType.Ai, null, requester.UserId, "policy.denied",
            "permission:" + Permissions.PlanSubmit, AuditOutcome.Denied));
        throw new PolicyDeniedException(Permissions.PlanSubmit);
    }

    private static RiskLevel RiskOf(PlanDocument document) =>
        RiskLevels.Max(document.Steps.Select(s => OperationCatalog.TryGet(s.Op, out var d) ? d.Risk : RiskLevel.Read));

    /// <summary>A stored or displayed plan is at least reversible: a plan with no known write is not a read.</summary>
    private static RiskLevel Reportable(RiskLevel risk) => risk == RiskLevel.Read ? RiskLevel.Reversible : risk;

    private static bool IsStorable(string text)
    {
        if (Encoding.UTF8.GetByteCount(text) > DslParser.MaxBytes) return false;
        try
        {
            using var _ = JsonDocument.Parse(text);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static PlanError InvalidDocument() =>
        new(null, null, "dsl.invalid_json", "O plano não é um documento válido.");

    private static string FailureReason(IReadOnlyList<PlanError> errors)
    {
        var text = errors.Count == 0 ? "O plano não pôde ser executado." : string.Join(" ", errors.Select(e => e.Message));
        return text.Length <= MaxFailureReasonLength ? text : text[..MaxFailureReasonLength];
    }

    private static string PlanTarget(long planId) => "plan:" + planId.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static void RequireOneRow(int affected)
    {
        if (affected != 1)
            throw new InvalidOperationException("O plano mudou de estado durante a operação.");
    }

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }
}
