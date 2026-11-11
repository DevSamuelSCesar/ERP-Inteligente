using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using erp_inteligente.Models;

namespace erp_inteligente.Services;

/// <summary>
/// One plan proposed by the Planner: a short title, why it was chosen, what it assumed (values the operator did not
/// give) and the draft. Nothing is written until the requester confirms it.
/// </summary>
public sealed record NexusProposal(string Title, string Rationale, IReadOnlyList<string> Assumptions, PlanDraft Draft,
    PlanPreview? Preview = null);

/// <summary>A suggested next step; choosing it sends <see cref="Request"/> to the Nexus as if typed.</summary>
public sealed record NexusOption(string Label, string Request);

/// <summary>A skill drafted by the Nexus. It is saved only when a supervisor confirms it; <see cref="CurrentVersion"/> is set when the name already exists.</summary>
public sealed record NexusSkillProposal(erp_inteligente.Core.Skills.SkillDocument Document, string Reason, int? CurrentVersion);

/// <summary>A memory the Nexus saved (or reinforced) while handling the request; shown to the operator, who can manage it in Memórias.</summary>
public sealed record NexusMemoryNote(string Content, bool Reinforced);

/// <summary>
/// Answer of the Nexus to a request: short plain text, up to three alternative proposals (or a single
/// <see cref="Draft"/>) and suggested next steps. Proposals are only written after the requester confirms one.
/// </summary>
/// <param name="IsEscalated">The Planner hit a limit (attempts, rounds or tokens): the task needs the operator.</param>
/// <param name="Warnings">Data read for the request had text that looked like an instruction to the assistant (feature 6).</param>
public sealed record NexusReply(
    string Text,
    PlanDraft? Draft = null,
    PlanPreview? Preview = null,
    bool IsError = false,
    bool IsEscalated = false,
    IReadOnlyList<NexusProposal>? Proposals = null,
    IReadOnlyList<NexusOption>? Options = null,
    NexusSkillProposal? Skill = null,
    IReadOnlyList<NexusMemoryNote>? Memories = null,
    IReadOnlyList<string>? Warnings = null);

/// <summary>Turns a natural-language request into a reply or a plan (the Planner, seen from the UI).</summary>
public interface INexusService
{
    /// <param name="status">Receives short status lines such as "Consultando saldo do ABC-123".</param>
    Task<NexusReply> SendAsync(string request, UserSession user, IProgress<string> status, CancellationToken cancellationToken = default);
}
