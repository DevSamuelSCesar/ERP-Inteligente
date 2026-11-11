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

        var warnings = Warnings(result.Suspicious);
        return result switch
        {
            PlannerAnswer answer => new NexusReply(answer.Text, Options: MapOptions(answer.Options), Skill: MapSkill(answer.Skill),
                Memories: MapMemories(answer.Remembered), Warnings: warnings),
            PlannerProposals proposals => new NexusReply(
                proposals.Text ?? "",
                Proposals: proposals.Items.Select(i => new NexusProposal(i.Title, i.Rationale, i.Assumptions, i.Draft)).ToList(),
                Options: MapOptions(proposals.Options),
                Skill: MapSkill(proposals.Skill),
                Memories: MapMemories(proposals.Remembered),
                Warnings: warnings),
            PlannerEscalated escalated => new NexusReply(escalated.Reason, IsEscalated: true, Warnings: warnings),
            PlannerFailed failed => new NexusReply(failed.Reason, IsError: true, Warnings: warnings),
            _ => new NexusReply("Falha ao processar o pedido. Nada foi gravado.", IsError: true),
        };
    }

    private const int MaxWarningPlaces = 3;

    /// <summary>
    /// One warning line in business language for the instruction-like content found while answering (feature 6). The
    /// text itself is never repeated: only where it was (a product, a procedure, a memory).
    /// </summary>
    internal static IReadOnlyList<string>? Warnings(IReadOnlyList<SuspiciousContent> suspicious)
    {
        if (suspicious.Count == 0) return null;
        var places = suspicious.Select(Place).Distinct(StringComparer.Ordinal).ToList();
        var shown = string.Join(", ", places.Take(MaxWarningPlaces)) + (places.Count > MaxWarningPlaces ? " e outros dados" : "");
        return
        [
            places.Count == 1
                ? $"Atenção: {shown} tem um texto com cara de instrução para o assistente. Ele foi tratado só como dado e " +
                  "nada foi feito por causa dele. Confira o que foi proposto antes de confirmar e avise o supervisor."
                : $"Atenção: {shown} têm textos com cara de instrução para o assistente. Eles foram tratados só como dados e " +
                  "nada foi feito por causa deles. Confira o que foi proposto antes de confirmar e avise o supervisor.",
        ];
    }

    private static string Place(SuspiciousContent item)
    {
        var tool = item.Source.StartsWith("tool:", StringComparison.Ordinal) ? item.Source["tool:".Length..] : null;
        var reference = item.Reference;
        if (item.Source == "memorias" || tool == "search_memories") return "uma memória do armazém";
        if (item.Source.StartsWith("knowledge:", StringComparison.Ordinal) || tool == "search_knowledge")
            return "um procedimento da base de conhecimento";
        return tool switch
        {
            "search_products" or "get_product" or "get_stock" when reference is not null => $"o produto {reference}",
            "search_products" or "get_product" or "get_stock" => "um produto",
            "search_locations" when reference is not null => $"o endereço {reference}",
            "search_zones" when reference is not null => $"a zona {reference}",
            "search_lots" when reference is not null => $"o lote {reference}",
            "search_orders" when reference is not null => $"o pedido {reference}",
            "search_partners" => "um fornecedor ou cliente",
            _ => "um dado consultado",
        };
    }

    private static NexusSkillProposal? MapSkill(SkillProposal? skill) =>
        skill is null ? null : new NexusSkillProposal(skill.Document, skill.Reason, skill.CurrentVersion);

    private static IReadOnlyList<NexusMemoryNote>? MapMemories(IReadOnlyList<RememberedMemory> remembered) =>
        remembered.Count == 0 ? null : remembered.Select(m => new NexusMemoryNote(m.Content, m.Reinforced)).ToList();

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
