using erp_inteligente.Core.Audit;

namespace Nexus.Tests.Core.Audit;

public class AuditHasherTests
{
    // Cross-language vectors: the same values are checked by db/tests/test_db.py (audit_hash_v1).

    [Fact]
    public void ComputeV1_MatchesPythonVector_WithDetailsJson()
    {
        var hash = AuditHasher.ComputeV1(
            AuditHasher.Genesis, 7, "2026-11-07T12:00:00Z", "user", 1, null,
            "stock.move", "sku:ABC-123", "allowed", """{"qty":2}""");

        Assert.Equal("344c473b21c5e78f8e4fd28a95096cb3057c37a68fd33e172a3179a3677c63b5", hash);
    }

    [Fact]
    public void ComputeV1_MatchesPythonVector_WithNonAsciiTargetAndNullDetails()
    {
        // 'endereço' is 8 characters but 9 UTF-8 bytes; the length prefix must count bytes.
        var hash = AuditHasher.ComputeV1(
            AuditHasher.Genesis, 9, "2026-11-07T12:05:00Z", "system", null, null,
            "location.view", "endereço", "allowed", null);

        Assert.Equal("4566d77da7ce58fe9f71e062ff6c769791bf94b8ff9278cc010852ccb737ee5e", hash);
    }

    [Fact]
    public void ComputeV1_ChangesWhenAnyFieldChanges()
    {
        var baseline = AuditHasher.ComputeV1(AuditHasher.Genesis, 1, "2026-11-07T12:00:00Z", "ai", 5, 1,
            "plan.submit", "plan:1", "allowed", null);

        Assert.NotEqual(baseline, AuditHasher.ComputeV1("0", 1, "2026-11-07T12:00:00Z", "ai", 5, 1,
            "plan.submit", "plan:1", "allowed", null));
        Assert.NotEqual(baseline, AuditHasher.ComputeV1(AuditHasher.Genesis, 2, "2026-11-07T12:00:00Z", "ai", 5, 1,
            "plan.submit", "plan:1", "allowed", null));
        Assert.NotEqual(baseline, AuditHasher.ComputeV1(AuditHasher.Genesis, 1, "2026-11-07T12:00:01Z", "ai", 5, 1,
            "plan.submit", "plan:1", "allowed", null));
        Assert.NotEqual(baseline, AuditHasher.ComputeV1(AuditHasher.Genesis, 1, "2026-11-07T12:00:00Z", "ai", 5, 1,
            "plan.submit", "plan:1", "denied", null));
        Assert.NotEqual(baseline, AuditHasher.ComputeV1(AuditHasher.Genesis, 1, "2026-11-07T12:00:00Z", "ai", 5, 1,
            "plan.submit", "plan:1", "allowed", "{}"));
    }

    [Fact]
    public void ComputeV1_NullAndEmptyFieldsDiffer()
    {
        // F(null) is "-;" and F("") is "0:;", so a missing target and an empty target hash differently.
        var withNull = AuditHasher.ComputeV1(AuditHasher.Genesis, 1, "2026-11-07T12:00:00Z", "system", null, null,
            "x.y", null, "allowed", null);
        var withEmpty = AuditHasher.ComputeV1(AuditHasher.Genesis, 1, "2026-11-07T12:00:00Z", "system", null, null,
            "x.y", "", "allowed", null);

        Assert.NotEqual(withNull, withEmpty);
    }

    [Fact]
    public void ComputeV1_ReturnsLowercaseHex()
    {
        var hash = AuditHasher.ComputeV1(AuditHasher.Genesis, 1, "2026-11-07T12:00:00Z", "system", null, null,
            "x.y", null, "allowed", null);

        Assert.Matches("^[0-9a-f]{64}$", hash);
    }

    [Fact]
    public void FormatTimestamp_IsUtcWithSecondsPrecision()
    {
        var local = new DateTimeOffset(2026, 11, 7, 9, 0, 0, 123, TimeSpan.FromHours(-3));

        Assert.Equal("2026-11-07T12:00:00Z", AuditHasher.FormatTimestamp(local));
    }

    [Fact]
    public void TimestampFormat_IsTheDocumentedConstant()
    {
        Assert.Equal("yyyy-MM-ddTHH:mm:ssZ", AuditHasher.TimestampFormat);
        Assert.Equal("GENESIS", AuditHasher.Genesis);
    }
}
