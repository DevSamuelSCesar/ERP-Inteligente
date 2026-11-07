using System;

namespace erp_inteligente.Core.Security;

/// <summary>
/// Abstraction over the operating-system key store that holds the per-database keys.
/// <c>keyName</c> is <c>"sqlcipher"</c> or <c>"column"</c>.
/// </summary>
public interface ISecretStore
{
    /// <summary>Returns a copy of the secret, or <c>null</c> when it is not stored.</summary>
    byte[]? Get(string databaseId, string keyName);

    /// <summary>Stores (or overwrites) the secret.</summary>
    void Set(string databaseId, string keyName, byte[] secret);

    /// <summary>Removes the secret; does nothing when it is absent.</summary>
    void Delete(string databaseId, string keyName);
}

/// <summary>Thrown when the OS key store cannot be reached or refuses the operation.</summary>
public sealed class SecretStoreUnavailableException : Exception
{
    public SecretStoreUnavailableException(string message) : base(message) { }
    public SecretStoreUnavailableException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>Thrown when a database file exists but its keys are not in the key store.</summary>
public sealed class MissingDatabaseKeyException(string databaseId)
    : Exception($"The keys for the existing database '{databaseId}' are missing from the key store.")
{
    public string DatabaseId { get; } = databaseId;
}
