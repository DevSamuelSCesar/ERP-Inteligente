using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace erp_inteligente.Core.Audit;

/// <summary>
/// Hash rule v1 of the audit chain (references/ARQUITETURA.md, section 12.7). Must stay identical to
/// <c>audit_hash_v1</c> in db/tests/test_db.py, which holds the cross-language known-answer vectors.
/// </summary>
public static class AuditHasher
{
    /// <summary><c>prev_hash</c> of the first row of the chain.</summary>
    public const string Genesis = "GENESIS";

    /// <summary>UTC timestamp with seconds precision, as stored in <c>occurred_at</c>.</summary>
    public const string TimestampFormat = "yyyy-MM-ddTHH:mm:ssZ";

    private const string HashPrefix = "nexus-audit-v1\n";

    /// <summary>
    /// Lowercase hex SHA-256 of the UTF-8 bytes of <c>"nexus-audit-v1\n" + prevHash + "\n"</c> followed by each
    /// field in the order of the table. Null fields are written as <c>-;</c>; other fields as
    /// <c>&lt;UTF-8 byte length&gt;:&lt;text&gt;;</c>.
    /// </summary>
    public static string ComputeV1(
        string prevHash,
        long id,
        string occurredAt,
        string actorType,
        long? actorId,
        long? onBehalfOf,
        string action,
        string? target,
        string outcome,
        string? detailsJson)
    {
        ArgumentNullException.ThrowIfNull(prevHash);

        var canonical = new StringBuilder();
        canonical.Append(HashPrefix).Append(prevHash).Append('\n');
        AppendField(canonical, id.ToString(CultureInfo.InvariantCulture));
        AppendField(canonical, occurredAt);
        AppendField(canonical, actorType);
        AppendField(canonical, actorId?.ToString(CultureInfo.InvariantCulture));
        AppendField(canonical, onBehalfOf?.ToString(CultureInfo.InvariantCulture));
        AppendField(canonical, action);
        AppendField(canonical, target);
        AppendField(canonical, outcome);
        AppendField(canonical, detailsJson);

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }

    /// <summary>Formats a time as <c>AAAA-MM-DDTHH:MM:SSZ</c> in UTC, dropping sub-second digits.</summary>
    public static string FormatTimestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString(TimestampFormat, CultureInfo.InvariantCulture);

    private static void AppendField(StringBuilder builder, string? value)
    {
        if (value is null)
        {
            builder.Append("-;");
            return;
        }
        builder.Append(Encoding.UTF8.GetByteCount(value).ToString(CultureInfo.InvariantCulture))
            .Append(':')
            .Append(value)
            .Append(';');
    }
}
