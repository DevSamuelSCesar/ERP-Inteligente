using System;
using System.Security.Cryptography;

namespace erp_inteligente.Core.Security;

/// <summary>
/// The two random 32-byte keys of a database (references/ARQUITETURA.md, section 12.3): one opens the
/// SQLCipher file, the other encrypts the <c>*_enc</c> columns. Both live only in the OS key store and
/// in memory; <see cref="Dispose"/> zeroes them.
/// </summary>
public sealed class DatabaseKeys : IDisposable
{
    public const int KeyLength = 32;

    public DatabaseKeys(byte[] sqlCipherKey, byte[] columnKey)
    {
        if (sqlCipherKey.Length != KeyLength || columnKey.Length != KeyLength)
            throw new ArgumentException($"Keys must have {KeyLength} bytes.");
        SqlCipherKey = sqlCipherKey;
        ColumnKey = columnKey;
    }

    public byte[] SqlCipherKey { get; }
    public byte[] ColumnKey { get; }

    public static DatabaseKeys Generate() =>
        new(RandomNumberGenerator.GetBytes(KeyLength), RandomNumberGenerator.GetBytes(KeyLength));

    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(SqlCipherKey);
        CryptographicOperations.ZeroMemory(ColumnKey);
    }
}
