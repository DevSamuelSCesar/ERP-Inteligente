using System;
using System.Collections.Generic;

namespace erp_inteligente.Core.Skills;

/// <summary>Lifecycle of a skill stored in the database.</summary>
public enum SkillStatus
{
    Active,
    Archived,
}

/// <summary>Who wrote a skill version. <see cref="BuiltIn"/> is only for the skills shipped with the app.</summary>
public enum SkillOrigin
{
    User,
    Ai,
    BuiltIn,
}

/// <summary>A skill as shown in the management screen.</summary>
/// <param name="OverridesBuiltIn">A database skill that replaces (or, when archived, would replace) a built-in one of the same name.</param>
public sealed record SkillRecord(
    string Name,
    string Description,
    IReadOnlyList<string> Operations,
    string Body,
    int Version,
    SkillStatus Status,
    SkillOrigin Origin,
    string? UpdatedAt,
    string? UpdatedByName,
    bool IsBuiltIn,
    bool OverridesBuiltIn);

/// <summary>One saved version of a skill (<c>skill_versions</c>).</summary>
public sealed record SkillVersionRecord(
    int Version,
    string Description,
    IReadOnlyList<string> Operations,
    string Body,
    SkillOrigin Origin,
    string? SavedByName,
    string SavedAt);

/// <summary>An entry of the skill index sent to the LLM: no body.</summary>
public sealed record SkillIndexEntry(string Name, string Description, IReadOnlyList<string> Operations, int Version);

/// <summary>A full skill loaded for the LLM when it activates the skill.</summary>
public sealed record SkillContent(string Name, string Description, IReadOnlyList<string> Operations, string Body, int Version);

/// <summary>The skill failed validation. <see cref="Issues"/> lists every problem.</summary>
public sealed class SkillValidationException(IReadOnlyList<SkillIssue> issues)
    : Exception("A skill é inválida: " + string.Join(" ", System.Linq.Enumerable.Select(issues, i => i.Message)))
{
    public IReadOnlyList<SkillIssue> Issues { get; } = issues;
}

/// <summary>The skill changed since the caller loaded it.</summary>
public sealed class SkillConflictException() : Exception("A skill foi alterada por outra pessoa. Recarregue.");

/// <summary>A refused skill operation (not found, built-in, wrong state). The message is Portuguese and safe to show.</summary>
public sealed class SkillStoreException(string message, Exception? inner = null) : Exception(message, inner);
