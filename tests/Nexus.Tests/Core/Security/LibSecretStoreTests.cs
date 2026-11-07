using System.Security.Cryptography;
using erp_inteligente.Core.Security;

namespace Nexus.Tests.Core.Security;

public class LibSecretStoreTests
{
    [Fact]
    public void RoundTrip_AgainstSecretService()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Skip("libsecret is Linux-only."); return; }

        var store = new LibSecretStore();
        var db = "/nexus-test/" + Convert.ToHexString(RandomNumberGenerator.GetBytes(8)) + ".db";
        var secret = RandomNumberGenerator.GetBytes(32);
        try
        {
            try { Assert.Null(store.Get(db, "sqlcipher")); }
            catch (SecretStoreUnavailableException e) { Assert.Skip("Secret Service unreachable: " + e.Message); return; }

            store.Set(db, "sqlcipher", secret);
            Assert.Equal(secret, store.Get(db, "sqlcipher"));
            Assert.Null(store.Get(db, "column"));

            var other = RandomNumberGenerator.GetBytes(32);
            store.Set(db, "sqlcipher", other); // overwrite
            Assert.Equal(other, store.Get(db, "sqlcipher"));

            store.Delete(db, "sqlcipher");
            Assert.Null(store.Get(db, "sqlcipher"));
            store.Delete(db, "sqlcipher"); // idempotent
        }
        finally
        {
            try { store.Delete(db, "sqlcipher"); store.Delete(db, "column"); }
            catch (SecretStoreUnavailableException) { }
        }
    }

    [Fact]
    public void ForCurrentPlatform_ReturnsStoreOnLinux()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Skip("Linux only."); return; }
        Assert.IsType<LibSecretStore>(SecretStores.ForCurrentPlatform());
    }
}
