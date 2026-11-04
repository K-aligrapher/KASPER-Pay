using Kasper.Application.Tools;
using Kasper.Infrastructure.Notion;

namespace Kasper.Infrastructure.Integrations;

/// <summary>Which integrations are connected; tools of unconnected ones are hidden from the planner.</summary>
public sealed class IntegrationStatus(NotionSettingsStore notion, SlackSettingsStore slack) : IIntegrationStatus
{
    public bool IsConnected(string integration) => integration switch
    {
        "notion" => notion.IsConnected("notion"),
        "slack" => slack.Connected,
        _ => false,
    };
}
