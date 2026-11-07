using System;

namespace erp_inteligente.Core.Data;

/// <summary>
/// Thrown when the database cannot be opened or created: wrong key, corrupt file, unknown schema version
/// or a failed bootstrap. Messages never contain key material.
/// </summary>
public sealed class DatabaseUnavailableException : Exception
{
    public DatabaseUnavailableException(string message) : base(message) { }
    public DatabaseUnavailableException(string message, Exception inner) : base(message, inner) { }
}
