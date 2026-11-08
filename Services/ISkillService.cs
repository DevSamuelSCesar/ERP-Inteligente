using System.Collections.Generic;
using System.Threading.Tasks;
using erp_inteligente.Core.Skills;

namespace erp_inteligente.Services;

/// <summary>
/// Skills of the current session (references/ARQUITETURA.md, section 9): everybody with <c>stock.read</c> reads them,
/// only <c>skill.manage</c> writes. The store checks the permission again and audits every write.
/// </summary>
public interface ISkillService
{
    /// <summary>Built-in and stored skills, active and archived; a stored skill replaces the built-in of the same name.</summary>
    Task<IReadOnlyList<SkillRecord>> ListAsync();

    /// <summary>Creates or updates a skill (a new version). <paramref name="expectedVersion"/> detects concurrent edits.</summary>
    Task<SkillRecord> SaveAsync(SkillDocument document, SkillOrigin origin, int? expectedVersion);

    Task ArchiveAsync(string name);

    Task ReactivateAsync(string name);
}
