using Kasper.Application.Abstractions;

namespace Kasper.Infrastructure.Observability;

public sealed class SystemClock : IClock
{
    public DateTime UtcNow => DateTime.UtcNow;
}
