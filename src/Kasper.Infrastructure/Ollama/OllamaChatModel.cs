using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Kasper.Application.Abstractions;
using Kasper.Application.Contracts;
using Kasper.Application.Options;
using Microsoft.Extensions.Options;

namespace Kasper.Infrastructure.Ollama;

/// <summary>Typed adapter for Ollama's /api/chat (non-streaming).</summary>
public sealed class OllamaChatModel(HttpClient http, IOptionsMonitor<OllamaOptions> options) : IChatModel
{
    public async Task<ChatResponseDto> ChatAsync(IReadOnlyList<ChatMessageDto> messages, string? model, CancellationToken ct,
        ChatOptions? chatOptions = null)
    {
        var opts = options.CurrentValue;
        var request = new OllamaChatRequest(
            model ?? opts.Model,
            messages.Select(m => new OllamaMessage(m.Role, m.Content)).ToList(),
            Stream: false,
            opts.KeepAlive,
            chatOptions?.JsonMode == true ? "json" : null,
            new OllamaRequestOptions(opts.ContextLength, chatOptions?.Temperature));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(opts.TimeoutSeconds));
        var sw = Stopwatch.StartNew();

        HttpResponseMessage response;
        try
        {
            response = await http.PostAsJsonAsync(new Uri(new Uri(opts.BaseUrl), "/api/chat"), request, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ChatModelException("model_timeout", $"The model did not answer within {opts.TimeoutSeconds}s.", retryable: true);
        }
        catch (HttpRequestException ex)
        {
            throw new ChatModelException("model_unreachable",
                $"Cannot reach Ollama at {opts.BaseUrl}. Is Ollama running?", retryable: true, ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                var notInstalled = response.StatusCode == HttpStatusCode.NotFound
                    && body.Contains("not found", StringComparison.OrdinalIgnoreCase);
                throw notInstalled
                    ? new ChatModelException("model_not_installed",
                        $"Model '{request.Model}' is not installed. Run: ollama pull {request.Model}", retryable: false)
                    : new ChatModelException("model_error",
                        $"Ollama returned HTTP {(int)response.StatusCode}.", retryable: (int)response.StatusCode >= 500);
            }

            OllamaChatResponse? parsed;
            try
            {
                parsed = await response.Content.ReadFromJsonAsync<OllamaChatResponse>(ct);
            }
            catch (System.Text.Json.JsonException ex)
            {
                throw new ChatModelException("model_bad_response", "Ollama returned malformed JSON.", retryable: false, ex);
            }

            if (parsed?.Message?.Content is not { } content)
                throw new ChatModelException("model_bad_response", "Ollama returned no message.", retryable: false);

            return new ChatResponseDto(content.Trim(), parsed.Model ?? request.Model, sw.Elapsed.TotalSeconds);
        }
    }

    private sealed record OllamaChatRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("messages")] List<OllamaMessage> Messages,
        [property: JsonPropertyName("stream")] bool Stream,
        [property: JsonPropertyName("keep_alive")] string KeepAlive,
        [property: JsonPropertyName("format"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Format,
        [property: JsonPropertyName("options")] OllamaRequestOptions Options);

    private sealed record OllamaRequestOptions(
        [property: JsonPropertyName("num_ctx")] int NumCtx,
        [property: JsonPropertyName("temperature"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Temperature);

    private sealed record OllamaMessage(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string Content);

    private sealed record OllamaChatResponse(
        [property: JsonPropertyName("model")] string? Model,
        [property: JsonPropertyName("message")] OllamaMessage? Message);
}
