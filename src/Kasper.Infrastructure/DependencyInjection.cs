using Kasper.Application;
using Kasper.Application.Abstractions;
using Kasper.Application.Options;
using Kasper.Application.Planning;
using Kasper.Application.Tools;
using Kasper.Infrastructure.Files;
using Kasper.Infrastructure.Ollama;
using Kasper.Infrastructure.Observability;
using Kasper.Infrastructure.Persistence;
using Kasper.Infrastructure.Planning;
using Kasper.Infrastructure.Runtime;
using Kasper.Infrastructure.Security;
using Kasper.Infrastructure.Tools;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Kasper.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddKasperInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<OllamaOptions>(config.GetSection(OllamaOptions.Section));
        services.Configure<RuntimeOptions>(config.GetSection(RuntimeOptions.Section));

        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<ISessionStore>(_ => new SessionStore());
        services.TryAddSingleton<IDesktopShell, NullDesktopShell>();

        services.AddDbContext<KasperDbContext>(o => o.UseSqlite($"Data Source={KasperPaths.Database}"));
        services.AddScoped<IAuditSink, DbAuditSink>();

        // Timeouts come from OllamaOptions per call, so HttpClient's own timeout is disabled.
        services.AddHttpClient<IChatModel, OllamaChatModel>(c => c.Timeout = Timeout.InfiniteTimeSpan);
        services.AddHttpClient<OllamaHealthCheck>();
        services.AddTransient<IIntegrationHealthCheck>(sp => sp.GetRequiredService<OllamaHealthCheck>());

        // Files
        services.AddScoped<FileScope>();
        services.AddScoped<ArtifactStore>();

        // Tools (only these can ever run)
        services.AddScoped<ITool, GenerateTextTool>();
        services.AddScoped<ITool, WriteTextFileTool>();
        services.AddScoped<ITool, CommitFileTool>();
        services.AddScoped<ITool, MockEchoTool>();
        services.AddScoped<ITool, MockFlakyTool>();
        services.AddScoped<ITool, MockSlowTool>();
        services.AddScoped<ITool, MockExternalPostTool>();
        services.AddScoped<IToolRegistry, ToolRegistry>();

        // Runtime
        services.AddSingleton<IWorkflowEvents, WorkflowEvents>();
        services.AddSingleton<RunningWorkflows>();
        services.AddScoped<WorkflowEngine>();
        services.AddScoped<WorkflowService>();
        services.AddScoped<IPlanner, OllamaPlanner>();
        services.AddScoped<AssistantService>();
        services.AddHostedService<WorkflowWorker>();

        return services;
    }

    public static async Task MigrateDatabaseAsync(this IServiceProvider services, CancellationToken ct = default)
    {
        await using var scope = services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<KasperDbContext>().Database.MigrateAsync(ct);
    }
}
