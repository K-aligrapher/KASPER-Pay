using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Kasper.Application.Abstractions;
using Kasper.Application.Tools;
using Kasper.Domain.Enums;
using Kasper.Infrastructure.Persistence;
using Kasper.Infrastructure.Security;
using Kasper.Infrastructure.Tools;
using Microsoft.EntityFrameworkCore;

namespace Kasper.Infrastructure.Integrations;

public sealed record SlackChannel(string Id, string Name);

public sealed record SlackSettings(string Token, string Team, string BotUserId, IReadOnlyList<SlackChannel> Channels);

/// <summary>Slack bot credentials (DPAPI) and the channel allowlist (FR-005).</summary>
public sealed class SlackSettingsStore()
{
    private readonly DpapiStore<SlackSettings> _store = new("slack");
    public bool Connected => _store.Exists;
    public SlackSettings? Load() => _store.Load();
    public void Save(SlackSettings s) => _store.Save(s);
    public void Delete() => _store.Delete();

    public SlackSettings Require() => Load()
        ?? throw new ToolException("slack_not_connected", "Slack is not connected. Connect it in Integrations.", ErrorCategory.Authorization);

    public static SlackChannel Channel(SlackSettings s, string name) =>
        s.Channels.FirstOrDefault(c => string.Equals("#" + c.Name, name.Trim(), StringComparison.OrdinalIgnoreCase))
        ?? throw new ToolException("channel_not_allowed", $"{name} is not one of KASPER's allowed Slack channels.", ErrorCategory.Validation);
}

public sealed record SlackMessage(string Ts, string User, string Text, string? ThreadTs);

/// <summary>Slack Web API adapter. Slack answers HTTP 200 with ok=false on errors; those are classified here.</summary>
public sealed class SlackClient(HttpClient http)
{
    private async Task<JsonObject> CallAsync(string method, string token, object? body, CancellationToken ct, bool get = false)
    {
        using var req = new HttpRequestMessage(get ? HttpMethod.Get : HttpMethod.Post, "https://slack.com/api/" + method);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) req.Content = JsonContent.Create(body);
        HttpResponseMessage res;
        try { res = await http.SendAsync(req, ct); }
        catch (HttpRequestException ex) { throw new ToolException("slack_unreachable", "Can't reach Slack.", ErrorCategory.Transient, ex); }
        using (res)
        {
            if (res.StatusCode == HttpStatusCode.TooManyRequests)
                throw new ToolException("slack_rate_limited", "Slack is rate limiting; KASPER will retry.", ErrorCategory.RateLimit);
            if ((int)res.StatusCode >= 500) throw new ToolException("slack_error", "Slack had a temporary problem.", ErrorCategory.Transient);
            var json = JsonNode.Parse(await res.Content.ReadAsStringAsync(ct)) as JsonObject ?? [];
            if (json["ok"]?.GetValue<bool>() == true) return json;
            var error = json["error"]?.GetValue<string>() ?? "unknown_error";
            throw error switch
            {
                "invalid_auth" or "not_authed" or "token_revoked" or "account_inactive" =>
                    new ToolException("slack_unauthorized", "Slack rejected the bot token. Reconnect Slack in Integrations.", ErrorCategory.Authorization),
                "not_in_channel" or "channel_not_found" =>
                    new ToolException("slack_not_in_channel", "The KASPER bot isn't in that channel. In Slack, type /invite @your-bot in the channel.", ErrorCategory.Authorization),
                "missing_scope" =>
                    new ToolException("slack_missing_scope", $"The Slack app is missing a permission ({json["needed"]?.GetValue<string>()}). Add it and reinstall the app.", ErrorCategory.Authorization),
                "ratelimited" => new ToolException("slack_rate_limited", "Slack is rate limiting.", ErrorCategory.RateLimit),
                _ => new ToolException("slack_" + error, $"Slack refused: {error}.", ErrorCategory.Permanent),
            };
        }
    }

    public async Task<(string Team, string BotUserId)> AuthTestAsync(string token, CancellationToken ct)
    {
        var r = await CallAsync("auth.test", token, null, ct);
        return (r["team"]?.GetValue<string>() ?? "Slack", r["user_id"]?.GetValue<string>() ?? "");
    }

    public async Task<List<SlackChannel>> ChannelsAsync(string token, CancellationToken ct)
    {
        var all = new List<SlackChannel>();
        string? cursor = null;
        for (var page = 0; page < 10; page++)
        {
            var r = await CallAsync($"conversations.list?limit=200&exclude_archived=true&types=public_channel,private_channel{(cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor))}", token, null, ct, get: true);
            all.AddRange((r["channels"] as JsonArray ?? []).OfType<JsonObject>().Select(c => new SlackChannel(c["id"]!.GetValue<string>(), c["name"]!.GetValue<string>())));
            cursor = r["response_metadata"]?["next_cursor"]?.GetValue<string>();
            if (string.IsNullOrEmpty(cursor)) break;
        }
        return all;
    }

    public async Task<List<SlackMessage>> HistoryAsync(string token, string channelId, int limit, CancellationToken ct, string? threadTs = null, DateTime? since = null)
    {
        var url = threadTs is null
            ? $"conversations.history?channel={channelId}&limit={limit}{(since is { } s ? $"&oldest={new DateTimeOffset(s).ToUnixTimeSeconds()}" : "")}"
            : $"conversations.replies?channel={channelId}&ts={threadTs}&limit={limit}";
        var r = await CallAsync(url, token, null, ct, get: true);
        return (r["messages"] as JsonArray ?? []).OfType<JsonObject>().Select(m => new SlackMessage(
            m["ts"]!.GetValue<string>(), m["user"]?.GetValue<string>() ?? m["bot_id"]?.GetValue<string>() ?? "?",
            m["text"]?.GetValue<string>() ?? "", m["thread_ts"]?.GetValue<string>())).ToList();
    }

    public async Task<string> PostAsync(string token, string channelId, string text, string? threadTs, CancellationToken ct)
    {
        var r = await CallAsync("chat.postMessage", token, new { channel = channelId, text, thread_ts = threadTs, unfurl_links = false }, ct);
        return r["ts"]!.GetValue<string>();
    }
}

