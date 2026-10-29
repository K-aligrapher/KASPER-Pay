namespace Kasper.Application.Contracts;

public sealed record HealthDto(string Status, string Version);

public sealed record ComponentStatusDto(string Name, bool Ok, string Detail);

public sealed record ReadyDto(bool Ready, IReadOnlyList<ComponentStatusDto> Components);

public sealed record ErrorDto(string Code, string Message);
