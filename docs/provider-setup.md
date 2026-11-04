# Connecting services

All secrets are entered in **Integrations**, verified, then encrypted with Windows DPAPI for your account
(`%LOCALAPPDATA%\KASPER\secrets`). Use test workspaces while you try things out.

## Notion
1. notion.so/profile/integrations → New integration (internal) → copy the secret.
2. Open your task database → ••• → Connections → add the integration.
3. Paste the secret and the database link. Ask: *"add a Notion task to review the Q3 report"*.

## Slack (posts as a KASPER bot)
1. api.slack.com/apps → Create New App → From scratch.
2. Bot Token Scopes: `channels:read`, `channels:history`, `groups:read`, `groups:history`, `chat:write`. Install to workspace.
3. In each channel KASPER may use: `/invite @your-app`.
4. Paste the Bot User OAuth Token (`xoxb-…`) and the allowed channels (e.g. `#team, #updates`).
   Ask: *"summarise #team and post the summary there"*. Each post waits for your approval.

## GitHub (read-only, optional token)
Public repos work without a token (60 requests/hour). For private repos or more requests, create a fine-grained
token with read-only access to the repos and paste it. Link repos to Projects to use them in briefings.

## MCP servers
Add trusted servers to `%LOCALAPPDATA%\KASPER\settings.json` and restart KASPER:

```json
{
  "Mcp": {
    "Servers": [
      {
        "Name": "files",
        "Command": "C:\\Program Files\\nodejs\\npx.cmd",
        "Args": ["-y", "@modelcontextprotocol/server-filesystem", "C:\\Users\\you\\Documents\\Notes"],
        "AllowedTools": ["read_text_file", "list_directory"],
        "ReadOnlyTools": ["read_text_file", "list_directory"]
      }
    ]
  }
}
```

Only `AllowedTools` are exposed. Tools not in `ReadOnlyTools` always go through Final Review.
Status: Settings → MCP tool servers.
