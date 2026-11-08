using System;
using System.Threading;
using System.Threading.Tasks;
using erp_inteligente.Models;

namespace erp_inteligente.Services;

/// <summary>Answer of the Nexus to a request: short plain text and, when data must change, a draft for the engine.</summary>
public sealed record NexusReply(string Text, PlanDraft? Draft = null, bool IsError = false);

/// <summary>Turns a natural-language request into a reply or a plan (the Planner, seen from the UI).</summary>
public interface INexusService
{
    /// <param name="status">Receives short status lines such as "Consultando saldo do ABC-123".</param>
    Task<NexusReply> SendAsync(string request, UserSession user, IProgress<string> status, CancellationToken cancellationToken = default);
}
