using Kasper.Application.Contracts;

namespace Kasper.Application.Abstractions;

/// <summary>Per-call options. JsonMode asks the model to return a single JSON object.</summary>
public sealed record ChatOptions(bool JsonMode = false, double? Temperature = null);

public interface IChatModel
{
    Task<ChatResponseDto> ChatAsync(IReadOnlyList<ChatMessageDto> messages, string? model, CancellationToken ct,
        ChatOptions? options = null);
}

/// <summary>Model or provider failure, classified so callers can decide whether to retry.</summary>
public sealed class ChatModelException(string code, string message, bool retryable, Exception? inner = null)
    : Exception(message, inner)
{
    public string Code { get; } = code;
    public bool Retryable { get; } = retryable;
}
