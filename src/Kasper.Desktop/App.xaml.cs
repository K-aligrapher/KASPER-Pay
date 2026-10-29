using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Windows;
using Kasper.Api;
using Kasper.Application;
using Kasper.Application.Abstractions;
using Kasper.Application.Contracts;
using Kasper.Infrastructure.Security;
using Microsoft.Extensions.DependencyInjection;

namespace Kasper.Desktop;

public partial class App : System.Windows.Application
{
    private const string MutexName = @"Local\KASPER.Desktop.SingleInstance";

    private Mutex? _singleInstance;
    private KasperApiHost? _host;
    private TrayIcon? _tray;
    private GlobalHotkey? _hotkey;
    private MainWindow? _window;
    private readonly SessionStore _sessions = new();

    internal bool IsQuitting { get; private set; }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var background = e.Args.Contains("--background", StringComparer.OrdinalIgnoreCase);

        _singleInstance = new Mutex(initiallyOwned: true, MutexName, out var isFirst);
        if (!isFirst)
        {
            // Already running: bring the existing window forward instead of starting twice.
            if (!background) await AskRunningInstanceToShowAsync();
            Shutdown();
            return;
        }

        try
        {
            var shell = new DesktopShell(this);
            var webRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
            _host = KasperApiHost.Create(webRoot, services => services.AddSingleton<IDesktopShell>(shell));
            await _host.StartAsync();
            _sessions.Write(new SessionInfo(_host.Port, _host.Token, Environment.ProcessId, DateTime.UtcNow));

            _tray = new TrayIcon(ShowMainWindow, Quit);
            _hotkey = new GlobalHotkey(ToggleFromHotkey);
            if (!_hotkey.Register())
                _tray.Notify("KASPER", "Ctrl+Space is used by another app, so the hotkey is off. Use the tray icon instead.");

            if (!background) ShowMainWindow();
            else _tray.Notify("KASPER is running", "Press Ctrl+Space or click the bunny in the tray.");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"KASPER could not start:\n\n{ex.Message}", "KASPER", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    internal void ShowMainWindow()
    {
        if (_host is null) return;
        _window ??= new MainWindow(_host.BaseUrl, _host.Token, Path.Combine(AppContext.BaseDirectory, "wwwroot"));
        _window.ShowAndFocus();
    }

    internal void Notify(string title, string message) => _tray?.Notify(title, message);

    private void ToggleFromHotkey()
    {
        if (_window is { IsVisible: true, IsActive: true }) _window.Hide();
        else
        {
            ShowMainWindow();
            _window?.FocusCommandBar();
        }
    }

    private void Quit()
    {
        IsQuitting = true;
        _window?.Close();
        Shutdown();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        _hotkey?.Dispose();
        _tray?.Dispose();
        _sessions.Delete(Environment.ProcessId);
        if (_host is not null) await _host.DisposeAsync();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    private async Task AskRunningInstanceToShowAsync()
    {
        if (_sessions.Read() is not { } session) return;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{session.BaseUrl}/api/app/show");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.Token);
            await http.SendAsync(req);
        }
        catch (HttpRequestException ex)
        {
            Debug.WriteLine($"Could not reach running instance: {ex.Message}");
        }
        catch (TaskCanceledException) { }
    }

    /// <summary>Lets the API (and the CLI through it) reach the UI thread.</summary>
    private sealed class DesktopShell(App app) : IDesktopShell
    {
        public void ShowMainWindow() => app.Dispatcher.BeginInvoke(app.ShowMainWindow);

        public void Notify(string title, string message) => app.Dispatcher.BeginInvoke(() => app.Notify(title, message));
    }
}
