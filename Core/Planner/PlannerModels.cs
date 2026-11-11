using System.Collections.Generic;
using erp_inteligente.Core.Engine;
using erp_inteligente.Core.Llm;
using erp_inteligente.Core.Skills;
using erp_inteligente.Models;

namespace erp_inteligente.Core.Planner;

/// <summary>Outcome of one request handled by the <see cref="Planner"/>.</summary>
public abstract record PlannerResult
{
    /// <summary>
    /// External content read during the request that looked like an instruction to the model (feature 6). It was
    /// delivered as marked reference data, audited as <c>ai.injection</c>, and the operator is warned.
    /// </summary>
    public IReadOnlyList<SuspiciousContent> Suspicious { get; init; } = [];
}

/// <summary>
/// Where an instruction-like text was found: <see cref="Source"/> is the data block ("tool:get_product", "memorias",
/// "knowledge:path#heading"), <see cref="Reference"/> the row it came from (a SKU, a code) when known, and
/// <see cref="Code"/> the detector rule (see <see cref="erp_inteligente.Core.Safety.InjectionDetector"/>).
/// </summary>
public sealed record SuspiciousContent(string Source, string? Reference, string Code);

/// <summary>A suggested next step the operator can click; choosing it sends <see cref="Request"/> as if typed.</summary>
public sealed record PlannerOption(string Label, string Request);

/// <summary>A memory the Nexus saved (or reinforced) during the request, shown to the operator.</summary>
public sealed record RememberedMemory(long Id, string Content, bool Reinforced);

/// <summary>A plain-text answer to a question, with optional suggested next steps.</summary>
public sealed record PlannerAnswer(string Text) : PlannerResult
{
    public IReadOnlyList<PlannerOption> Options { get; init; } = [];

    /// <summary>A skill drafted by the model for the supervisor to review; nothing is saved before they confirm.</summary>
    public SkillProposal? Skill { get; init; }

    /// <summary>Memories saved by the model during this request.</summary>
    public IReadOnlyList<RememberedMemory> Remembered { get; init; } = [];

    public PlannerAnswer(string text, IReadOnlyList<PlannerOption> options, SkillProposal? skill = null) : this(text)
    {
        Options = options;
        Skill = skill;
    }
}

/// <summary>A valid skill draft (<see cref="SkillDocument.Validate"/> found no issue). <see cref="CurrentVersion"/> is set when the name already exists.</summary>
public sealed record SkillProposal(SkillDocument Document, string Reason, int? CurrentVersion);

/// <summary>One plan that passed the dry run (nothing was written), with the title, rationale and assumptions the model gave.</summary>
public sealed record ProposalItem(string Title, string Rationale, IReadOnlyList<string> Assumptions, PlanDraft Draft, EnginePreview Preview);

/// <summary>
/// One to <see cref="PlannerOptions.MaxProposals"/> alternative plans that passed the dry run. <see cref="Text"/> is the model's
/// short closing message and <see cref="Options"/> the suggested next steps.
/// </summary>
public sealed record PlannerProposals(IReadOnlyList<ProposalItem> Items, string? Text, IReadOnlyList<PlannerOption> Options, SkillProposal? Skill = null) : PlannerResult
{
    /// <summary>Memories saved by the model during this request.</summary>
    public IReadOnlyList<RememberedMemory> Remembered { get; init; } = [];
}

/// <summary>A limit was reached (attempts, rounds or tokens). <see cref="Reason"/> is a Portuguese business message.</summary>
public sealed record PlannerEscalated(string Reason) : PlannerResult;

/// <summary>The provider failed. <see cref="Reason"/> is a Portuguese message without technical details.</summary>
public sealed record PlannerFailed(string Reason) : PlannerResult;

/// <summary>One finished exchange kept for the next request; <see cref="Reply"/> keeps <c>reasoning_content</c>.</summary>
public sealed record PlannerTurn(string Request, LlmMessage Reply);

/// <summary>Limits of one planner run (references/ARQUITETURA.md, section 4).</summary>
public sealed record PlannerOptions(
    int MaxInvalidPlans = 4,
    int MaxRounds = 12,
    int MaxProposals = 3,
    int MaxTokensPerCall = 8192,
    int MaxTokensPerRequest = 60000,
    int MaxHistoryTurns = 10)
{
    public static PlannerOptions Default { get; } = new();
}
