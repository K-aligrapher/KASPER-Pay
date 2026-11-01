using System.Text.Json.Nodes;
using Kasper.Application.Abstractions;
using Kasper.Application.Contracts;
using Kasper.Application.Planning;
using Kasper.Infrastructure.Documents;
using Kasper.Infrastructure.Files;
using Kasper.Infrastructure.Notion;
using Kasper.Infrastructure.Runtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;

namespace Kasper.Api.Endpoints;

public sealed record DocumentJobDto(string Operation, IReadOnlyList<Guid> Uploads, string? To = null, string? Pages = null,
    int? Degrees = null, string? Quality = null, string? FileName = null, string? Folder = null);

public sealed record UploadDto(Guid Upload, string FileName, long SizeBytes, IReadOnlyList<string> Targets);

public sealed record ConnectNotionDto(string Token, string Database);

internal static class DocumentEndpoints
{
    private const long MaxUploadBytes = 200L * 1024 * 1024;

    public static void MapDocumentEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/documents/capabilities", () => new
        {
            Engines = new[] { "LibreOffice", "Pandoc", "Ghostscript", "QPDF", "Tesseract" }
                .Select(e => new { Name = e, Installed = EngineLocator.Find(e) is not null, Install = EngineLocator.InstallHints[e] }),
        });

        app.MapGet("/api/documents/targets", (string ext) =>
            DocumentConverter.TargetsFor(ext.TrimStart('.').ToLowerInvariant()).Select(t => new { To = t, Lossy = DocumentConverter.IsLossy(ext.TrimStart('.').ToLowerInvariant(), t) }));

        // Files dropped on KASPER are copied into private staging; originals are never touched.
        app.MapPost("/api/documents/upload", async (HttpContext http, CancellationToken ct) =>
        {
            if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit) limit.MaxRequestBodySize = MaxUploadBytes;
            if (!http.Request.HasFormContentType) return Results.BadRequest(new ErrorDto("invalid_upload", "Send files as multipart/form-data."));
            var form = await http.Request.ReadFormAsync(ct);
            var result = new List<UploadDto>();
            foreach (var file in form.Files.Take(30))
            {
                var name = Path.GetFileName(file.FileName);
                if (PlanValidator.FileNameProblem(name) is { } problem)
                    return Results.BadRequest(new ErrorDto("invalid_file", $"{name}: {problem}"));
                var token = Guid.NewGuid();
                var dir = ArtifactStore.UploadDir(token);
                Directory.CreateDirectory(dir);
                await using (var fs = File.Create(Path.Combine(dir, name))) await file.CopyToAsync(fs, ct);
                result.Add(new UploadDto(token, name, file.Length, DocumentConverter.TargetsFor(DocumentConverter.Ext(name))));
            }
            return Results.Ok(result);
        });

        // Quick actions: the server builds the plan (no model involved), then it runs like any workflow.
        app.MapPost("/api/documents/jobs", async (DocumentJobDto job, WorkflowService workflows, FileScope scope, IClock clock, CancellationToken ct) =>
        {
            if (job.Uploads is not { Count: > 0 and <= 12 })
                return Results.BadRequest(new ErrorDto("invalid_job", "Add 1–12 files."));
            await scope.EnsureDefaultAsync(clock.UtcNow, ct);
            var folder = job.Folder ?? FileScope.RefFor((await scope.ListAsync(ct)).First(f => f.IsDefault));

            var steps = new List<PlanStepDto>();
            var n = 0;
            string Next() => $"s{++n}";
            var imports = job.Uploads.Select(u =>
            {
                var key = Next();
                steps.Add(new(key, "files.import", "Bring in the file", new JsonObject { ["upload"] = u.ToString() }));
                return key;
            }).ToList();

            void Commit(string from) => steps.Add(new(Next(), "files.commit", "Save the result",
                new JsonObject { ["artifact"] = $"${from}.artifact", ["folder"] = folder }));
            JsonArray Refs() => new(imports.Select(k => (JsonNode)JsonValue.Create($"${k}.artifact")!).ToArray());

            string summary;
            switch (job.Operation)
            {
                case "convert" when !string.IsNullOrWhiteSpace(job.To):
                    foreach (var k in imports)
                    {
                        var key = Next();
                        steps.Add(new(key, "documents.convert", $"Convert to {job.To}", new JsonObject { ["artifact"] = $"${k}.artifact", ["to"] = job.To }));
                        Commit(key);
                    }
                    summary = $"Convert {Plural(imports.Count)} to {job.To!.ToUpperInvariant()}";
                    break;
                case "merge":
                    var merge = Next();
                    var m = new JsonObject { ["artifacts"] = Refs() };
                    if (job.FileName is { Length: > 0 }) m["fileName"] = job.FileName;
                    steps.Add(new(merge, "pdf.merge", "Merge the PDFs", m));
                    Commit(merge);
                    summary = $"Merge {Plural(imports.Count)} into one PDF";
                    break;
                case "images_to_pdf":
                    var img = Next();
                    steps.Add(new(img, "pdf.from_images", "Put the images into a PDF", new JsonObject { ["artifacts"] = Refs() }));
                    Commit(img);
                    summary = $"Turn {Plural(imports.Count)} into a PDF";
                    break;
                case "extract" or "rotate" or "compress":
                    foreach (var k in imports)
                    {
                        var key = Next();
                        var input = new JsonObject { ["artifact"] = $"${k}.artifact" };
                        var tool = job.Operation switch { "extract" => "pdf.extract_pages", "rotate" => "pdf.rotate", _ => "pdf.compress" };
                        if (job.Operation == "extract") input["pages"] = job.Pages ?? "";
                        if (job.Operation == "rotate") { input["degrees"] = job.Degrees ?? 90; if (job.Pages is { Length: > 0 }) input["pages"] = job.Pages; }
                        if (job.Operation == "compress") input["quality"] = job.Quality ?? "medium";
                        steps.Add(new(key, tool, job.Operation switch { "extract" => $"Keep pages {job.Pages}", "rotate" => "Rotate pages", _ => "Compress" }, input));
                        Commit(key);
                    }
                    summary = job.Operation switch
                    {
                        "extract" => $"Extract pages {job.Pages}",
                        "rotate" => $"Rotate {Plural(imports.Count)}",
                        _ => $"Compress {Plural(imports.Count)}",
                    };
                    break;
                default:
                    return Results.BadRequest(new ErrorDto("invalid_job", "Operation must be convert, merge, images_to_pdf, extract, rotate or compress."));
            }

            try
            {
                var plan = new Plan(summary, steps.Select(s => new PlanStep(s.Id, s.Tool, s.Purpose ?? "", s.Input)).ToList());
                var created = await workflows.CreateAsync($"Document Studio: {summary}", plan, ct, maxSteps: 40);
                return Results.Created($"/api/workflows/{created.Id}", created);
            }
            catch (WorkflowRequestException ex)
            {
                return Results.Json(new ErrorDto(ex.Code, ex.Message, ex.Details), statusCode: ex.Status);
            }
        });

        // ---- Integrations (FR-002/004) --------------------------------------------------------

        app.MapGet("/api/integrations", (NotionSettingsStore notion) =>
        {
            var s = notion.Load();
            return new[]
            {
                new { Name = "notion", Connected = s is not null, Detail = s is null ? "Not connected" : $"Tasks go to “{s.DatabaseTitle}”" },
                new { Name = "slack", Connected = false, Detail = "Coming in Milestone 5" },
            };
        });

        app.MapPost("/api/integrations/notion", async (ConnectNotionDto dto, NotionSettingsStore store, NotionClient client, CancellationToken ct) =>
        {
            var token = dto.Token?.Trim() ?? "";
            if (token.Length is < 20 or > 200) return Results.BadRequest(new ErrorDto("token_invalid", "Paste the integration secret from notion.so/my-integrations."));
            if (NotionSettingsStore.ParseId(dto.Database ?? "") is not { } dbId)
                return Results.BadRequest(new ErrorDto("database_invalid", "Paste the link to your Notion task database."));
            try
            {
                var workspace = await client.WhoAmIAsync(token, ct);
                var (title, titleProp) = await client.DatabaseAsync(token, dbId, ct);
                store.Save(new NotionSettings(token, dbId, title, titleProp));
                return Results.Ok(new { Connected = true, Workspace = workspace, Database = title });
            }
            catch (Kasper.Application.Tools.ToolException ex)
            {
                return Results.BadRequest(new ErrorDto(ex.Code, ex.Message));
            }
        });

        app.MapPost("/api/integrations/notion/test", async (NotionSettingsStore store, NotionClient client, CancellationToken ct) =>
        {
            if (store.Load() is not { } s) return Results.BadRequest(new ErrorDto("notion_not_connected", "Notion is not connected."));
            try
            {
                var (title, _) = await client.DatabaseAsync(s.Token, s.DatabaseId, ct);
                return Results.Ok(new { Ok = true, Database = title });
            }
            catch (Kasper.Application.Tools.ToolException ex)
            {
                return Results.BadRequest(new ErrorDto(ex.Code, ex.Message));
            }
        });

        app.MapDelete("/api/integrations/notion", (NotionSettingsStore store) =>
        {
            store.Delete();
            return Results.NoContent();
        });
    }

    private static string Plural(int n) => n == 1 ? "1 file" : $"{n} files";
}
