using System;

namespace erp_inteligente.Core.Audit;

/// <summary>Result of an audited action. Stored as lowercase text in <c>audit_log.outcome</c>.</summary>
public enum AuditOutcome
{
    Allowed,
    Denied,
    Failed,
}

/// <summary>Conversions between <see cref="AuditOutcome"/> and its database text.</summary>
public static class AuditOutcomes
{
    /// <summary>Returns the database text: <c>allowed</c>, <c>denied</c> or <c>failed</c>.</summary>
    public static string ToDbValue(this AuditOutcome value) => value switch
    {
        AuditOutcome.Allowed => "allowed",
        AuditOutcome.Denied => "denied",
        AuditOutcome.Failed => "failed",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown audit outcome."),
    };

    /// <summary>Parses the database text. Throws <see cref="ArgumentException"/> for any other value.</summary>
    public static AuditOutcome FromDbValue(string value) => value switch
    {
        "allowed" => AuditOutcome.Allowed,
        "denied" => AuditOutcome.Denied,
        "failed" => AuditOutcome.Failed,
        _ => throw new ArgumentException($"Unknown audit outcome '{value}'.", nameof(value)),
    };
}
