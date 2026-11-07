using System;

namespace erp_inteligente.Core.Audit;

/// <summary>Who performed an audited action. Stored as lowercase text in <c>audit_log.actor_type</c>.</summary>
public enum AuditActorType
{
    User,
    Ai,
    System,
}

/// <summary>Conversions between <see cref="AuditActorType"/> and its database text.</summary>
public static class AuditActorTypes
{
    /// <summary>Returns the database text: <c>user</c>, <c>ai</c> or <c>system</c>.</summary>
    public static string ToDbValue(this AuditActorType value) => value switch
    {
        AuditActorType.User => "user",
        AuditActorType.Ai => "ai",
        AuditActorType.System => "system",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown audit actor type."),
    };

    /// <summary>Parses the database text. Throws <see cref="ArgumentException"/> for any other value.</summary>
    public static AuditActorType FromDbValue(string value) => value switch
    {
        "user" => AuditActorType.User,
        "ai" => AuditActorType.Ai,
        "system" => AuditActorType.System,
        _ => throw new ArgumentException($"Unknown audit actor type '{value}'.", nameof(value)),
    };
}
