using erp_inteligente.Models;
using erp_inteligente.Services;
using Nexus.Tests.Core.Policy;

namespace Nexus.Tests.Services;

/// <summary>Session service whose current user a test can switch.</summary>
public sealed class FakeSessionService(UserSession current) : ISessionService
{
    public UserSession Current { get; set; } = current;
}

/// <summary>Collects the status lines reported by the Nexus.</summary>
public sealed class StatusLog : IProgress<string>
{
    public List<string> Lines { get; } = [];

    public void Report(string value) => Lines.Add(value);
}

public static class SqliteTestData
{
    /// <summary>
    /// Adds warehouse CD-02 with 99 units of ABC-123 at DOCA-99 and an operator of CD-02 (user 6), so that tests can
    /// prove that warehouse 1 never sees warehouse 2 data.
    /// </summary>
    public static void AddSecondWarehouse(PolicyTestFixture f)
    {
        f.Exec("INSERT INTO warehouses (id, code, name) VALUES (2, 'CD-02', 'Centro de Distribuição Fictício 02')");
        f.Exec("INSERT INTO zones (id, warehouse_id, code, name) VALUES (5, 2, 'DOCA', 'Docas do CD 02')");
        f.Exec("INSERT INTO locations (id, zone_id, code, capacity) VALUES (17, 5, 'DOCA-99', 100)");
        f.Exec("INSERT INTO stock_balances (warehouse_id, product_id, location_id, lot_id, quantity, reserved_qty) " +
               "VALUES (2, 1, 17, NULL, 99, 0)");
        f.Exec("INSERT INTO users (id, username, display_name, password_hash, role_id, warehouse_id) " +
               "VALUES (6, 'op.ana', 'Ana (operadora)', 'no-login-in-tests', 1, 2)");
    }
}
