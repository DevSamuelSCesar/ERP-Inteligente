using System;
using erp_inteligente.Models;
using erp_inteligente.Services;

namespace erp_inteligente.Core.Auth;

/// <summary>
/// Holds the signed-in session and enforces the idle timeout (references/ARQUITETURA.md, section 12.5). The UI calls
/// <see cref="Touch"/> on keyboard or mouse input and <see cref="CheckExpiry"/> from a timer. Thread-safe.
/// </summary>
public sealed class SessionService(AuthService auth, TimeProvider time, AuthOptions options) : ISessionService
{
    private readonly object _gate = new();
    private UserSession? _session;
    private DateTimeOffset _lastActivity;

    /// <summary>Raised after the session ended because of inactivity.</summary>
    public event EventHandler? Expired;

    /// <summary>Raised after the user signed out.</summary>
    public event EventHandler? SignedOut;

    /// <summary>True while a session is active.</summary>
    public bool IsActive
    {
        get { lock (_gate) return _session is not null; }
    }

    /// <summary>The active session. Throws <see cref="InvalidOperationException"/> when nobody is signed in.</summary>
    public UserSession Current
    {
        get
        {
            lock (_gate) return _session ?? throw new InvalidOperationException("Nenhuma sessão ativa.");
        }
    }

    /// <summary>Begins a session and starts its idle clock.</summary>
    public void Start(UserSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        lock (_gate)
        {
            _session = session;
            _lastActivity = time.GetUtcNow();
        }
    }

    /// <summary>Records user activity; ignored when there is no session.</summary>
    public void Touch()
    {
        lock (_gate)
        {
            if (_session is not null) _lastActivity = time.GetUtcNow();
        }
    }

    /// <summary>Ends the session if it has been idle for the timeout. Returns true when it expired.</summary>
    public bool CheckExpiry()
    {
        UserSession? expired;
        lock (_gate)
        {
            if (_session is null || time.GetUtcNow() - _lastActivity < options.IdleTimeout) return false;
            expired = _session;
            _session = null;
        }

        auth.RecordSessionExpired(expired);
        Expired?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>Audits the sign-out, ends the session and raises <see cref="SignedOut"/>. No-op without a session.</summary>
    public void SignOut()
    {
        UserSession? ended;
        lock (_gate)
        {
            ended = _session;
            _session = null;
        }
        if (ended is null) return;

        auth.Logout(ended);
        SignedOut?.Invoke(this, EventArgs.Empty);
    }
}
