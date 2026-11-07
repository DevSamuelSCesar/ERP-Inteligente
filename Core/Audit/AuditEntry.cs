using System;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace erp_inteligente.Core.Audit;

/// <summary>
/// One audit event before it is appended. The constructor rejects values that the table or the hash rule
/// cannot represent, so an invalid entry never reaches the database.
/// </summary>
public sealed record AuditEntry
{
    public const int MaxTargetLength = 200;
    public const int MaxDetailsJsonLength = 4000;

    // \z instead of $: "$" would also accept a trailing newline.
    private static readonly Regex ActionPattern = new(@"^[a-z][a-z0-9_.]{0,99}\z", RegexOptions.CultureInvariant);

    /// <summary>Creates an entry. Throws <see cref="ArgumentException"/> when a field is out of range.</summary>
    public AuditEntry(
        AuditActorType actorType,
        long? actorId,
        long? onBehalfOf,
        string action,
        string? target,
        AuditOutcome outcome,
        string? detailsJson = null)
    {
        ValidateFields(action, target, detailsJson);

        ActorType = actorType;
        ActorId = actorId;
        OnBehalfOf = onBehalfOf;
        Action = action;
        Target = target;
        Outcome = outcome;
        DetailsJson = detailsJson;
    }

    public AuditActorType ActorType { get; init; }
    public long? ActorId { get; init; }
    public long? OnBehalfOf { get; init; }
    public string Action { get; init; }
    public string? Target { get; init; }
    public AuditOutcome Outcome { get; init; }
    public string? DetailsJson { get; init; }

    /// <summary>
    /// Re-checks the fields. Needed because a <c>with</c> expression copies a record without running its constructor.
    /// </summary>
    internal void EnsureValid() => ValidateFields(Action, Target, DetailsJson);

    private static void ValidateFields(string? action, string? target, string? detailsJson)
    {
        if (action is null || !ActionPattern.IsMatch(action))
            throw new ArgumentException("Action must match ^[a-z][a-z0-9_.]{0,99}$.", "action");

        if (target is not null && target.Length > MaxTargetLength)
            throw new ArgumentException($"Target must be at most {MaxTargetLength} characters.", "target");

        if (detailsJson is null) return;
        if (detailsJson.Length > MaxDetailsJsonLength)
            throw new ArgumentException($"DetailsJson must be at most {MaxDetailsJsonLength} characters.", "detailsJson");
        try
        {
            using var document = JsonDocument.Parse(detailsJson);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException("DetailsJson must be valid JSON.", "detailsJson", ex);
        }
    }
}
