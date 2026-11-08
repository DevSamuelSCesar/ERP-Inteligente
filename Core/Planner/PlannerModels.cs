using erp_inteligente.Core.Engine;
using erp_inteligente.Core.Llm;
using erp_inteligente.Models;

namespace erp_inteligente.Core.Planner;

/// <summary>Outcome of one request handled by the <see cref="Planner"/>.</summary>
public abstract record PlannerResult;

/// <summary>A plain-text answer to a question.</summary>
public sealed record PlannerAnswer(string Text) : PlannerResult;

/// <summary>A plan that passed the dry run (nothing was written). <see cref="Text"/> is an optional short explanation.</summary>
public sealed record PlannerProposal(PlanDraft Draft, EnginePreview Preview, string? Text) : PlannerResult;

/// <summary>A limit was reached (attempts, rounds or tokens). <see cref="Reason"/> is a Portuguese business message.</summary>
public sealed record PlannerEscalated(string Reason) : PlannerResult;

/// <summary>The provider failed. <see cref="Reason"/> is a Portuguese message without technical details.</summary>
public sealed record PlannerFailed(string Reason) : PlannerResult;

/// <summary>One finished exchange kept for the next request; <see cref="Reply"/> keeps <c>reasoning_content</c>.</summary>
public sealed record PlannerTurn(string Request, LlmMessage Reply);

/// <summary>Limits of one planner run (references/ARQUITETURA.md, section 4).</summary>
public sealed record PlannerOptions(
    int MaxInvalidPlans = 3,
    int MaxRounds = 8,
    int MaxTokensPerCall = 8192,
    int MaxTokensPerRequest = 60000,
    int MaxHistoryTurns = 10)
{
    public static PlannerOptions Default { get; } = new();
}
