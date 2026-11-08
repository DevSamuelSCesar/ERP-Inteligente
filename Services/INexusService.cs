using System;
using System.Threading;
using System.Threading.Tasks;
using erp_inteligente.Models;

namespace erp_inteligente.Services;

/// <summary>
/// Answer of the Nexus to a request: short plain text and, when data must change, a draft of the Planner with its
/// dry run. The draft is only written after the requester confirms it.
/// </summary>
/// <param name="IsEscalated">The Planner hit a limit (attempts, rounds or tokens): the task needs the operator.</param>
public sealed record NexusReply(
    string Text,
    PlanDraft? Draft = null,
    PlanPreview? Preview = null,
    bool IsError = false,
    bool IsEscalated = false);

/// <summary>Turns a natural-language request into a reply or a plan (the Planner, seen from the UI).</summary>
public interface INexusService
{
    /// <param name="status">Receives short status lines such as "Consultando saldo do ABC-123".</param>
    Task<NexusReply> SendAsync(string request, UserSession user, IProgress<string> status, CancellationToken cancellationToken = default);
}
