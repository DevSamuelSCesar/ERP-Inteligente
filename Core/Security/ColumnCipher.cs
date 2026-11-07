using System;
using System.Security.Cryptography;
using System.Text;

namespace erp_inteligente.Core.Security;

/// <summary>
/// AES-256-GCM for the <c>*_enc</c> columns. Layout: 0x01 | 12-byte nonce | ciphertext | 16-byte tag.
/// The AAD binds each value to its cell (<c>table.column#id</c>), so moving a value to another row fails.
/// </summary>
public sealed class ColumnCipher : IDisposable
{
    private const byte Version = 1;
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly AesGcm _aes;

    public ColumnCipher(byte[] columnKey)
    {
        ArgumentNullException.ThrowIfNull(columnKey);
        if (columnKey.Length != DatabaseKeys.KeyLength)
            throw new ArgumentException($"The column key must have {DatabaseKeys.KeyLength} bytes.", nameof(columnKey));
        _aes = new AesGcm(columnKey, TagSize);
    }

    public byte[] Encrypt(string plaintext, string table, string column, long id)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        var aad = Aad(table, column, id);
        var plain = Encoding.UTF8.GetBytes(plaintext);
        try
        {
            var result = new byte[1 + NonceSize + plain.Length + TagSize];
            result[0] = Version;
            var nonce = result.AsSpan(1, NonceSize);
            RandomNumberGenerator.Fill(nonce);
            _aes.Encrypt(nonce, plain, result.AsSpan(1 + NonceSize, plain.Length),
                result.AsSpan(1 + NonceSize + plain.Length, TagSize), aad);
            return result;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    public string Decrypt(byte[] value, string table, string column, long id)
    {
        ArgumentNullException.ThrowIfNull(value);
        var aad = Aad(table, column, id);
        if (value.Length < 1 + NonceSize + TagSize || value[0] != Version)
            throw new CryptographicException("Invalid encrypted value.");
        var span = value.AsSpan();
        var ctLen = value.Length - 1 - NonceSize - TagSize;
        var plain = new byte[ctLen];
        try
        {
            _aes.Decrypt(span.Slice(1, NonceSize), span.Slice(1 + NonceSize, ctLen),
                span.Slice(1 + NonceSize + ctLen, TagSize), plain, aad);
            return new UTF8Encoding(false, true).GetString(plain);
        }
        catch (ArgumentException e) // invalid UTF-8
        {
            throw new CryptographicException("Invalid encrypted value.", e);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    public void Dispose() => _aes.Dispose();

    private static byte[] Aad(string table, string column, long id)
    {
        if (!IsIdentifier(table)) throw new ArgumentException("Invalid table name.", nameof(table));
        if (!IsIdentifier(column)) throw new ArgumentException("Invalid column name.", nameof(column));
        return Encoding.UTF8.GetBytes($"{table}.{column}#{id}");
    }

    private static bool IsIdentifier(string s)
    {
        if (string.IsNullOrEmpty(s) || s.Length > 128) return false;
        if (char.IsAsciiDigit(s[0])) return false;
        foreach (var c in s)
            if (!(char.IsAsciiLetterOrDigit(c) || c == '_')) return false;
        return true;
    }
}
