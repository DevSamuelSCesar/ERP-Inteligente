namespace erp_inteligente.Core.Audit;

/// <summary>Appends events to the tamper-evident audit chain.</summary>
public interface IAuditLog
{
    /// <summary>Appends one row in its own transaction and returns the new row id.</summary>
    long Append(AuditEntry entry);
}
