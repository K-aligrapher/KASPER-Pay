using Kasper.Application.Contracts;

namespace Kasper.Application.Abstractions;

public interface IChatModel
{
    Task<ChatResponseDto> ChatAsync(IReadOnlyList<ChatMessageDto> messages, string? model, CancellationToken ct);
}

/// <summary>Model or provider failure, classified so callers can decide whether to retry.</summary>
public sealed class ChatModelException(string code, string message, bool retryable, Exception? inner = null)
    : Exception(message, inner)
{
    public string Code { get; } = code;
    public bool Retryable { get; } = retryable;
}
