using erp_inteligente;
using erp_inteligente.Core.Auth;
using erp_inteligente.Core.Security;
using erp_inteligente.Models;
using erp_inteligente.ViewModels;
using Nexus.Tests.Core;

namespace Nexus.Tests;

/// <summary>
/// The real core over a temporary encrypted database: AppHost with the seeded users. The seed hashes use the
/// production Argon2id cost, so logins here take a fraction of a second each.
/// </summary>
public sealed class TestApp : IDisposable
{
    public const string DemoPassword = "demo";

    private readonly TestDatabase _db = new();

    public TestApp(TimeProvider? time = null)
    {
        Time = time ?? new ManualTimeProvider(new DateTimeOffset(2026, 11, 7, 12, 0, 0, TimeSpan.Zero));
        Host = new AppHost(_db.Database, Time, new Argon2Parameters(1024, 1, 1), AuthOptions.Default);
    }

    public TimeProvider Time { get; }
    public AppHost Host { get; }

    public async Task<UserSession> LoginAsync(string username)
    {
        var result = await Host.Auth.LoginAsync(username, DemoPassword);
        var session = Assert.IsType<LoginResult.Succeeded>(result).Session;
        Host.Session.Start(session);
        return session;
    }

    public async Task<ShellViewModel> ShellForAsync(string username)
    {
        await LoginAsync(username);
        return Host.CreateShell(new FakeDialogService());
    }

    // AppHost.Dispose would dispose the same NexusDatabase that TestDatabase owns; TestDatabase cleans up.
    public void Dispose() => _db.Dispose();
}
