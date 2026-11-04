using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Kasper.Application.Tools;
using Kasper.Domain.Enums;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Kasper.Infrastructure.Mcp;

/// <summary>
/// An MCP server the user trusts (NFR-010). Only <see cref="AllowedTools"/> are exposed, and every
/// tool needs Final Review unless listed in <see cref="ReadOnlyTools"/>.
/// </summary>
public sealed class McpServerOptions
{
    public string Name { get; set; } = "";
    public string Command { get; set; } = "";
    public List<string> Args { get; set; } = [];
    public List<string> AllowedTools { get; set; } = [];
    public List<string> ReadOnlyTools { get; set; } = [];
}

public sealed class McpOptions
{
    public const string Section = "Mcp";
    public List<McpServerOptions> Servers { get; set; } = [];
}

/// <summary>Extra tools that come from outside DI (MCP servers).</summary>
public interface IToolSource
{
    IReadOnlyList<ITool> Tools { get; }
}

public sealed record McpServerStatus(string Name, bool Running, int Tools, string? Error);

/// <summary>Minimal MCP stdio client: initialize, tools/list, tools/call (JSON-RPC 2.0, one JSON message per line).</summary>
public sealed partial class McpConnection(McpServerOptions options, ILogger log) : IAsyncDisposable
{
    private Process? _process;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private int _nextId;

    public McpServerOptions Options => options;

    public async Task StartAsync(CancellationToken ct)
    {
        var psi = new ProcessStartInfo(options.Command)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var a in options.Args) psi.ArgumentList.Add(a);
        _process = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {options.Command}.");
        _ = Task.Run(async () =>
        {
            while (await _process.StandardError.ReadLineAsync() is { } line) log.LogDebug("[mcp {Server}] {Line}", options.Name, line);
        });

        await RequestAsync("initialize", new JsonObject
        {
            ["protocolVersion"] = "2025-06-18",
            ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject { ["name"] = "KASPER", ["version"] = "0.1.0" },
        }, ct);
        await WriteAsync(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" }, ct);
    }

    public async Task<JsonArray> ListToolsAsync(CancellationToken ct) =>
        (await RequestAsync("tools/list", new JsonObject(), ct))["tools"] as JsonArray ?? [];

    public async Task<JsonObject> CallAsync(string tool, JsonObject args, CancellationToken ct) =>
        await RequestAsync("tools/call", new JsonObject { ["name"] = tool, ["arguments"] = args }, ct);

    private async Task WriteAsync(JsonObject msg, CancellationToken ct)
    {
        await _process!.StandardInput.WriteLineAsync(msg.ToJsonString().AsMemory(), ct);
        await _process.StandardInput.FlushAsync(ct);
    }

    private async Task<JsonObject> RequestAsync(string method, JsonObject @params, CancellationToken ct)
    {
        if (_process is null || _process.HasExited)
            throw new ToolException("mcp_stopped", $"MCP server '{options.Name}' is not running.", ErrorCategory.Transient);
        await _lock.WaitAsync(ct);
        try
        {
            var id = Interlocked.Increment(ref _nextId);
            await WriteAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = @params }, ct);
            while (true)
            {
                var line = await _process.StandardOutput.ReadLineAsync(ct)
                    ?? throw new ToolException("mcp_stopped", $"MCP server '{options.Name}' closed the connection.", ErrorCategory.Transient);
                JsonObject? msg;
                try { msg = JsonNode.Parse(line) as JsonObject; } catch (JsonException) { continue; }
                if (msg?["id"]?.GetValue<int>() != id) continue; // ignore notifications / other ids
                if (msg["error"] is JsonObject err)
                    throw new ToolException("mcp_error", $"{options.Name}: {err["message"]?.GetValue<string>()}", ErrorCategory.Permanent);
                return msg["result"] as JsonObject ?? [];
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_process is { HasExited: false })
        {
            try { _process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            await _process.WaitForExitAsync();
        }
        _process?.Dispose();
    }

    [GeneratedRegex("[^a-z0-9_]")]
    internal static partial Regex Unsafe();
}

/// <summary>Wraps one allowlisted MCP tool as a KASPER tool (FR-030). Output is untrusted data.</summary>
public sealed class McpTool(McpConnection connection, string toolName, ToolDescriptor descriptor) : ITool
{
    public ToolDescriptor Descriptor => descriptor;

    public Task<ToolPreview> PreviewAsync(ToolContext context, JsonObject input, CancellationToken ct) =>
        Task.FromResult(new ToolPreview($"Run {toolName} on MCP server '{connection.Options.Name}'", $"MCP › {connection.Options.Name} › {toolName}",
            input.Select(kv => new PreviewField(kv.Key, kv.Value?.ToString() ?? "")).ToList()));

