using Kasper.Application;
using Kasper.Application.Abstractions;
using Kasper.Application.Contracts;
using Kasper.Infrastructure.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace Kasper.Api.Endpoints;

internal static class CoreEndpoints
{
    private const int MaxMessages = 50;
    private const int MaxMessageChars = 16_000;

    public static void MapCoreEndpoints(this IEndpointRouteBuilder app, string version)
    {
        app.MapGet("/health", () => new HealthDto("ok", version));

        app.MapGet("/ready", async (IEnumerable<IIntegrationHealthCheck> checks, KasperDbContext db, CancellationToken ct) =>
        {
            var components = new List<ComponentStatusDto>
            {
                await db.Database.CanConnectAsync(ct)
                    ? new ComponentStatusDto("Database", true, "SQLite ok")
                    : new ComponentStatusDto("Database", false, "cannot open database"),
            };
            foreach (var check in checks) components.Add(await check.CheckAsync(ct));
            return new ReadyDto(components.All(c => c.Ok), components);
        });

        app.MapPost("/api/ai/chat", async (ChatRequestDto request, IChatModel model, ILoggerFactory lf, CancellationToken ct) =>
        {
            if (request.Messages is not { Count: > 0 and <= MaxMessages } ||
                request.Messages.Any(m => m.Role is not ("user" or "assistant") || string.IsNullOrWhiteSpace(m.Content) || m.Content.Length > MaxMessageChars))
            {
                return Results.BadRequest(new ErrorDto("invalid_request",
                    $"Send 1–{MaxMessages} messages with role 'user' or 'assistant' and up to {MaxMessageChars} characters each."));
            }

            // The system prompt is always ours; callers cannot replace it.
            var messages = new List<ChatMessageDto> { new("system", Prompts.System) };
            messages.AddRange(request.Messages);

            try
            {
                return Results.Ok(await model.ChatAsync(messages, request.Model, ct));
            }
            catch (ChatModelException ex)
            {
                // Log the category only, never prompt content (NFR-032).
                lf.CreateLogger("Kasper.Chat").LogWarning("Chat failed: {Code}", ex.Code);
                return Results.Json(new ErrorDto(ex.Code, ex.Message), statusCode: StatusCodes.Status502BadGateway);
            }
        });

        app.MapPost("/api/app/show", (IDesktopShell shell) =>
        {
            shell.ShowMainWindow();
            return Results.NoContent();
        });
    }
}
