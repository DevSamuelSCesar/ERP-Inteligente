using System.Collections.Generic;

namespace erp_inteligente.Core.Skills;

/// <summary>A problem found in a skill. <see cref="Field"/> is one of <c>nome</c>, <c>descricao</c>, <c>operacoes</c>, <c>corpo</c>.</summary>
/// <param name="Field">The skill field involved.</param>
/// <param name="Message">Portuguese message for the operator.</param>
public sealed record SkillIssue(string Field, string Message);

/// <summary>Result of <see cref="SkillDocument.Parse"/>: the document when no issue was found.</summary>
public sealed record SkillParseResult(SkillDocument? Document, IReadOnlyList<SkillIssue> Issues)
{
    /// <summary>True when the text was parsed (the document still needs <see cref="SkillDocument.Validate"/>).</summary>
    public bool Succeeded => Document is not null && Issues.Count == 0;
}
