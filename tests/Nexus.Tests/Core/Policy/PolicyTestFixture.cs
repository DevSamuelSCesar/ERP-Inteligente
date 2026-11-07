using erp_inteligente.Core.Audit;
using erp_inteligente.Core.Policy;
using erp_inteligente.Models;
using Microsoft.Data.Sqlite;
using Nexus.Tests.Core;

namespace Nexus.Tests.Core.Policy;

/// <summary>Seeded database plus the policy services, shared by the policy and auth tests.</summary>
public sealed class PolicyTestFixture : IDisposable
{
    public PolicyTestFixture()
    {
        Time = new ManualTimeProvider(new DateTimeOffset(2026, 11, 7, 12, 0, 0, TimeSpan.Zero));
        Audit = new AuditLog(Db.Database, Time);
        Policy = new PolicyEngine(Db.Database, Audit);
        App = new ApplicationDb(Db.Database, Policy);
    }

    public TestDatabase Db { get; } = new();
    public ManualTimeProvider Time { get; }
    public AuditLog Audit { get; }
    public PolicyEngine Policy { get; }
    public ApplicationDb App { get; }

    public void Dispose() => Db.Dispose();

    public SqliteConnection Open() => Db.Database.OpenApplication();

    public void Exec(string sql, params (string Name, object Value)[] parameters)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        command.ExecuteNonQuery();
    }

    public long Scalar(string sql, params (string Name, object Value)[] parameters)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        return Convert.ToInt64(command.ExecuteScalar());
    }

    /// <summary>Builds a session for a seeded user, reading ids from the database.</summary>
    public UserSession SessionFor(string username)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT u.id, u.display_name, u.role_id, r.name, u.warehouse_id, w.code " +
            "FROM users u JOIN roles r ON r.id = u.role_id JOIN warehouses w ON w.id = u.warehouse_id " +
            "WHERE u.username = $u";
        command.Parameters.AddWithValue("$u", username);
        using var reader = command.ExecuteReader();
        reader.Read();
        var roleId = reader.GetInt64(2);
        return new UserSession(reader.GetInt64(0), username, reader.GetString(1), roleId, reader.GetString(3),
            reader.GetInt64(4), reader.GetString(5), Policy.PermissionsForRole(roleId));
    }
}
