using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Kasper.Application.Abstractions;
using Kasper.Application.Contracts;
using Kasper.Application.Options;
using Microsoft.Extensions.Options;

namespace Kasper.Infrastructure.Ollama;

public sealed class OllamaHealthCheck(HttpClient http, IOptionsMonitor<OllamaOptions> options) : IIntegrationHealthCheck
{
    public string Name => "Ollama";

    public async Task<ComponentStatusDto> CheckAsync(CancellationToken ct)
    {
        var opts = options.CurrentValue;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            var tags = await http.GetFromJsonAsync<TagsResponse>(new Uri(new Uri(opts.BaseUrl), "/api/tags"), timeout.Token);
            var installed = tags?.Models?.Any(m =>
                string.Equals(m.Name, opts.Model, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(m.Name, opts.Model + ":latest", StringComparison.OrdinalIgnoreCase)) ?? false;
            return installed
                ? new ComponentStatusDto(Name, true, $"model {opts.Model}")
                : new ComponentStatusDto(Name, false, $"model {opts.Model} not installed (run: ollama pull {opts.Model})");
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException)
        {
            return new ComponentStatusDto(Name, false, $"not reachable at {opts.BaseUrl} (start Ollama)");
        }
    }

    private sealed record TagsResponse([property: JsonPropertyName("models")] List<TagModel>? Models);

    private sealed record TagModel([property: JsonPropertyName("name")] string Name);
}
