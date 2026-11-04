using Kasper.Application.Options;
using Kasper.Application.Tools;
using Kasper.Infrastructure.Mcp;
using Microsoft.Extensions.Options;

namespace Kasper.Infrastructure.Tools;

/// <summary>
/// Only registered tools exist (FR-021): built-in tools plus allowlisted MCP tools. Mock tools are
/// hidden unless enabled, and integration tools only appear once that integration is connected.
/// </summary>
public sealed class ToolRegistry(
    IEnumerable<ITool> tools,
    IOptionsMonitor<RuntimeOptions> options,
    IIntegrationStatus? integrations = null,
    IEnumerable<IToolSource>? sources = null) : IToolRegistry
{
    private readonly Dictionary<string, ITool> _byId = tools
        .Concat(sources?.SelectMany(s => s.Tools) ?? [])
        .GroupBy(t => t.Descriptor.Id, StringComparer.Ordinal)
        .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

    public IReadOnlyList<ToolDescriptor> Available =>
        _byId.Values.Select(t => t.Descriptor)
            .Where(d => !d.DevOnly || options.CurrentValue.EnableMockTools)
            .Where(d => d.Integration is null || integrations?.IsConnected(d.Integration) == true)
            .OrderBy(d => d.Id, StringComparer.Ordinal)
            .ToList();

    public ITool? Find(string toolId) => _byId.GetValueOrDefault(toolId);
}
