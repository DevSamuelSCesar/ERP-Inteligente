using erp_inteligente.Core.Security;

namespace Nexus.Tests.Core.Security;

/// <summary>
/// The seed hashes were produced by argon2-cffi (the reference C implementation); the managed
/// Konscious implementation must accept them, and vice versa the format must stay PHC-standard.
/// </summary>
public class SeedPasswordCompatibilityTests
{
    private const string DemoPassword = "demo";
    private const string SeedHashOfOperator =
        "$argon2id$v=19$m=65536,t=3,p=4$nqN+GxHYJbAl3p4JDrtF0w$6vn3eeADPWe1PZ/tcMhn7vBpk9VMzBgNneefuOBzsBE";

    [Fact]
    public void Seed_hash_from_reference_implementation_verifies()
    {
        var hasher = new PasswordHasher(Argon2Parameters.Default);

        Assert.True(hasher.Verify(DemoPassword, SeedHashOfOperator));
        Assert.False(hasher.Verify("demo2", SeedHashOfOperator));
    }
}
