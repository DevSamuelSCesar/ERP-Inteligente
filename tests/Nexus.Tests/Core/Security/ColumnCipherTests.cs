using System.Security.Cryptography;
using erp_inteligente.Core.Security;

namespace Nexus.Tests.Core.Security;

public class ColumnCipherTests
{
    private static ColumnCipher NewCipher() => new(RandomNumberGenerator.GetBytes(32));

    [Theory]
    [InlineData("123.456.789-09")]
    [InlineData("")]
    [InlineData("João da Conceição — 日本語 🙂")]
    public void RoundTrip(string text)
    {
        using var c = NewCipher();
        var enc = c.Encrypt(text, "customers", "tax_id_enc", 7);
        Assert.Equal(0x01, enc[0]);
        Assert.Equal(1 + 12 + System.Text.Encoding.UTF8.GetByteCount(text) + 16, enc.Length);
        Assert.Equal(text, c.Decrypt(enc, "customers", "tax_id_enc", 7));
    }

    [Fact]
    public void Encrypt_IsRandomised()
    {
        using var c = NewCipher();
        Assert.NotEqual(c.Encrypt("a", "t", "c_enc", 1), c.Encrypt("a", "t", "c_enc", 1));
    }

    [Theory]
    [InlineData(0)]  // version
    [InlineData(1)]  // nonce
    [InlineData(14)] // ciphertext
    [InlineData(-1)] // tag
    public void TamperedValue_Throws(int index)
    {
        using var c = NewCipher();
        var enc = c.Encrypt("secret", "t", "c_enc", 1);
        enc[index < 0 ? enc.Length + index : index] ^= 0x01;
        Assert.ThrowsAny<CryptographicException>(() => c.Decrypt(enc, "t", "c_enc", 1));
    }

    [Fact]
    public void TruncatedValue_Throws()
    {
        using var c = NewCipher();
        Assert.ThrowsAny<CryptographicException>(() => c.Decrypt(new byte[10], "t", "c_enc", 1));
    }

    [Theory]
    [InlineData("other", "c_enc", 1L)]
    [InlineData("t", "d_enc", 1L)]
    [InlineData("t", "c_enc", 2L)]
    public void WrongAad_Throws(string table, string column, long id)
    {
        using var c = NewCipher();
        var enc = c.Encrypt("secret", "t", "c_enc", 1);
        Assert.ThrowsAny<CryptographicException>(() => c.Decrypt(enc, table, column, id));
    }

    [Fact]
    public void WrongKey_Throws()
    {
        using var a = NewCipher();
        using var b = NewCipher();
        var enc = a.Encrypt("secret", "t", "c_enc", 1);
        Assert.ThrowsAny<CryptographicException>(() => b.Decrypt(enc, "t", "c_enc", 1));
    }

    [Theory]
    [InlineData("", "c")]
    [InlineData("t", "")]
    [InlineData("t.x", "c")]
    [InlineData("t", "c;drop")]
    public void InvalidIdentifiers_Throw(string table, string column)
    {
        using var c = NewCipher();
        Assert.Throws<ArgumentException>(() => c.Encrypt("x", table, column, 1));
    }

    [Fact]
    public void Constructor_RejectsWrongKeyLength() =>
        Assert.Throws<ArgumentException>(() => new ColumnCipher(new byte[16]));
}
