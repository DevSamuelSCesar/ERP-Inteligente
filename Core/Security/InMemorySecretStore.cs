using System;
using System.Collections.Generic;

namespace erp_inteligente.Core.Security;

/// <summary>Non-persistent <see cref="ISecretStore"/> for tests. Thread-safe; stores and returns copies.</summary>
public sealed class InMemorySecretStore : ISecretStore
{
    private readonly Dictionary<(string, string), byte[]> _items = new();
    private readonly object _lock = new();

    public byte[]? Get(string databaseId, string keyName)
    {
        lock (_lock)
            return _items.TryGetValue((databaseId, keyName), out var v) ? (byte[])v.Clone() : null;
    }

    public void Set(string databaseId, string keyName, byte[] secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        lock (_lock)
            _items[(databaseId, keyName)] = (byte[])secret.Clone();
    }

    public void Delete(string databaseId, string keyName)
    {
        lock (_lock)
            _items.Remove((databaseId, keyName));
    }
}
