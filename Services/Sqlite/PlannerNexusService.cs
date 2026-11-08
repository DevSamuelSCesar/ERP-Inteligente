using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using erp_inteligente.Core.Planner;
using erp_inteligente.Models;

namespace erp_inteligente.Services.Sqlite;

/// <summary>
/// The Nexus backed by the Planner (ROADMAP.md, feature 3). Keeps the last finished exchanges of the session in memory
/// (never in the database) so the operator can refer to them; a different user starts with an empty history.
/// Proposed plans (up to three alternatives) are returned as drafts only: the requester confirms it before the engine writes anything.
/// </summary>
public sealed class PlannerNexusService(Planner planner, int maxHistoryTurns = 10) : INexusService
{
    private readonly List<PlannerTurn> _history = [];
    private readonly Lock _gate = new();
    private long? _historyOwner;

    public async Task<NexusReply> SendAsync(string request, UserSession user, IProgress<string> status, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(status);

        var (result, turn) = await planner.RunAsync(user, Snapshot(user), request, status, cancellationToken);
        if (turn is not null)
            Remember(user, turn);

        return result switch
        {
            PlannerAnswer answer => new NexusReply(answer.Text, Options: MapOptions(answer.Options)),
            PlannerProposals proposals => new NexusReply(
                proposals.Text ?? "",
                Proposals: proposals.Items.Select(i => new NexusProposal(i.Title, i.Rationale, i.Assumptions, i.Draft)).ToList(),
                Options: MapOptions(proposals.Options)),
            PlannerEscalated escalated => new NexusReply(escalated.Reason, IsEscalated: true),
            PlannerFailed failed => new NexusReply(failed.Reason, IsError: true),
            _ => new NexusReply("Falha ao processar o pedido. Nada foi gravado.", IsError: true),
        };
    }

    private static IReadOnlyList<NexusOption> MapOptions(IReadOnlyList<PlannerOption> options) =>
        options.Select(o => new NexusOption(o.Label, o.Request)).ToList();

    private IReadOnlyList<PlannerTurn> Snapshot(UserSession user)
    {
        lock (_gate)
        {
            if (_historyOwner != user.UserId)
            {
                _history.Clear();
                _historyOwner = user.UserId;
            }
            return _history.ToArray();
        }
    }

    private void Remember(UserSession user, PlannerTurn turn)
    {
        lock (_gate)
        {
            if (_historyOwner != user.UserId)
                return;
            _history.Add(turn);
            if (_history.Count > maxHistoryTurns)
                _history.RemoveRange(0, _history.Count - maxHistoryTurns);
        }
    }
}
