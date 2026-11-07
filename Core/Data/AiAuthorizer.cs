using System;
using System.Collections.Frozen;
using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace erp_inteligente.Core.Data;

/// <summary>
/// SQLite authorizer of the AI read connection (references/ARQUITETURA.md, section 12.6). Exact port of
/// <c>ai_authorizer</c> in <c>db/tests/test_db.py</c>, plus a denial of <c>load_extension</c>:
/// only SELECTs that read base tables through <c>v_ai_*</c> views are allowed.
/// </summary>
public static class AiAuthorizer
{
    /// <summary>Columns that hold ciphertext or password hashes; never readable by the AI.</summary>
    public static readonly FrozenSet<string> SensitiveColumns = new[]
    {
        "unit_cost_enc", "cpf_enc", "email_enc", "full_name_enc", "password_hash",
        "cnpj_enc", "contact_name_enc", "contact_email_enc", "phone_enc",
        "document_enc", "address_enc",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// Base tables behind the v_ai_* views. SQLite reports a READ with an empty column name when a joined
    /// table contributes no column (e.g. LEFT JOIN elimination); that exposes no data and is allowed only here.
    /// </summary>
    public static readonly FrozenSet<string> AiViewTables = new[]
    {
        "products", "locations", "zones", "lots", "stock_balances", "inbound_orders",
        "inbound_order_lines", "outbound_orders", "outbound_order_lines", "suppliers", "customers", "recipes",
    }.ToFrozenSet(StringComparer.Ordinal);

    // Static field: the native side keeps only a function pointer, so the delegate must never be collected.
    private static readonly delegate_authorizer Callback = Authorize;

    /// <summary>Installs the authorizer on an open connection. It stays until the connection is closed.</summary>
    public static void Install(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var rc = raw.sqlite3_set_authorizer(connection.Handle, Callback, null);
        if (rc != raw.SQLITE_OK)
            throw new DatabaseUnavailableException("Não foi possível instalar o autorizador da conexão de IA.");
    }

    /// <summary>The decision function, public for direct testing. Returns SQLITE_OK or SQLITE_DENY.</summary>
    public static int Decide(int action, string? arg1, string? arg2, string? source)
    {
        switch (action)
        {
            case raw.SQLITE_SELECT:
            case raw.SQLITE_RECURSIVE:
                return raw.SQLITE_OK;
            case raw.SQLITE_FUNCTION:
                // For SQLITE_FUNCTION the function name is the second argument.
                return string.Equals(arg2, "load_extension", StringComparison.OrdinalIgnoreCase)
                    ? raw.SQLITE_DENY
                    : raw.SQLITE_OK;
            case raw.SQLITE_READ:
                var table = arg1 ?? "";
                var column = arg2 ?? "";
                if (SensitiveColumns.Contains(column)) return raw.SQLITE_DENY;
                if (table.StartsWith("v_ai_", StringComparison.Ordinal)
                    || (source ?? "").StartsWith("v_ai_", StringComparison.Ordinal))
                    return raw.SQLITE_OK;
                if (column.Length == 0 && AiViewTables.Contains(table)) return raw.SQLITE_OK;
                return raw.SQLITE_DENY;
            default:
                return raw.SQLITE_DENY;
        }
    }

    private static int Authorize(object? userData, int action, utf8z p0, utf8z p1, utf8z dbName, utf8z source)
    {
        try
        {
            return Decide(action, p0.utf8_to_string(), p1.utf8_to_string(), source.utf8_to_string());
        }
        catch
        {
            return raw.SQLITE_DENY; // fail closed
        }
    }
}
