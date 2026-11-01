using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media.Imaging;
using Kasper.Application;
using Microsoft.Web.WebView2.Core;

namespace Kasper.Desktop;

public partial class MainWindow : Window
{
    private string _baseUrl = "http://127.0.0.1:0";
    private readonly string _webRoot;
    private readonly Task _init;

    /// <summary>Shows instantly with a splash; the browser engine starts while the local server boots.</summary>
    public MainWindow(string webRoot)
    {
        _webRoot = webRoot;
        InitializeComponent();

        var icon = Path.Combine(AppContext.BaseDirectory, "kasper.ico");
        if (File.Exists(icon))
        {
            Icon = BitmapFrame.Create(new Uri(icon));
            var big = BitmapDecoder.Create(new Uri(icon), BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames.MaxBy(f => f.PixelWidth);
            SplashBunny.Source = big;
        }

        _init = InitializeWebViewAsync();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // Dark title bar in KASPER black (Windows 11; silently ignored on older builds).
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        int dark = 1, caption = 0x000D0D0D, text = 0x0000B8F5; // COLORREF is 0x00BBGGRR
        DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));    // DWMWA_USE_IMMERSIVE_DARK_MODE
        DwmSetWindowAttribute(hwnd, 35, ref caption, sizeof(int)); // DWMWA_CAPTION_COLOR
        DwmSetWindowAttribute(hwnd, 36, ref text, sizeof(int));    // DWMWA_TEXT_COLOR (gold)
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public void ShowAndFocus()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true;  // reliably bring to front from a hotkey/tray
        Topmost = false;
        Focus();
    }

    public async void FocusCommandBar()
    {
        await _loaded.Task;
        Web.Focus();
        await Web.ExecuteScriptAsync("location.hash = '#home'; window.kasper && window.kasper.focusCommand()");
    }

    /// <summary>Opens a view in the UI, e.g. "approvals" from a notification.</summary>
    public async void Navigate(string route)
    {
        await _loaded.Task;
        await Web.ExecuteScriptAsync($"location.hash = {JsonSerializer.Serialize("#" + route)}");
    }

    private readonly TaskCompletionSource _loaded = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private async Task InitializeWebViewAsync()
    {
        var env = await CoreWebView2Environment.CreateAsync(userDataFolder: KasperPaths.WebViewData);
        await Web.EnsureCoreWebView2Async(env);
        StartupTimer.Mark("webview ready");
        var core = Web.CoreWebView2;

        core.Settings.AreDevToolsEnabled = Debugger.IsAttached;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = Debugger.IsAttached;

        // Stay on the local app; open any other link in the default browser.
        core.NavigationCompleted += (_, _) => _loaded.TrySetResult();
        core.NavigationStarting += (_, e) =>
        {
            if (e.Uri.StartsWith(_baseUrl, StringComparison.OrdinalIgnoreCase) || e.Uri.StartsWith("data:", StringComparison.Ordinal)) return;
            e.Cancel = true;
            OpenExternal(e.Uri);
        };
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            OpenExternal(e.Uri);
        };

    }

    /// <summary>Called once the local API is up: hand over the token and load the UI.</summary>
    public async Task ConnectAsync(string baseUrl, string token)
    {
        await _init;
        _baseUrl = baseUrl;
        var core = Web.CoreWebView2;
        // The token is only given to our own origin.
        await core.AddScriptToExecuteOnDocumentCreatedAsync(
            $"if (location.origin === {JsonSerializer.Serialize(baseUrl)}) window.__KASPER__ = Object.freeze({{ token: {JsonSerializer.Serialize(token)} }});");
        core.NavigationCompleted += (_, _) =>
        {
            Web.Visibility = Visibility.Visible;
            StartupTimer.Mark("ui loaded");
            Splash.Visibility = Visibility.Collapsed;
        };
        if (File.Exists(Path.Combine(_webRoot, "index.html"))) core.Navigate(baseUrl + "/");
        else core.NavigateToString(MissingUiPage);
    }

    private static void OpenExternal(string uri)
    {
        if (Uri.TryCreate(uri, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp))
            Process.Start(new ProcessStartInfo(u.ToString()) { UseShellExecute = true });
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Closing the window keeps KASPER running in the tray.
        if (!((App)System.Windows.Application.Current).IsQuitting)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnClosing(e);
    }

    private const string MissingUiPage = """
        <html><body style="background:#0D0D0D;color:#F2F2F2;font-family:Segoe UI;padding:40px">
        <h2 style="color:#F5B800">KASPER UI not built</h2>
        <p>Run <code>npm install</code> and <code>npm run build</code> in the <code>ui</code> folder, then rebuild.</p>
        </body></html>
        """;
}
