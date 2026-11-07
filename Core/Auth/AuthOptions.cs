using System;

namespace erp_inteligente.Core.Auth;

/// <summary>Lockout and session-timeout settings (references/ARQUITETURA.md, section 12.5).</summary>
public sealed record AuthOptions(int MaxFailedAttempts, TimeSpan LockoutDuration, TimeSpan IdleTimeout)
{
    /// <summary>5 failures lock the user for 15 minutes; a session expires after 15 idle minutes.</summary>
    public static AuthOptions Default { get; } = new(5, TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(15));
}
