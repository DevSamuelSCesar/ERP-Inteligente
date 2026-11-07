using System;
using erp_inteligente.Models;

namespace erp_inteligente.Core.Auth;

/// <summary>Outcome of a login attempt. <see cref="Failed"/> is identical whether or not the user exists.</summary>
public abstract record LoginResult
{
    private LoginResult() { }

    /// <summary>The user is authenticated.</summary>
    public sealed record Succeeded(UserSession Session) : LoginResult;

    /// <summary>Wrong user or password, or an account that cannot log in.</summary>
    public sealed record Failed : LoginResult
    {
        public const string DefaultMessage = "Usuário ou senha inválidos";

        /// <summary>Shared instance.</summary>
        public static Failed Instance { get; } = new();

        public string Message => DefaultMessage;
    }

    /// <summary>Login is refused until <see cref="Until"/> (UTC).</summary>
    public sealed record Locked(DateTimeOffset Until) : LoginResult;
}
