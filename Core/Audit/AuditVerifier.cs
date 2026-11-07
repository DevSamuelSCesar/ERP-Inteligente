using System;
using Microsoft.Data.Sqlite;

namespace erp_inteligente.Core.Audit;

/// <summary>
/// Walks <c>audit_log</c> in id order and checks each row's <c>prev_hash</c> link and recomputed hash
/// (rule v1, references/ARQUITETURA.md, section 12.7). Stops at the first broken row.
/// </summary>
public static class AuditVerifier
{
    private const string Query =
        "SELECT id, occurred_at, actor_type, actor_id, on_behalf_of, action, target, outcome, " +
        "details_json, prev_hash, hash FROM audit_log ORDER BY id";

    public static AuditVerification Verify(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using var command = connection.CreateCommand();
        command.CommandText = Query;
        using var reader = command.ExecuteReader();

        long rowsChecked = 0;
        string expectedPrev = AuditHasher.Genesis;
        while (reader.Read())
        {
            var id = reader.GetInt64(0);
            var prevHash = reader.GetString(9);
            var storedHash = reader.GetString(10);

            if (prevHash != expectedPrev)
                return Broken(rowsChecked, id, "prev_hash does not match the previous row's hash");

            var recomputed = AuditHasher.ComputeV1(
                prevHash,
                id,
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetInt64(3),
                reader.IsDBNull(4) ? null : reader.GetInt64(4),
                reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8));

            if (!string.Equals(recomputed, storedHash, StringComparison.Ordinal))
                return Broken(rowsChecked, id, "hash does not match the recomputed value");

            expectedPrev = storedHash;
            rowsChecked++;
        }

        return new AuditVerification(true, rowsChecked, null, null);
    }

    private static AuditVerification Broken(long rowsChecked, long id, string reason) =>
        new(false, rowsChecked, id, reason);
}
