namespace Kasper.Application.Contracts;

public sealed record ChatMessageDto(string Role, string Content);

public sealed record ChatRequestDto(IReadOnlyList<ChatMessageDto> Messages, string? Model = null);

public sealed record ChatResponseDto(string Content, string Model, double DurationSeconds);
