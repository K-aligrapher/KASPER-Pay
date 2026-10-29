using Kasper.Application;
using Kasper.Application.Abstractions;
using Kasper.Application.Options;
using Kasper.Infrastructure.Ollama;
using Kasper.Infrastructure.Observability;
using Kasper.Infrastructure.Persistence;
using Kasper.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Kasper.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddKasperInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<OllamaOptions>(config.GetSection(OllamaOptions.Section));

        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<ISessionStore>(_ => new SessionStore());

        services.AddDbContext<KasperDbContext>(o => o.UseSqlite($"Data Source={KasperPaths.Database}"));
        services.AddScoped<IAuditSink, DbAuditSink>();

        // Timeouts come from OllamaOptions per call, so HttpClient's own timeout is disabled.
        services.AddHttpClient<IChatModel, OllamaChatModel>(c => c.Timeout = Timeout.InfiniteTimeSpan);
        services.AddHttpClient<OllamaHealthCheck>();
        services.AddTransient<IIntegrationHealthCheck>(sp => sp.GetRequiredService<OllamaHealthCheck>());

        return services;
    }

    public static async Task MigrateDatabaseAsync(this IServiceProvider services, CancellationToken ct = default)
    {
        await using var scope = services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<KasperDbContext>().Database.MigrateAsync(ct);
    }
}
