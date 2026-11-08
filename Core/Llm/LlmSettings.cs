using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using erp_inteligente.Core.Security;

namespace erp_inteligente.Core.Llm;

/// <summary>DeepSeek key and model choice, kept in the OS key store (never in SQLite).</summary>
public sealed class LlmSettings(ISecretStore store, string databaseId)
{
    public const string KeyEntry = "llm.deepseek.key";
    public const string ModelEntry = "llm.deepseek.model";
    public const string StrongModel = "deepseek-v4-pro";
    public const string FastModel = "deepseek-flash";
    public const int MinKeyLength = 8;
    public const int MaxKeyLength = 200;

    public static IReadOnlyList<string> SupportedModels { get; } = [StrongModel, FastModel];

    private readonly ISecretStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly string _databaseId = !string.IsNullOrEmpty(databaseId) ? databaseId : throw new ArgumentException("Database id is required.", nameof(databaseId));

    public bool IsConfigured
    {
        get
        {
            var key = _store.Get(_databaseId, KeyEntry);
            if (key is null) return false;
            var ok = key.Length > 0;
            Array.Clear(key);
            return ok;
        }
    }

    /// <summary>Validates and stores the key. Throws <see cref="ArgumentException"/> without echoing the key.</summary>
    public void SetKey(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var trimmed = key.Trim();
        if (trimmed.Length < MinKeyLength || trimmed.Length > MaxKeyLength)
            throw new ArgumentException($"The API key must have {MinKeyLength} to {MaxKeyLength} characters.", nameof(key));
        foreach (var c in trimmed)
            if (c < 0x21 || c > 0x7E)
                throw new ArgumentException("The API key must contain only printable ASCII characters without spaces.", nameof(key));
        var bytes = Encoding.UTF8.GetBytes(trimmed);
        try { _store.Set(_databaseId, KeyEntry, bytes); }
        finally { Array.Clear(bytes); }
    }

    public void ClearKey() => _store.Delete(_databaseId, KeyEntry);

    /// <summary>Returns the key bytes, or null. The caller must zero the array.</summary>
    public byte[]? ReadKey() => _store.Get(_databaseId, KeyEntry);

    /// <summary>Selected model; an unknown stored value falls back to <see cref="StrongModel"/>.</summary>
    public string Model
    {
        get
        {
            var raw = _store.Get(_databaseId, ModelEntry);
            if (raw is null) return StrongModel;
            var text = Encoding.UTF8.GetString(raw);
            return SupportedModels.Contains(text) ? text : StrongModel;
        }
        set
        {
            if (!SupportedModels.Contains(value ?? string.Empty))
                throw new ArgumentException("Unsupported model.", nameof(value));
            _store.Set(_databaseId, ModelEntry, Encoding.UTF8.GetBytes(value!));
        }
    }
}
