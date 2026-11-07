using erp_inteligente.Core.Auth;
using Nexus.Tests.Core.Policy;

namespace Nexus.Tests.Core.Auth;

public class SessionServiceTests : IDisposable
{
    private readonly PolicyTestFixture _f = new();
    private readonly SessionService _sessions;

    public SessionServiceTests()
    {
        var auth = new AuthService(_f.Db.Database, new erp_inteligente.Core.Security.PasswordHasher(
            new erp_inteligente.Core.Security.Argon2Parameters(1024, 1, 1)), _f.Policy, _f.Audit, _f.Time, AuthOptions.Default);
        _sessions = new SessionService(auth, _f.Time, AuthOptions.Default);
    }

    public void Dispose() => _f.Dispose();

    [Fact]
    public void Current_throws_without_session()
    {
        Assert.False(_sessions.IsActive);
        Assert.Throws<InvalidOperationException>(() => _sessions.Current);
        Assert.False(_sessions.CheckExpiry());
    }

    [Fact]
    public void Touch_postpones_expiry()
    {
        var s = _f.SessionFor("sup.maria");
        _sessions.Start(s);
        Assert.Same(s, _sessions.Current);

        _f.Time.Advance(TimeSpan.FromMinutes(14));
        _sessions.Touch();
        _f.Time.Advance(TimeSpan.FromMinutes(14));
        Assert.False(_sessions.CheckExpiry());
        Assert.True(_sessions.IsActive);
    }

    [Fact]
    public void Idle_timeout_expires_raises_event_and_audits()
    {
        var raised = 0;
        _sessions.Expired += (_, _) => raised++;
        _sessions.Start(_f.SessionFor("sup.maria"));

        _f.Time.Advance(TimeSpan.FromMinutes(15));
        Assert.True(_sessions.CheckExpiry());
        Assert.Equal(1, raised);
        Assert.False(_sessions.IsActive);
        Assert.Equal(1, _f.Scalar("SELECT count(*) FROM audit_log WHERE action = 'auth.session_expired'"));
        Assert.False(_sessions.CheckExpiry());
        Assert.Equal(1, raised);
    }

    [Fact]
    public void SignOut_audits_and_raises_event()
    {
        var raised = 0;
        _sessions.SignedOut += (_, _) => raised++;
        _sessions.Start(_f.SessionFor("sup.maria"));

        _sessions.SignOut();
        Assert.Equal(1, raised);
        Assert.False(_sessions.IsActive);
        Assert.Equal(1, _f.Scalar("SELECT count(*) FROM audit_log WHERE action = 'auth.logout'"));
        Assert.Throws<InvalidOperationException>(() => _sessions.Current);
    }
}
