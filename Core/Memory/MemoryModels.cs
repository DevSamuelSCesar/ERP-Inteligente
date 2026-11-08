using System;
using System.Collections.Generic;

namespace erp_inteligente.Core.Memory;

/// <summary>What a memory is about (the <c>kind</c> column).</summary>
public enum MemoryKind { Correction, Preference, Pattern, Fact }

/// <summary>What a memory applies to (the <c>scope</c> column); <see cref="Global"/> has no reference.</summary>
public enum MemoryScope { Global, Skill, Sku, Location, Zone, Supplier, Customer }

/// <summary>Who wrote the memory (the <c>origin</c> column).</summary>
public enum MemoryOrigin { Ai, System, User }

/// <summary>Lifecycle of a memory; memories are never deleted, only archived.</summary>
public enum MemoryStatus { Active, Archived }

/// <summary>Database text conversions for the memory enums.</summary>
public static class MemoryDb
{
    public static string DbValue(this MemoryKind value) => value switch
    {
        MemoryKind.Correction => "correction",
        MemoryKind.Preference => "preference",
        MemoryKind.Pattern => "pattern",
        MemoryKind.Fact => "fact",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    public static string DbValue(this MemoryScope value) => value switch
    {
        MemoryScope.Global => "global",
        MemoryScope.Skill => "skill",
        MemoryScope.Sku => "sku",
        MemoryScope.Location => "location",
        MemoryScope.Zone => "zone",
        MemoryScope.Supplier => "supplier",
        MemoryScope.Customer => "customer",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    public static string DbValue(this MemoryOrigin value) => value switch
    {
        MemoryOrigin.Ai => "ai",
        MemoryOrigin.System => "system",
        MemoryOrigin.User => "user",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    public static MemoryKind KindFromDb(string value) => value switch
    {
        "correction" => MemoryKind.Correction,
        "preference" => MemoryKind.Preference,
        "pattern" => MemoryKind.Pattern,
        "fact" => MemoryKind.Fact,
        _ => throw new ArgumentException($"Unknown memory kind '{value}'.", nameof(value)),
    };

    public static MemoryScope ScopeFromDb(string value) => value switch
    {
        "global" => MemoryScope.Global,
        "skill" => MemoryScope.Skill,
        "sku" => MemoryScope.Sku,
        "location" => MemoryScope.Location,
        "zone" => MemoryScope.Zone,
        "supplier" => MemoryScope.Supplier,
        "customer" => MemoryScope.Customer,
        _ => throw new ArgumentException($"Unknown memory scope '{value}'.", nameof(value)),
    };

    public static MemoryOrigin OriginFromDb(string value) => value switch
    {
        "ai" => MemoryOrigin.Ai,
        "system" => MemoryOrigin.System,
        "user" => MemoryOrigin.User,
        _ => throw new ArgumentException($"Unknown memory origin '{value}'.", nameof(value)),
    };
}

/// <summary>A memory as shown in the management screen.</summary>
public sealed record MemoryRecord(
    long Id,
    MemoryKind Kind,
    MemoryScope Scope,
    string? ScopeRef,
    string Content,
    double Confidence,
    long UseCount,
    MemoryStatus Status,
    MemoryOrigin Origin,
    string? CreatedByName,
    long? SourcePlanId,
    string CreatedAt,
    string? UpdatedAt,
    string? LastUsedAt);

/// <summary>
/// Outcome of a write. <see cref="Saved"/> is false when the content or scope reference was refused
/// (<see cref="Reason"/> is then a pt-BR message and nothing was written).
/// </summary>
public sealed record MemoryWriteResult(bool Saved, long Id, string Content, bool Reinforced, string? Reason)
{
    public static MemoryWriteResult Refused(string reason) => new(false, 0, string.Empty, false, reason);
}

/// <summary>A retrieved memory; <see cref="Score"/> is the final ranking score (higher is better).</summary>
public sealed record MemoryHit(
    long Id, MemoryKind Kind, MemoryScope Scope, string? ScopeRef, string Content, double Confidence, double Score);

/// <summary>Options of <see cref="MemoryRetriever.Search"/>.</summary>
/// <param name="Limit">Clamped to 1..10.</param>
public sealed record MemorySearchOptions(
    int Limit = 5, double MinConfidence = 0.3, MemoryScope? Scope = null, string? ScopeRef = null);

/// <summary>A rule violation in memory management (not found, ...); the message is pt-BR and safe to show.</summary>
public sealed class MemoryStoreException : Exception
{
    public MemoryStoreException(string message) : base(message) { }
    public MemoryStoreException(string message, Exception inner) : base(message, inner) { }
}