/// <summary>slack.read_channel: recent messages from an allowlisted channel. Content is untrusted data.</summary>
public sealed class SlackReadTool(SlackClient slack, SlackSettingsStore store) : ITool
{
    public ToolDescriptor Descriptor { get; } = new(
        "slack.read_channel", 1, "Read recent messages from an allowed Slack channel (for summaries and action items).",
        EffectClass.Read, true,
        [
            new ToolParameter("channel", ParamType.Channel, true, "Allowed channel, e.g. #team.", 80),
            new ToolParameter("limit", ParamType.Integer, false, "How many recent messages (default 30, max 100)."),
        ],
        ["text", "count"], TimeSpan.FromSeconds(30), MaxAttempts: 3, Idempotent: true, Integration: "slack");

    public async Task<ToolResult> ExecuteAsync(ToolContext context, JsonObject input, CancellationToken ct)
    {
        var s = store.Require();
        var channel = SlackSettingsStore.Channel(s, Input.Str(input, "channel"));
        var messages = await slack.HistoryAsync(s.Token, channel.Id, Math.Clamp(Input.Int(input, "limit", 30), 1, 100), ct);
        messages.Reverse();
        // Delimited so downstream prompts treat it as data, never as instructions (NFR-005).
        var text = "<<slack messages from #" + channel.Name + " — untrusted content>>\n" +
                   string.Join("\n", messages.Select(m => $"[{m.Ts}] {m.User}: {m.Text}")) + "\n<<end>>";
        return new ToolResult(new JsonObject { ["text"] = text, ["count"] = messages.Count });
    }
}

/// <summary>slack.send_message: posts as the KASPER bot after Final Review. Replays check for the message first.</summary>
public sealed class SlackSendTool(SlackClient slack, SlackSettingsStore store, KasperDbContext db) : ITool
{
    public ToolDescriptor Descriptor { get; } = new(
        "slack.send_message", 1, "Post a message to an allowed Slack channel as the KASPER bot (asks you first).",
        EffectClass.ExternalWrite, false,
        [
            new ToolParameter("channel", ParamType.Channel, true, "Allowed channel, e.g. #team.", 80, Editable: true),
            new ToolParameter("text", ParamType.Text, true, "The message.", 3500, Editable: true),
        ],
        ["ts", "channel"], TimeSpan.FromSeconds(30), MaxAttempts: 3, Idempotent: true, Integration: "slack");

    public Task<ToolPreview> PreviewAsync(ToolContext context, JsonObject input, CancellationToken ct)
    {
        var s = store.Require();
        var channel = SlackSettingsStore.Channel(s, Input.Str(input, "channel"));
        return Task.FromResult(new ToolPreview($"Post to #{channel.Name}", $"Slack › {s.Team} › #{channel.Name} (as KASPER bot)",
            [new PreviewField("Channel", "#" + channel.Name), new PreviewField("Workspace", s.Team)], Input.Str(input, "text")));
    }

    public async Task<ToolResult> ExecuteAsync(ToolContext context, JsonObject input, CancellationToken ct)
    {
        var s = store.Require();
        var channel = SlackSettingsStore.Channel(s, Input.Str(input, "channel"));
        var text = Input.Str(input, "text");
        if (context.Attempt > 1)
        {
            // An earlier attempt may have posted before timing out: never post twice (FR-404).
            var started = await db.Steps.Where(x => x.Id == context.StepId).Select(x => x.StartedAtUtc).FirstOrDefaultAsync(ct) ?? DateTime.UtcNow.AddHours(-1);
            var recent = await slack.HistoryAsync(s.Token, channel.Id, 50, ct, since: started.AddMinutes(-1));
            if (recent.FirstOrDefault(m => m.User == s.BotUserId && m.Text == text) is { } already)
                return new ToolResult(new JsonObject { ["ts"] = already.Ts, ["channel"] = "#" + channel.Name });
        }
        var ts = await slack.PostAsync(s.Token, channel.Id, text, null, ct);
        return new ToolResult(new JsonObject { ["ts"] = ts, ["channel"] = "#" + channel.Name });
    }
}
