using System;
using System.IO;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace erp_inteligente.Core.Security;

/// <summary>
/// Windows key store: each secret is protected with DPAPI (current user scope, per-item entropy) and kept
/// in <c>%LOCALAPPDATA%\Nexus\keys</c>.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiSecretStore : ISecretStore
{
    private readonly string _directory;

    public DpapiSecretStore() : this(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nexus", "keys")) { }

    internal DpapiSecretStore(string directory) => _directory = directory;

    public byte[]? Get(string databaseId, string keyName)
    {
        var path = PathFor(databaseId, keyName);
        if (!File.Exists(path)) return null;
        try
        {
            return ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy(databaseId, keyName),
                DataProtectionScope.CurrentUser);
        }
        catch (Exception e) when (e is CryptographicException or IOException or UnauthorizedAccessException)
        {
            throw new SecretStoreUnavailableException("The Windows key store could not be read.", e);
        }
    }

    public void Set(string databaseId, string keyName, byte[] secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        try
        {
            Directory.CreateDirectory(_directory);
            var blob = ProtectedData.Protect(secret, Entropy(databaseId, keyName), DataProtectionScope.CurrentUser);
            var path = PathFor(databaseId, keyName);
            var tmp = path + ".tmp";
            File.WriteAllBytes(tmp, blob);
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception e) when (e is CryptographicException or IOException or UnauthorizedAccessException)
        {
            throw new SecretStoreUnavailableException("The Windows key store could not be written.", e);
        }
    }

    public void Delete(string databaseId, string keyName)
    {
        try { File.Delete(PathFor(databaseId, keyName)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new SecretStoreUnavailableException("The Windows key store could not be modified.", e);
        }
    }

    private static byte[] Entropy(string databaseId, string keyName) =>
        Encoding.UTF8.GetBytes(databaseId + "\n" + keyName);

    private string PathFor(string databaseId, string keyName) =>
        Path.Combine(_directory, Convert.ToHexStringLower(SHA256.HashData(Entropy(databaseId, keyName))) + ".bin");
}
