using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using VivintDesktop.Api;

namespace VivintDesktop.Views;

/// <summary>
/// Hosts Vivint's real OAuth login page in an embedded Edge WebView2 and
/// captures the <c>vivint://app/oauth_redirect?code=…</c> redirect when sign-in
/// (including any email/2FA challenge) completes.
/// </summary>
public partial class LoginWindow : Window
{
    private readonly VivintClient _client;
    private readonly bool _clearBrowser;
    private readonly string? _initialMessage;
    private bool _webReady;
    private bool _completing;

    public LoginWindow(VivintClient client, bool clearBrowser = false, string? message = null)
    {
        InitializeComponent();
        Theme.ApplyTitleBar(this);
        _client = client;
        _clearBrowser = clearBrowser;
        _initialMessage = message;
        RememberBox.IsChecked = client.RememberMe;
        BuildIdleHome();
        Loaded += async (_, _) => await StartAsync();
    }

    private async Task InitWebAsync()
    {
        if (_webReady) return;
        var userData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VivintDesktop", "WebView2");
        Directory.CreateDirectory(userData);

        var env = await CoreWebView2Environment.CreateAsync(null, userData);
        await Web.EnsureCoreWebView2Async(env);
        var core = Web.CoreWebView2;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;

        // After an explicit sign-out, forget the IdP session so the login form appears.
        if (_clearBrowser) core.CookieManager.DeleteAllCookies();

        core.NavigationStarting += OnNavigationStarting;
        core.NavigationCompleted += (_, e) =>
        {
            Placeholder.Visibility = Visibility.Collapsed;
            if (!e.IsSuccess && !_completing &&
                e.WebErrorStatus is not CoreWebView2WebErrorStatus.OperationCanceled)
                ShowError($"Could not load the sign-in page ({e.WebErrorStatus}). Check your internet connection.");
        };
        // Keep pop-ups (help links, etc.) inside this window.
        core.NewWindowRequested += (_, e) => { e.Handled = true; core.Navigate(e.Uri); };
        _webReady = true;
    }

    private async Task StartAsync()
    {
        try
        {
            SetStatus(_initialMessage ?? "Loading Vivint sign-in…");
            await InitWebAsync();
            var authUrl = await _client.BuildAuthorizeUrlAsync();
            if (_initialMessage is null)
                SetStatus("Sign in with your Vivint account, or try demo mode to look around first.");
            Web.CoreWebView2.Navigate(authUrl);
        }
        catch (WebView2RuntimeNotFoundException)
        {
            ShowError("The Microsoft Edge WebView2 Runtime is required. Install it from " +
                      "https://developer.microsoft.com/microsoft-edge/webview2/ and try again.");
        }
        catch (Exception ex)
        {
            ShowError("Could not start sign-in: " + ex.Message);
        }
    }

    private async void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (!_client.IsRedirect(e.Uri)) return;

        // Intercept the custom-scheme redirect; the browser can't load it.
        e.Cancel = true;
        if (_completing) return;
        _completing = true;
        SetStatus("Completing sign-in…");
        try
        {
            _client.RememberMe = RememberBox.IsChecked == true;
            await _client.CompleteLoginFromRedirectAsync(e.Uri);
            DialogResult = true;
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
            _completing = false;
        }
    }

    private async void Restart_Click(object sender, RoutedEventArgs e)
    {
        RestartBtn.Visibility = Visibility.Collapsed;
        _completing = false;
        await StartAsync();
    }

    /// <summary>An idle camera wall and empty timeline, so this screen reads as the app waiting to connect.</summary>
    private void BuildIdleHome()
    {
        var noon = DateTime.Today.AddHours(14);
        foreach (var name in new[] { "Front door", "Driveway", "Backyard", "Side yard" })
        {
            var tile = new Grid();
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.StreamSource = new MemoryStream(DemoImages.CameraJpeg(name, noon, osd: false));
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();
                tile.Children.Add(new Image { Source = bmp, Stretch = Stretch.UniformToFill, Opacity = 0.16 });
            }
            catch { /* the picture is decoration only */ }
            var label = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            label.Children.Add(new TextBlock
            {
                Text = "\uE714", FontFamily = (FontFamily)FindResource("Icons"), FontSize = 22,
                Foreground = new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)), HorizontalAlignment = HorizontalAlignment.Center,
            });
            label.Children.Add(new TextBlock
            {
                Text = "Waiting for sign-in", Margin = new Thickness(0, 8, 0, 0), FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)), HorizontalAlignment = HorizontalAlignment.Center,
            });
            tile.Children.Add(label);
            tile.Children.Add(new TextBlock
            {
                Text = name, Margin = new Thickness(10, 8, 0, 0), FontSize = 12, FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF)),
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            });
            GhostWall.Children.Add(new Border { Margin = new Thickness(0, 0, 10, 10), CornerRadius = new CornerRadius(8), ClipToBounds = true, Background = Brushes.Black, Child = tile });
        }

        foreach (var row in new[] { "Security", "Locks", "Camera events" })
        {
            var dp = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
            var l = new TextBlock { Text = row, Width = 130, FontSize = 12 };
            l.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            DockPanel.SetDock(l, Dock.Left);
            dp.Children.Add(l);
            var bar = new Border { Height = 10, CornerRadius = new CornerRadius(3), VerticalAlignment = VerticalAlignment.Center };
            bar.SetResourceReference(Border.BackgroundProperty, "Sunken");
            dp.Children.Add(bar);
            GhostTimeline.Items.Add(dp);
        }
    }

    private void Demo_Click(object sender, RoutedEventArgs e)
    {
        if (_completing) return;
        _client.StartDemo();
        DialogResult = true;
    }

    private void SetStatus(string msg)
    {
        StatusText.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        StatusText.Text = msg;
    }

    private void ShowError(string msg)
    {
        StatusText.SetResourceReference(TextBlock.ForegroundProperty, "Danger");
        StatusText.Text = msg;
        RestartBtn.Visibility = Visibility.Visible;
    }
}
