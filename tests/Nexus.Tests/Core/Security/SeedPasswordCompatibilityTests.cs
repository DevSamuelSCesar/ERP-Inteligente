using erp_inteligente.Core.Security;

namespace Nexus.Tests.Core.Security;

/// <summary>
/// The seed hashes were produced by argon2-cffi (the reference C implementation); the managed
/// Konscious implementation must accept them, and vice versa the format must stay PHC-standard.
/// </summary>
public class SeedPasswordCompatibilityTests
{
    private const string DemoPassword = "nexus-demo-2026";
    private const string SeedHashOfOperator =
        "$argon2id$v=19$m=65536,t=3,p=4$4H3UK/Bmc+aoDlJBaqyenA$qVxvHSzAzpt4OnIntR3nD14T/SSD/TSRo8ARXm5bBeU";

    [Fact]
    public void Seed_hash_from_reference_implementation_verifies()
    {
        var hasher = new PasswordHasher(Argon2Parameters.Default);

        Assert.True(hasher.Verify(DemoPassword, SeedHashOfOperator));
        Assert.False(hasher.Verify("nexus-demo-2025", SeedHashOfOperator));
    }
}
