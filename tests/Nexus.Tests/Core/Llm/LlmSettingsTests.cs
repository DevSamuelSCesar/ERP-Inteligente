using System;
using System.Text;
using erp_inteligente.Core.Llm;
using erp_inteligente.Core.Security;
using Xunit;

namespace Nexus.Tests.Core.Llm;

public class LlmSettingsTests
{
    private static (LlmSettings, InMemorySecretStore) Make()
    {
        var store = new InMemorySecretStore();
        return (new LlmSettings(store, "db1"), store);
    }

    [Fact]
    public void SetReadClear()
    {
        var (s, store) = Make();
        Assert.False(s.IsConfigured);
        Assert.Null(s.ReadKey());
        s.SetKey("  sk-abcdefgh1234  ");
        Assert.True(s.IsConfigured);
        Assert.Equal("sk-abcdefgh1234", Encoding.UTF8.GetString(s.ReadKey()!));
        Assert.NotNull(store.Get("db1", "llm.deepseek.key"));
        s.ClearKey();
        Assert.False(s.IsConfigured);
        Assert.Null(s.ReadKey());
    }

    [Theory]
    [InlineData("short")]
    [InlineData("has space inside1")]
    [InlineData("tab\tinside12345")]
    [InlineData("acentuação12345")]
    [InlineData("")]
    public void SetKey_RejectsInvalid_WithoutEchoingKey(string key)
    {
        var (s, _) = Make();
        var e = Assert.Throws<ArgumentException>(() => s.SetKey(key));
        if (key.Length > 0) Assert.DoesNotContain(key, e.Message);
        Assert.False(s.IsConfigured);
    }

    [Fact]
    public void SetKey_RejectsTooLong()
    {
        var (s, _) = Make();
        Assert.Throws<ArgumentException>(() => s.SetKey(new string('a', 201)));
        s.SetKey(new string('a', 200));
        Assert.True(s.IsConfigured);
    }

    [Fact]
    public void Model_DefaultsToStrong_AndPersists()
    {
        var (s, _) = Make();
        Assert.Equal("deepseek-v4-pro", s.Model);
        s.Model = "deepseek-flash";
        Assert.Equal("deepseek-flash", s.Model);
    }

    [Fact]
    public void Model_RejectsUnknown_AndFallsBackOnStoredGarbage()
    {
        var (s, store) = Make();
        Assert.Throws<ArgumentException>(() => s.Model = "gpt-evil");
        store.Set("db1", "llm.deepseek.model", Encoding.UTF8.GetBytes("gpt-evil"));
        Assert.Equal("deepseek-v4-pro", s.Model);
    }

    [Fact]
    public void StoreUnavailable_Propagates()
    {
        var s = new LlmSettings(new ThrowingStore(), "db1");
        Assert.Throws<SecretStoreUnavailableException>(() => s.IsConfigured);
        Assert.Throws<SecretStoreUnavailableException>(() => s.SetKey("sk-abcdefgh1234"));
    }

    private sealed class ThrowingStore : ISecretStore
    {
        public byte[]? Get(string databaseId, string keyName) => throw new SecretStoreUnavailableException("x");
        public void Set(string databaseId, string keyName, byte[] secret) => throw new SecretStoreUnavailableException("x");
        public void Delete(string databaseId, string keyName) => throw new SecretStoreUnavailableException("x");
    }
}
