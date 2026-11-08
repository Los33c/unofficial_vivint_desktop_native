using System.Windows;
using System.Windows.Threading;
using VivintDesktop.Api;
using VivintDesktop.Views;

namespace VivintDesktop;

public partial class App : Application
{
    public VivintClient Client { get; } = new();

    private MainWindow? _main;
    private bool _swapping;     // closing the main window to re-authenticate, not quitting

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Theme.Init();
        DispatcherUnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, args) => args.SetObserved();

        Client.SessionExpired += () => Dispatcher.BeginInvoke(() => _ = ReauthenticateAsync(clearBrowser: false,
            "Your Vivint session expired. Please sign in again."));

        // --demo: open straight into demo mode. --sign-in: show the sign-in window even with a saved session.
        var args = e.Args.Select(x => x.ToLowerInvariant()).ToArray();
        if (args.Contains("--demo")) Client.StartDemo();
        await StartSessionAsync(tryRestore: !args.Contains("--sign-in"), clearBrowser: false, message: null);
    }

    /// <summary>Restore a saved session or show the sign-in window, then open the main window.</summary>
    private async Task StartSessionAsync(bool tryRestore, bool clearBrowser, string? message)
    {
        var ok = Client.DemoMode;
        if (!ok && tryRestore)
        {
            try { ok = await Client.TryRestoreSessionAsync(); }
            catch { ok = false; }
        }

        if (!ok)
        {
            var login = new LoginWindow(Client, clearBrowser, message);
            ok = login.ShowDialog() == true;
        }

        if (!ok)
        {
            Shutdown();
            return;
        }

        _main = new MainWindow(Client);
        MainWindow = _main;
        var wasDemo = Client.DemoMode;
        // Leaving the demo shouldn't wipe a browser sign-in the user may want to keep.
        _main.SignedOut += () => _ = ReauthenticateAsync(clearBrowser: !wasDemo, null);
        _main.Closed += (_, _) => { if (!_swapping) Shutdown(); };
        _main.Show();
    }

    private async Task ReauthenticateAsync(bool clearBrowser, string? message)
    {
        if (_swapping) return;
        _swapping = true;
        try
        {
            _main?.Close();
            _main = null;
            await StartSessionAsync(tryRestore: false, clearBrowser, message);
        }
        finally { _swapping = false; }
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        if (e.Exception is AuthException) return; // handled by SessionExpired
        try { Dialog.Info(MainWindow, "Something went wrong", e.Exception.Message, warning: true); }
        catch { MessageBox.Show(e.Exception.Message, "Vivint Desktop — error", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { Video.VideoEngine.Shutdown(); } catch { }
        Client.Dispose();
        base.OnExit(e);
    }
}
