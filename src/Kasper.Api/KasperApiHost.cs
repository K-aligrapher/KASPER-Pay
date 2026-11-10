using Microsoft.Extensions.Logging;
using System.Net;
using System.Reflection;
using Kasper.Api.Endpoints;
using Kasper.Api.Middleware;
using Kasper.Application;
using Kasper.Infrastructure;
using Kasper.Infrastructure.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace Kasper.Api;

/// <summary>
/// Hosts the local API in-process, bound to 127.0.0.1 on a random free port, with a
/// fresh bearer token per launch. Static UI files are served from <paramref name="webRoot"/>.
/// </summary>
public sealed class KasperApiHost : IAsyncDisposable
{
    private readonly WebApplication _app;

    private KasperApiHost(WebApplication app, string token)
    {
        _app = app;
        Token = token;
    }

    public string Token { get; }
    public long MigrateMs { get; private set; }
    public long KestrelMs { get; private set; }
    public int Port { get; private set; }
    public IServiceProvider Services => _app.Services;
    public string BaseUrl => $"http://127.0.0.1:{Port}";

    public static KasperApiHost Create(string webRoot, Action<IServiceCollection>? configureServices = null, int port = 0)
    {
        KasperPaths.EnsureCreated();
        var token = TokenGenerator.NewToken();
        var version = typeof(KasperApiHost).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion.Split('+')[0] ?? "0.0.0";

        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory,
            EnvironmentName = Environments.Production,
        });

        // Settings: built-in defaults < %LOCALAPPDATA%\KASPER\settings.json < KASPER__* env vars.
        builder.Configuration.Sources.Clear();
        builder.Configuration
            .AddJsonFile(KasperPaths.SettingsFile, optional: true, reloadOnChange: true)
            .AddUserSecrets(typeof(KasperApiHost).Assembly, optional: true, reloadOnChange: false) // PayPal:ClientId / PayPal:ClientSecret
            .AddEnvironmentVariables("KASPER__");

        builder.WebHost.ConfigureKestrel(k =>
        {
            k.Listen(IPAddress.Loopback, port);
            k.Limits.MaxRequestBodySize = 1 * 1024 * 1024;
        });

        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(new Kasper.Infrastructure.Observability.FileLoggerProvider());
        builder.Services.AddKasperInfrastructure(builder.Configuration);
        configureServices?.Invoke(builder.Services);

        var app = builder.Build();
        app.UseMiddleware<LocalOnlyAuthMiddleware>(token);

        if (Directory.Exists(webRoot))
        {
            var files = new PhysicalFileProvider(Path.GetFullPath(webRoot));
            app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
            app.UseStaticFiles(new StaticFileOptions { FileProvider = files });
        }

        app.MapCoreEndpoints(version);
        app.MapWorkflowEndpoints();
        app.MapDocumentEndpoints();
        app.MapConnectEndpoints();
        app.MapPaymentEndpoints();
        if (Directory.Exists(webRoot))
            app.MapFallbackToFile("index.html", new StaticFileOptions { FileProvider = new PhysicalFileProvider(Path.GetFullPath(webRoot)) });

        return new KasperApiHost(app, token);
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await _app.Services.MigrateDatabaseAsync(ct);
        MigrateMs = sw.ElapsedMilliseconds;
        await _app.StartAsync(ct);
        KestrelMs = sw.ElapsedMilliseconds - MigrateMs;
        var address = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        Port = new Uri(address).Port;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
