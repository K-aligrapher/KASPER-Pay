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
        services.AddSingleton<Documents.DocumentConverter>();
        services.AddSingleton<Notion.NotionSettingsStore>();
        services.AddSingleton<Integrations.SlackSettingsStore>();
        services.AddSingleton<Integrations.GitHubSettingsStore>();
        services.AddSingleton<IIntegrationStatus, Integrations.IntegrationStatus>();
        services.AddSingleton<Security.EphemeralSecrets>();
        services.AddHttpClient<Integrations.SlackClient>(c => c.Timeout = TimeSpan.FromSeconds(30));
        services.AddHttpClient<Integrations.GitHubClient>(c => c.Timeout = TimeSpan.FromSeconds(30));
        services.Configure<Mcp.McpOptions>(config.GetSection(Mcp.McpOptions.Section));
        services.AddSingleton<Mcp.McpHub>();
        services.AddSingleton<Mcp.IToolSource>(sp => sp.GetRequiredService<Mcp.McpHub>());
        services.AddHostedService(sp => sp.GetRequiredService<Mcp.McpHub>());
        services.AddHttpClient<Notion.NotionClient>(c => c.Timeout = TimeSpan.FromSeconds(40));

        // Tools (only these can ever run)
        services.AddScoped<ITool, GenerateTextTool>();
        services.AddScoped<ITool, WriteTextFileTool>();
        services.AddScoped<ITool, CommitFileTool>();
        services.AddScoped<ITool, ImportFileTool>();
        services.AddScoped<ITool, OpenFileTool>();
        services.AddScoped<ITool, ConvertTool>();
        services.AddScoped<ITool, PdfMergeTool>();
        services.AddScoped<ITool, PdfExtractTool>();
        services.AddScoped<ITool, PdfRotateTool>();
        services.AddScoped<ITool, PdfCompressTool>();
        services.AddScoped<ITool, ImagesToPdfTool>();
        services.AddScoped<ITool, PdfStampTool>();
        services.AddScoped<ITool, PdfProtectTool>();
        services.AddScoped<ITool, PdfUnlockTool>();
        services.AddScoped<ITool, OcrTool>();
        services.AddScoped<ITool, PdfToImagesTool>();
        services.AddScoped<ITool, PdfRepairTool>();
        services.AddScoped<ITool, PdfMetadataTool>();
        services.AddScoped<ITool, Integrations.SlackUploadTool>();
        services.AddScoped<ITool, NotionUpdateTaskTool>();
        services.AddScoped<ITool, NotionAppendTool>();
        services.AddScoped<ITool, ProjectAddTaskTool>();
        services.AddScoped<ITool, Integrations.SlackReadTool>();
        services.AddScoped<ITool, Integrations.SlackSendTool>();
        services.AddScoped<ITool, Integrations.GitHubSummaryTool>();
        services.AddScoped<ITool, NotionSearchTool>();
        services.AddScoped<ITool, NotionCreateTaskTool>();
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
        services.AddScoped<ProjectService>();
        services.AddScoped<ProjectTaskService>();
        services.AddSingleton<ScheduleRunner>();
        services.AddHostedService(sp => sp.GetRequiredService<ScheduleRunner>());

        return services;
    }

    public static async Task MigrateDatabaseAsync(this IServiceProvider services, CancellationToken ct = default)
    {
        await using var scope = services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<KasperDbContext>().Database.MigrateAsync(ct);
    }
}
