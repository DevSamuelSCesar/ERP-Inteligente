using System.Collections.Generic;
using System.Threading.Tasks;
using erp_inteligente.Core.Memory;

namespace erp_inteligente.Services;

/// <summary>
/// Memories of the current session's warehouse (references/ARQUITETURA.md, section 8): everybody with <c>stock.read</c>
/// reads them, only <c>memory.manage</c> (the supervisor) creates, corrects, archives and reactivates. The store checks
/// the permission again, runs the protection filter and audits every write.
/// </summary>
public interface IMemoryService
{
    Task<IReadOnlyList<MemoryRecord>> ListAsync();

    /// <summary>A refused text (filter or scope) comes back with <c>Saved = false</c> and the reason; nothing is written.</summary>
    Task<MemoryWriteResult> CreateAsync(MemoryKind kind, MemoryScope scope, string? scopeRef, string content);

    Task<MemoryWriteResult> UpdateAsync(long id, string content);

    Task ArchiveAsync(long id);

    Task ReactivateAsync(long id);
}
