using erp_inteligente.Core.Security;

namespace Nexus.Tests.Core.Security;

public class PasswordHasherTests
{
    private static readonly PasswordHasher Fast = new(new Argon2Parameters(1024, 1, 1));

    [Fact]
    public void RoundTrip_AcceptsCorrectAndRejectsWrongPassword()
    {
        var h = Fast.Hash("s3nha-ñ-çã");
        Assert.True(Fast.Verify("s3nha-ñ-çã", h));
        Assert.False(Fast.Verify("s3nha-n-ca", h));
    }

    [Fact]
    public void Hash_UsesPhcFormatWithParameters()
    {
        var h = Fast.Hash("abc");
        var parts = h.Split('$');
        Assert.Equal(6, parts.Length);
        Assert.Equal("argon2id", parts[1]);
        Assert.Equal("v=19", parts[2]);
        Assert.Equal("m=1024,t=1,p=1", parts[3]);
        Assert.Equal(22, parts[4].Length); // 16 bytes, unpadded base64
        Assert.Equal(43, parts[5].Length); // 32 bytes, unpadded base64
        Assert.DoesNotContain('=', parts[4] + parts[5]);
    }

    [Fact]
    public void Hash_DefaultParameters()
    {
        var hasher = new PasswordHasher(Argon2Parameters.Default);
        var h = hasher.Hash("x");
        Assert.StartsWith("$argon2id$v=19$m=65536,t=3,p=4$", h);
        Assert.True(hasher.Verify("x", h));
    }

    [Fact]
    public void Verify_UsesEmbeddedParameters()
    {
        var h = new PasswordHasher(new Argon2Parameters(2048, 2, 2)).Hash("pw");
        Assert.True(Fast.Verify("pw", h));
    }

    [Fact]
    public void Hash_SaltsAreUnique() => Assert.NotEqual(Fast.Hash("same"), Fast.Hash("same"));

    [Theory]
    [InlineData("argon2id$FICTICIO$op.joao")]
    [InlineData("service-account:no-password")]
    [InlineData("")]
    [InlineData("$argon2id$v=19$m=1024,t=1,p=1$AAAA")]
    [InlineData("$argon2i$v=19$m=1024,t=1,p=1$AAAAAAAAAAAAAAAAAAAAAA$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("$argon2id$v=19$m=abc,t=1,p=1$AAAAAAAAAAAAAAAAAAAAAA$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("$argon2id$v=19$m=1024,t=1,p=1$!!!$@@@")]
    public void Verify_MalformedReturnsFalse(string encoded) => Assert.False(Fast.Verify("x", encoded));

    [Theory]
    [InlineData("m=2097152,t=1,p=1")] // > 1 GiB
    [InlineData("m=1024,t=11,p=1")]
    [InlineData("m=1024,t=1,p=17")]
    [InlineData("m=1024,t=0,p=1")]
    [InlineData("m=99999999999,t=1,p=1")]
    public void Verify_RejectsOutOfBoundParameters(string ps)
    {
        var good = Fast.Hash("x").Split('$');
        var tampered = $"${good[1]}${good[2]}${ps}${good[4]}${good[5]}";
        Assert.False(Fast.Verify("x", tampered));
    }

    [Fact]
    public void LongPassword_RejectedEverywhere()
    {
        var longPw = new string('a', 1025);
        Assert.Throws<ArgumentException>(() => Fast.Hash(longPw));
        Assert.False(Fast.Verify(longPw, Fast.Hash("a")));
    }

    [Fact]
    public void VerifyDummy_DoesNotThrow() => Fast.VerifyDummy("whatever");

    [Fact]
    public void Constructor_RejectsInvalidParameters() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new PasswordHasher(new Argon2Parameters(1024, 99, 1)));
}
