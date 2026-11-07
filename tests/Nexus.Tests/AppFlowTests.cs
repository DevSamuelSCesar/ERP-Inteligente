using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Nexus.Tests.Core;
using Avalonia.Threading;
using erp_inteligente;
using erp_inteligente.ViewModels;
using erp_inteligente.Views;

namespace Nexus.Tests;

/// <summary>Login → shell → sign-out / idle expiry → login, driven by the real AppController.</summary>
public class AppFlowTests
{
    private sealed class FakeDesktop : IWindowHost
    {
        public int ShutdownCalls { get; private set; }
        public Window? MainWindow { get; set; }
        public void Shutdown() => ShutdownCalls++;
    }

    private static async Task LogInAsync(FakeDesktop desktop, string username, string password)
    {
        var login = Assert.IsType<LoginWindow>(desktop.MainWindow);
        var viewModel = Assert.IsType<LoginViewModel>(login.DataContext);
        viewModel.Username = username;
        viewModel.Password = password;
        await viewModel.LoginCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task Login_opens_the_shell_and_sign_out_returns_to_login()
    {
        using var app = new TestApp();
        var desktop = new FakeDesktop();
        new AppController(desktop, app.Host).Start();

        await LogInAsync(desktop, "sup.maria", TestApp.DemoPassword);

        var main = Assert.IsType<MainWindow>(desktop.MainWindow);
        var shell = Assert.IsType<ShellViewModel>(main.DataContext);
        Assert.True(app.Host.Session.IsActive);
        Assert.Equal("sup.maria", app.Host.Session.Current.Username);

        shell.SignOutCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.IsType<LoginWindow>(desktop.MainWindow);
        Assert.False(app.Host.Session.IsActive);
        Assert.Equal(0, desktop.ShutdownCalls);
    }

    [AvaloniaFact]
    public async Task Wrong_password_keeps_the_login_with_the_uniform_message()
    {
        using var app = new TestApp();
        var desktop = new FakeDesktop();
        new AppController(desktop, app.Host).Start();

        await LogInAsync(desktop, "sup.maria", "senha-errada");

        var login = Assert.IsType<LoginWindow>(desktop.MainWindow);
        var viewModel = Assert.IsType<LoginViewModel>(login.DataContext);
        Assert.Equal(LoginViewModel.InvalidCredentials, viewModel.ErrorText);
        Assert.Equal("", viewModel.Password);
        Assert.False(app.Host.Session.IsActive);
    }

    [AvaloniaFact]
    public async Task Idle_session_expires_back_to_login_with_notice()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 11, 7, 12, 0, 0, TimeSpan.Zero));
        using var app = new TestApp(time);
        var desktop = new FakeDesktop();
        new AppController(desktop, app.Host, TimeSpan.FromMilliseconds(10)).Start();
        await LogInAsync(desktop, "op.joao", TestApp.DemoPassword);
        Assert.IsType<MainWindow>(desktop.MainWindow);

        time.Advance(TimeSpan.FromMinutes(16));
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (desktop.MainWindow is MainWindow && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(15);
        }

        var login = Assert.IsType<LoginWindow>(desktop.MainWindow);
        Assert.Equal(AppController.ExpiredNotice, Assert.IsType<LoginViewModel>(login.DataContext).Notice);
        Assert.False(app.Host.Session.IsActive);
    }

    [AvaloniaFact]
    public async Task Closing_the_main_window_signs_out_and_quits()
    {
        using var app = new TestApp();
        var desktop = new FakeDesktop();
        new AppController(desktop, app.Host).Start();
        await LogInAsync(desktop, "sup.maria", TestApp.DemoPassword);

        desktop.MainWindow!.Close();
        Dispatcher.UIThread.RunJobs();

        Assert.False(app.Host.Session.IsActive);
        Assert.Equal(1, desktop.ShutdownCalls);
    }

    [Fact]
    public void Database_path_comes_from_the_db_argument_or_the_user_data_folder()
    {
        Assert.Equal(Path.GetFullPath("x/nexus.db"), App.ResolveDatabasePath(["--db", "x/nexus.db"]));
        Assert.EndsWith(Path.Combine("Nexus", "nexus.db"), App.ResolveDatabasePath([]));
    }
}
