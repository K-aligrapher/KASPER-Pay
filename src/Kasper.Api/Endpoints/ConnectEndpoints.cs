using Kasper.Application.Abstractions;
using Kasper.Application.Contracts;
using Kasper.Application.Tools;
using Kasper.Domain.Entities;
using Kasper.Infrastructure.Files;
using Kasper.Infrastructure.Integrations;
using Kasper.Infrastructure.Mcp;
using Kasper.Infrastructure.Persistence;
using Kasper.Infrastructure.Runtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Kasper.Api.Endpoints;

public sealed record ConnectSlackDto(string Token, string Channels);
public sealed record ConnectGitHubDto(string Token);

/// <summary>Slack, GitHub, projects, schedules and MCP status (Milestones 5–7).</summary>
internal static class ConnectEndpoints
{
    public static void MapConnectEndpoints(this IEndpointRouteBuilder app)
    {
        // ---- Slack (bot) -----------------------------------------------------------------
        app.MapGet("/api/integrations/slack", (SlackSettingsStore store) =>
            store.Load() is { } s
                ? Results.Ok(new { Connected = true, s.Team, Channels = s.Channels.Select(c => "#" + c.Name) })
                : Results.Ok(new { Connected = false, Team = "", Channels = Array.Empty<string>() }));

        app.MapPost("/api/integrations/slack", (ConnectSlackDto dto, SlackSettingsStore store, SlackClient slack, CancellationToken ct) => Guard(async () =>
        {
            var token = dto.Token?.Trim() ?? "";
            if (!token.StartsWith("xoxb-") || token.Length > 200)
                return Results.BadRequest(new ErrorDto("token_invalid", "Paste the Bot User OAuth Token (starts with xoxb-)."));
            var wanted = (dto.Channels ?? "").Split([',', ' ', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(c => c.TrimStart('#').ToLowerInvariant()).Distinct().ToList();
            if (wanted.Count == 0) return Results.BadRequest(new ErrorDto("channels_required", "List the channels KASPER may use, e.g. #team, #updates."));
            var (team, botId) = await slack.AuthTestAsync(token, ct);
            var all = await slack.ChannelsAsync(token, ct);
            var missing = wanted.Where(w => all.All(c => c.Name != w)).ToList();
            if (missing.Count > 0)
                return Results.BadRequest(new ErrorDto("channels_not_found", $"Not found (or the bot can't see them): {string.Join(", ", missing.Select(m => "#" + m))}. For private channels, /invite the bot first."));
            var channels = all.Where(c => wanted.Contains(c.Name)).ToList();
            store.Save(new SlackSettings(token, team, botId, channels));
            return Results.Ok(new { Connected = true, Team = team, Channels = channels.Select(c => "#" + c.Name) });
        }));

        app.MapDelete("/api/integrations/slack", (SlackSettingsStore store) => { store.Delete(); return Results.NoContent(); });

        // ---- GitHub (read-only; token optional) -------------------------------------------
        app.MapGet("/api/integrations/github", (GitHubSettingsStore store) =>
            Results.Ok(new { Connected = store.Load() is not null, Login = store.Load()?.Login }));

        app.MapPost("/api/integrations/github", (ConnectGitHubDto dto, GitHubSettingsStore store, GitHubClient gh, CancellationToken ct) => Guard(async () =>
        {
            var token = dto.Token?.Trim() ?? "";
            if (token.Length is < 20 or > 255) return Results.BadRequest(new ErrorDto("token_invalid", "Paste a fine-grained, read-only GitHub token."));
            var login = await gh.WhoAmIAsync(token, ct);
            store.Save(new GitHubSettings(token, login));
            return Results.Ok(new { Connected = true, Login = login });
        }));

        app.MapDelete("/api/integrations/github", (GitHubSettingsStore store) => { store.Delete(); return Results.NoContent(); });

        // ---- Projects --------------------------------------------------------------------
        app.MapGet("/api/projects", (ProjectService projects, CancellationToken ct) => Guard(async () => Results.Ok(await projects.ListAsync(ct))));
        app.MapPost("/api/projects", (ProjectDto dto, ProjectService projects, CancellationToken ct) => Guard(async () => Results.Ok(await projects.SaveAsync(null, dto, ct))));
        app.MapPut("/api/projects/{id:guid}", (Guid id, ProjectDto dto, ProjectService projects, CancellationToken ct) => Guard(async () => Results.Ok(await projects.SaveAsync(id, dto, ct))));
        app.MapDelete("/api/projects/{id:guid}", (Guid id, ProjectService projects, CancellationToken ct) => Guard(async () => { await projects.DeleteAsync(id, ct); return Results.NoContent(); }));
        app.MapPost("/api/projects/{id:guid}/briefing", (Guid id, ProjectService projects, CancellationToken ct) => Guard(async () => Results.Ok(await projects.BriefingAsync(id, ct))));

        // ---- Schedules -------------------------------------------------------------------
        app.MapGet("/api/schedules", async (KasperDbContext db, CancellationToken ct) =>
            (await db.Schedules.OrderBy(s => s.TimeOfDay).ToListAsync(ct)).Select(ToDto));

        app.MapPost("/api/schedules", async (ScheduleDto dto, KasperDbContext db, IClock clock, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(dto.Name) || string.IsNullOrWhiteSpace(dto.Request) || dto.Request.Length > 2000)
                return Results.BadRequest(new ErrorDto("schedule_invalid", "Give the schedule a name and a request."));
            if (!TimeOnly.TryParseExact(dto.TimeOfDay, "HH:mm", out _))
                return Results.BadRequest(new ErrorDto("time_invalid", "Time must look like 09:00."));
            var s = new Schedule { Name = dto.Name.Trim(), Request = dto.Request.Trim(), TimeOfDay = dto.TimeOfDay, Days = string.IsNullOrWhiteSpace(dto.Days) ? "daily" : dto.Days, Enabled = dto.Enabled, CreatedAtUtc = clock.UtcNow };
            db.Schedules.Add(s);
            await db.SaveChangesAsync(ct);
            return Results.Ok(ToDto(s));
        });

        app.MapPut("/api/schedules/{id:guid}/enabled", async (Guid id, bool enabled, KasperDbContext db, CancellationToken ct) =>
        {
            if (await db.Schedules.FirstOrDefaultAsync(s => s.Id == id, ct) is not { } s) return Results.NotFound();
            s.Enabled = enabled;
            await db.SaveChangesAsync(ct);
            return Results.Ok(ToDto(s));
        });

        app.MapDelete("/api/schedules/{id:guid}", async (Guid id, KasperDbContext db, CancellationToken ct) =>
        {
            if (await db.Schedules.FirstOrDefaultAsync(s => s.Id == id, ct) is not { } s) return Results.NotFound();
            db.Schedules.Remove(s);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        // ---- MCP -------------------------------------------------------------------------
        app.MapGet("/api/mcp", (McpHub hub) => new
        {
            Servers = hub.Status,
            Tools = hub.Tools.Select(t => new { t.Descriptor.Id, t.Descriptor.Description, t.Descriptor.RequiresApproval }),
        });

        // ---- Send To / drop: look up an uploaded file so the Documents screen can preload it
        app.MapGet("/api/documents/uploads/{token:guid}", (Guid token) =>
        {
            var dir = ArtifactStore.UploadDir(token);
            var file = Directory.Exists(dir) ? Directory.GetFiles(dir).SingleOrDefault() : null;
            if (file is null) return Results.NotFound();
            var name = Path.GetFileName(file);
            return Results.Ok(new UploadDto(token, name, new FileInfo(file).Length,
                Kasper.Infrastructure.Documents.DocumentConverter.TargetsFor(Kasper.Infrastructure.Documents.DocumentConverter.Ext(name))));
        });
    }

    private static ScheduleDto ToDto(Schedule s) => new(s.Id, s.Name, s.Request, s.TimeOfDay, s.Days, s.Enabled, s.LastRunUtc, s.LastWorkflowId);

    private static async Task<IResult> Guard(Func<Task<IResult>> action)
    {
        try { return await action(); }
        catch (WorkflowRequestException ex) { return Results.Json(new ErrorDto(ex.Code, ex.Message), statusCode: ex.Status); }
        catch (ToolException ex) { return Results.BadRequest(new ErrorDto(ex.Code, ex.Message)); }
        catch (ChatModelException ex) { return Results.Json(new ErrorDto(ex.Code, ex.Message), statusCode: 502); }
    }
}
