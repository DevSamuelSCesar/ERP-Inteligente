namespace erp_inteligente.Core.Engine;

/// <summary>
/// A problem found while validating a plan. <see cref="Message"/> is written for the operator in Portuguese
/// business language; <see cref="Code"/> is stable and machine-readable (e.g. <c>schema.required</c>).
/// </summary>
/// <param name="StepIndex">0-based step index; <c>null</c> when the problem concerns the whole plan.</param>
/// <param name="Field">The argument or document field involved (e.g. <c>qty</c>, <c>steps</c>), if any.</param>
/// <param name="Code">Machine-readable code.</param>
/// <param name="Message">Portuguese message for the operator.</param>
public sealed record PlanError(int? StepIndex, string? Field, string Code, string Message);
