using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;

namespace erp_inteligente.Core.Security;

/// <summary>Argon2id cost parameters.</summary>
public sealed record Argon2Parameters(int MemoryKiB, int Iterations, int Parallelism)
{
    public const int MaxMemoryKiB = 256 * 1024; // 256 MiB: caps the cost a tampered hash can impose
    public const int MaxIterations = 10;
    public const int MaxParallelism = 16;

    public static Argon2Parameters Default { get; } = new(65536, 3, 4);

    /// <summary>True when the values are inside the accepted bounds.</summary>
    public bool IsValid =>
        Parallelism is >= 1 and <= MaxParallelism
        && Iterations is >= 1 and <= MaxIterations
        && MemoryKiB >= 8 * Parallelism && MemoryKiB <= MaxMemoryKiB;
}

/// <summary>Argon2id password hashing with PHC-format strings.</summary>
public sealed class PasswordHasher
{
    public const int MaxPasswordLength = 1024;
    private const int SaltSize = 16;
    private const int HashSize = 32;

    private readonly Argon2Parameters _parameters;
    private readonly string _dummyHash;

    public PasswordHasher(Argon2Parameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        if (!parameters.IsValid) throw new ArgumentOutOfRangeException(nameof(parameters));
        _parameters = parameters;
        _dummyHash = Hash(Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)));
    }

    public string Hash(string password)
    {
        ArgumentNullException.ThrowIfNull(password);
        if (password.Length > MaxPasswordLength)
            throw new ArgumentException("Password is too long.", nameof(password));
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Compute(password, salt, _parameters, HashSize);
        try
        {
            return $"$argon2id$v=19$m={_parameters.MemoryKiB},t={_parameters.Iterations},p={_parameters.Parallelism}"
                 + $"${Convert.ToBase64String(salt).TrimEnd('=')}${Convert.ToBase64String(hash).TrimEnd('=')}";
        }
        finally
        {
            CryptographicOperations.ZeroMemory(hash);
        }
    }

    /// <summary>Verifies a password; returns false (never throws) for malformed or out-of-bound hashes.</summary>
    public bool Verify(string password, string encoded)
    {
        if (password is null || encoded is null || password.Length > MaxPasswordLength) return false;
        if (!TryParse(encoded, out var p, out var salt, out var expected)) return false;
        var actual = Compute(password, salt, p, expected.Length);
        try { return CryptographicOperations.FixedTimeEquals(actual, expected); }
        finally { CryptographicOperations.ZeroMemory(actual); }
    }

    /// <summary>Spends the same work as <see cref="Verify"/> so unknown users are not distinguishable by timing.</summary>
    public void VerifyDummy(string password) => _ = Verify(password, _dummyHash);

    private static byte[] Compute(string password, byte[] salt, Argon2Parameters p, int length)
    {
        var pw = Encoding.UTF8.GetBytes(password);
        try
        {
            using var argon = new Argon2id(pw)
            {
                Salt = salt,
                MemorySize = p.MemoryKiB,
                Iterations = p.Iterations,
                DegreeOfParallelism = p.Parallelism,
            };
            return argon.GetBytes(length);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pw);
        }
    }

    private static bool TryParse(string s, out Argon2Parameters p, out byte[] salt, out byte[] hash)
    {
        p = Argon2Parameters.Default;
        salt = hash = [];
        var parts = s.Split('$');
        // "", "argon2id", "v=19", "m=..,t=..,p=..", salt, hash
        if (parts.Length != 6 || parts[0].Length != 0 || parts[1] != "argon2id" || parts[2] != "v=19") return false;
        var kv = parts[3].Split(',');
        if (kv.Length != 3 || !Num(kv[0], "m=", out var m) || !Num(kv[1], "t=", out var t) || !Num(kv[2], "p=", out var par))
            return false;
        p = new Argon2Parameters(m, t, par);
        if (!p.IsValid) return false;
        if (!TryB64(parts[4], out salt) || salt.Length < 8 || salt.Length > 64) return false;
        if (!TryB64(parts[5], out hash) || hash.Length < 16 || hash.Length > 128) return false;
        return true;
    }

    private static bool Num(string s, string prefix, out int value)
    {
        value = 0;
        return s.StartsWith(prefix, StringComparison.Ordinal)
            && s.Length <= prefix.Length + 9
            && int.TryParse(s.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryB64(string s, out byte[] bytes)
    {
        bytes = [];
        if (s.Length == 0 || s.Contains('=')) return false;
        var padded = s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=');
        var buf = new byte[padded.Length / 4 * 3];
        if (!Convert.TryFromBase64String(padded, buf, out var n)) return false;
        bytes = buf.AsSpan(0, n).ToArray();
        return true;
    }
}
