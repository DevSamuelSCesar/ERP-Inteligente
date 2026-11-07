namespace erp_inteligente.Core.Audit;

/// <summary>
/// Result of walking the audit chain. <paramref name="RowsChecked"/> counts the rows that passed before the first
/// broken row, or all rows when the chain is valid. <paramref name="FirstBrokenId"/> and <paramref name="Reason"/>
/// are set only when the chain is invalid.
/// </summary>
public sealed record AuditVerification(bool IsValid, long RowsChecked, long? FirstBrokenId, string? Reason);
