using System.Collections.Generic;
using System.Threading.Tasks;
using erp_inteligente.Core.Skills;

namespace erp_inteligente.Services.Sqlite;

/// <summary>Bridges the screens to <see cref="SkillStore"/>, always acting as the logged-in user; the work runs off the UI thread.</summary>
public sealed class SqliteSkillService(SkillStore store, ISessionService session) : ISkillService
{
    public Task<IReadOnlyList<SkillRecord>> ListAsync() =>
        Task.Run(() => store.List(session.Current));

    public Task<SkillRecord> SaveAsync(SkillDocument document, SkillOrigin origin, int? expectedVersion) =>
        Task.Run(() => store.Save(session.Current, document, origin, expectedVersion));

    public Task ArchiveAsync(string name) =>
        Task.Run(() => store.Archive(session.Current, name));

    public Task ReactivateAsync(string name) =>
        Task.Run(() => store.Reactivate(session.Current, name));
}