    public async Task<ToolResult> ExecuteAsync(ToolContext context, JsonObject input, CancellationToken ct)
    {
        var result = await connection.CallAsync(toolName, (JsonObject)input.DeepClone(), ct);
        var text = string.Join("\n", (result["content"] as JsonArray ?? []).OfType<JsonObject>()
            .Where(c => c["type"]?.GetValue<string>() == "text").Select(c => c["text"]?.GetValue<string>()));
        if (result["isError"]?.GetValue<bool>() == true)
            throw new ToolException("mcp_tool_error", text.Length > 300 ? text[..300] : text, ErrorCategory.Permanent);
        if (text.Length > 20_000) text = text[..20_000] + "…";
        return new ToolResult(new JsonObject { ["text"] = $"<<mcp {connection.Options.Name}/{toolName} — untrusted content>>\n{text}\n<<end>>" });
    }
}

/// <summary>Starts configured MCP servers at launch and exposes their allowlisted tools.</summary>
public sealed class McpHub(IOptions<McpOptions> options, ILogger<McpHub> log) : BackgroundService, IToolSource
{
    private readonly List<ITool> _tools = [];
    private readonly List<McpConnection> _connections = [];
    private readonly List<McpServerStatus> _status = [];

    public IReadOnlyList<ITool> Tools { get { lock (_tools) return _tools.ToList(); } }
    public IReadOnlyList<McpServerStatus> Status { get { lock (_tools) return _status.ToList(); } }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        foreach (var server in options.Value.Servers.Where(s => !string.IsNullOrWhiteSpace(s.Name) && !string.IsNullOrWhiteSpace(s.Command)))
        {
            var conn = new McpConnection(server, log);
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(30));
                await conn.StartAsync(timeout.Token);
                var added = 0;
                foreach (var t in (await conn.ListToolsAsync(timeout.Token)).OfType<JsonObject>())
                {
                    var name = t["name"]?.GetValue<string>() ?? "";
                    if (!server.AllowedTools.Contains(name)) continue; // allowlist only (FR-030)
                    if (Describe(server, name, t) is not { } d) continue;
                    lock (_tools) _tools.Add(new McpTool(conn, name, d));
                    added++;
                }
                _connections.Add(conn);
                lock (_tools) _status.Add(new McpServerStatus(server.Name, true, added, null));
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            {
                log.LogWarning("MCP server {Server} failed to start: {Message}", server.Name, ex.Message);
                lock (_tools) _status.Add(new McpServerStatus(server.Name, false, 0, ex.Message));
                await conn.DisposeAsync();
            }
        }
        try { await Task.Delay(Timeout.Infinite, stoppingToken); } catch (OperationCanceledException) { }
        foreach (var c in _connections) await c.DisposeAsync();
    }

    /// <summary>Maps the tool's JSON schema to KASPER parameters. Tools with unsupported required inputs are skipped.</summary>
    internal static ToolDescriptor? Describe(McpServerOptions server, string name, JsonObject t)
    {
        var schema = t["inputSchema"] as JsonObject ?? [];
        var required = (schema["required"] as JsonArray ?? []).Select(r => r?.GetValue<string>()).ToHashSet();
        var parameters = new List<ToolParameter>();
        foreach (var (prop, def) in schema["properties"] as JsonObject ?? [])
        {
            var type = def?["type"]?.GetValue<string>();
            ParamType? pt = type switch { "string" => ParamType.Text, "integer" => ParamType.Integer, _ => null };
            if (pt is null)
            {
                if (required.Contains(prop)) return null;
                continue;
            }
            parameters.Add(new ToolParameter(prop, pt.Value, required.Contains(prop), def?["description"]?.GetValue<string>() ?? prop, 8000, Editable: pt == ParamType.Text));
        }
        var readOnly = server.ReadOnlyTools.Contains(name);
        var id = $"mcp.{McpConnection.Unsafe().Replace(server.Name.ToLowerInvariant(), "_")}.{McpConnection.Unsafe().Replace(name.ToLowerInvariant(), "_")}";
        return new ToolDescriptor(id, 1, $"[{server.Name}] {t["description"]?.GetValue<string>() ?? name}",
            readOnly ? EffectClass.Read : EffectClass.ExternalWrite, WritesOnlyToStaging: readOnly, parameters, ["text"],
            TimeSpan.FromMinutes(2), MaxAttempts: 1, Idempotent: readOnly);
    }
}
