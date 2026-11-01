using Kasper.Application.Options;
using Kasper.Application.Tools;
using Microsoft.Extensions.Options;

namespace Kasper.Infrastructure.Tools;

/// <summary>
/// Only registered tools exist (FR-021). Mock tools are hidden unless enabled, and integration
/// tools only appear once that integration is connected.
/// </summary>
public sealed class ToolRegistry(IEnumerable<ITool> tools, IOptionsMonitor<RuntimeOptions> options, IIntegrationStatus? integrations = null) : IToolRegistry
{
    private readonly Dictionary<string, ITool> _byId = tools.ToDictionary(t => t.Descriptor.Id, StringComparer.Ordinal);

    public IReadOnlyList<ToolDescriptor> Available =>
        _byId.Values.Select(t => t.Descriptor)
            .Where(d => !d.DevOnly || options.CurrentValue.EnableMockTools)
            .Where(d => d.Integration is null || integrations?.IsConnected(d.Integration) == true)
            .OrderBy(d => d.Id, StringComparer.Ordinal)
            .ToList();

    public ITool? Find(string toolId) => _byId.GetValueOrDefault(toolId);
}
