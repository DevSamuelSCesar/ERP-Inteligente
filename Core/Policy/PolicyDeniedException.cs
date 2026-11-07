using System;

namespace erp_inteligente.Core.Policy;

/// <summary>Thrown when a user lacks a permission. The denial has already been written to the audit log.</summary>
public sealed class PolicyDeniedException : Exception
{
    public PolicyDeniedException(string permission)
        : base($"Permissão negada: {permission}")
    {
        Permission = permission;
    }

    /// <summary>The permission code that was required.</summary>
    public string Permission { get; }
}
