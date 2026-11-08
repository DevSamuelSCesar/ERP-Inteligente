using System.Collections.Generic;
using System.Threading.Tasks;
using erp_inteligente.Core.Memory;

namespace erp_inteligente.Services.Sqlite;

/// <summary>Bridges the Memórias screen to <see cref="MemoryStore"/>, always as the logged-in user and off the UI thread.</summary>
public sealed class SqliteMemoryService(MemoryStore store, ISessionService session) : IMemoryService
{
    public Task<IReadOnlyList<MemoryRecord>> ListAsync() =>
        Task.Run(() => store.List(session.Current));

    public Task<MemoryWriteResult> CreateAsync(MemoryKind kind, MemoryScope scope, string? scopeRef, string content) =>
        Task.Run(() => store.Create(session.Current, kind, scope, scopeRef, content));

    public Task<MemoryWriteResult> UpdateAsync(long id, string content) =>
        Task.Run(() => store.Update(session.Current, id, content));

    public Task ArchiveAsync(long id) =>
        Task.Run(() => store.Archive(session.Current, id));

    public Task ReactivateAsync(long id) =>
        Task.Run(() => store.Reactivate(session.Current, id));
}
